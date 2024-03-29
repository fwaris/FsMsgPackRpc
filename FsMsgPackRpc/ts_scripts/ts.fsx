﻿#load "../scripts/packages.fsx"
#load "../TsData.fs"
#load "../RL.fs"
open System.Threading.Tasks
open TorchSharp
open TorchSharp.Fun
open TsData
open FSharpx.Collections
open RL
open System.IO
open Plotly.NET
open DQN
open System

let device = torch.CUDA
let ACTIONS = 3 //0,1,2 - buy, sell, hold
let ( @@ ) a b = Path.Combine(a,b)
let data_dir = System.Environment.GetEnvironmentVariable("DATA_DRIVE")

let root = data_dir @@ @"s\tradestation"
let fn = root @@ "mes_hist_td.csv"
let fnL = File.ReadLines fn |> Seq.length
let TRAIN_SIZE = float fnL * 0.7 |> int

let loadData() = 
    File.ReadLines fn
    |> Seq.truncate fnL
    |> Seq.map(fun l -> 
        let xs = l.Split(',')
        {
            Time = DateTime.Parse xs.[1]
            Open = float xs.[2]
            High = float xs.[3]
            Low = float xs.[4]
            Close = float xs.[5]
            Volume = float xs.[6]
        }
        )

let dataRaw = loadData()
let data = dataRaw |> Seq.truncate TRAIN_SIZE |> Seq.toArray
let dataTest = dataRaw |> Seq.skip TRAIN_SIZE |> Seq.toArray
dataTest.Length
let mutable verbose = false

//Properties not expected to change over the course of the run (e.g. model, hyperparameters, ...)
//can support multiple concurrent runs
type Parms =
    {
        CreateModel      : unit -> IModel                   //need model creation function so that we can load weights from file
        DQN              : DQN
        LossFn           : Loss<torch.Tensor,torch.Tensor,torch.Tensor>
        Opt              : torch.optim.Optimizer
        LearnEverySteps  : int
        SyncEverySteps   : int
        BatchSize        : int
    }
    with 
        static member Default modelFn ddqn lr = 
            let mps = ddqn.Model.Online.Module.parameters()
            {
                CreateModel     = modelFn
                DQN             = ddqn
                LossFn          = torch.nn.SmoothL1Loss()
                Opt             = torch.optim.Adam(mps, lr=lr)
                LearnEverySteps = 3
                SyncEverySteps  = 1000
                BatchSize       = 32
            }

//keep track of the information we need to run RL in here
type RLState =
    {
        State            : torch.Tensor
        PrevState        : torch.Tensor
        Step             : Step
        InitialCash      : float
        Stock            : int
        CashOnHand       : float
        LookBack         : int64
        ExpBuff          : DQN.ExperienceBuffer
        S_reward         : float
        S_gain           : float
        Episode          : int
    }
    with 
        ///reset for new episode
        static member Reset x = 
            {x with 
                Step            = {x.Step with Num=0}
                CashOnHand      = x.InitialCash
                Stock           = 0
                State           = torch.zeros([|x.LookBack;5L|],dtype=torch.float32)
                PrevState       = torch.zeros([|x.LookBack;5L|],dtype=torch.float32)
            }

        static member Default initExpRate initialCash = 
            let expBuff = {DQN.Buffer=RandomAccessList.empty; DQN.Max=50000}
            let lookback = 40L
            {
                State            = torch.zeros([|lookback;5L|],dtype=torch.float32)
                PrevState        = torch.zeros([|lookback;5L|],dtype=torch.float32)
                Step             = {ExplorationRate = initExpRate; Num=0}
                Stock            = 0
                CashOnHand       = initialCash
                InitialCash      = initialCash
                LookBack         = lookback
                ExpBuff          = expBuff
                S_reward         = -1.0
                S_gain           = -1.0
                Episode          = 0
            }

//environment
type Market = {prices : Bar array}
    with 
        member this.IsDone t = t >= this.prices.Length 

let TX_COST_CNTRCT = 1.0

module Agent = 
    let bar (env:Market) t = if t < env.prices.Length && t >= 0 then env.prices.[t] |> Some else None
    let avgPrice bar = 0.5 * (bar.High + bar.Low)        

    let buy (env:Market) (s:RLState) = 
        bar env s.Step.Num
        |> Option.map (fun bar -> 
            let avgP = avgPrice bar
            let newStock = s.CashOnHand / avgP |> floor
            let cash = if newStock > 0 then s.CashOnHand - (newStock * avgP) else s.CashOnHand
            let stock = s.Stock + (int newStock)
            let cost = newStock * TX_COST_CNTRCT
            {s with CashOnHand=cash-cost; Stock=stock})
        |> Option.defaultValue s

    let sell (env:Market) (s:RLState) =
        bar env s.Step.Num
        |> Option.map (fun bar -> 
            let avgP = avgPrice bar
            let newCash = float s.Stock * avgP + s.CashOnHand
            let cost = float s.Stock * TX_COST_CNTRCT
            {s with CashOnHand=newCash-cost; Stock=0})
        |> Option.defaultValue s

    let doAction _ env s act =
        match act with
        | 0 -> buy env s
        | 1 -> sell env s
        | _ -> s                //hold

    let skipHead = torch.TensorIndex.Slice(1)

    let getObservations _ (env:Market) (s:RLState) =         
        if env.IsDone s.Step.Num then s 
        else                                
            let b =  env.prices.[s.Step.Num]
            let t1 = torch.tensor([|b.Open;b.High;b.Low;b.Close;b.Volume|],dtype=torch.float32)
            let ts = torch.vstack([|s.State;t1|])
            let ts2 = if ts.shape.[0] > s.LookBack then ts.index skipHead else ts  // 40 x 5             
            {s with State = ts2; PrevState = s.State}
        
    let computeRewards parms env s action =         
        bar env s.Step.Num
        |> Option.bind (fun cBar -> bar env (s.Step.Num-1) |> Option.map (fun pBar -> pBar,cBar))
        |> Option.map (fun (prevBar,bar) -> 
            let avgP     = avgPrice  bar            
            let avgPprev = avgPrice prevBar
            let sign     = if action = 0 (*buy*) then 1.0 else -1.0 
            let reward   = (avgP-avgPprev) * sign //* float s.Stock            
            let tPlus1   = s.Step.Num
            let isDone   = env.IsDone (tPlus1 + 1)
            let sGain    = ((avgP * float s.Stock + s.CashOnHand) - s.InitialCash) / s.InitialCash
            if verbose then
                printfn $"{s.Step.Num} - P:%0.3f{avgP}, OnHand:{s.CashOnHand}, S:{s.Stock}, R:{reward}, A:{action}, Exp:{s.Step.ExplorationRate} Gain:{sGain}"
            let experience = {NextState = s.State; Action=action; State = s.PrevState; Reward=float32 reward; Done=isDone }
            let experienceBuff = Experience.append experience s.ExpBuff  
            let step = DQN.updateStep parms.DQN.Exploration s.Step 
            {s with ExpBuff = experienceBuff; Step=step; S_reward=reward; S_gain = sGain},isDone,reward
        )
        |> Option.defaultValue ({s with Step = DQN.updateStep parms.DQN.Exploration s.Step},false,0.0)

    let agent  = 
        {
            doAction = doAction
            getObservations = getObservations
            computeRewards = computeRewards
        }

module Policy =

    let updateQ parms td_estimate td_target =
        use loss = parms.LossFn.forward(td_estimate,td_target)
        parms.Opt.zero_grad()
        loss.backward()
        //torch.nn.utils.clip_grad_value_(model.Online.Module.parameters(),1.0)
        torch.nn.utils.clip_grad_norm_(parms.DQN.Model.Online.Module.parameters(),10.0) |> ignore
        use t = parms.Opt.step() 
        loss.ToDouble()

    let learn parms s = 
        let states,nextStates,rewards,actions,dones = Experience.recall parms.BatchSize s.ExpBuff  //sample from experience
        use states = states.``to``(parms.DQN.Device)
        use nextStates = nextStates.``to``(parms.DQN.Device)
        let td_est = DQN.td_estimate states actions parms.DQN           //estimate the Q-value of state-action pairs from online model
        let td_tgt = DQN.td_target rewards nextStates dones parms.DQN   //
        let loss = updateQ parms td_est td_tgt //update online model         
        if verbose then 
            printfn $"Loss  %0.4f{loss}"
        s        

    let syncModel parms s = 
        System.GC.Collect()
        DQNModel.sync parms.DQN.Model parms.DQN.Device
        let fn = root @@ "models" @@ $"model_{s.Episode}_{s.Step.Num}.bin"
        DQNModel.save fn parms.DQN.Model 
        if verbose then printfn "Synced"

    let rec policy parms = 
        {
            selectAction = fun parms (s:RLState) -> 
                let act =  DQN.selectAction s.State parms.DQN s.Step
                act

            update = fun parms sadrs ->    
                let s = fst sadrs.[0]  //we only have one agent here
                let s = 
                    if s.Step.Num > 0 && s.Step.Num % parms.LearnEverySteps = 0 then    
                        learn parms s
                    else
                        s
                if s.Step.Num > 0 && s.Step.Num % parms.SyncEverySteps = 0 then
                    syncModel parms s
                policy parms, [s]

            sync = syncModel
        }
        
module Test = 
    let interimModel = root @@ "test_ddqn.bin"

    let saveInterim parms =    
        DQN.DQNModel.save interimModel parms.DQN.Model

    let testMarket() = {prices = dataTest}
    let trainMarket() = {prices = data}

    let evalModelTT (model:IModel) market data refLen = 
        let s = RLState.Default 0.0 1_000_000 
        let exp = Exploration.Default
        let lookback = 40
        let dataChunks = data |> Array.windowed lookback
        (*
        let bars = dataChunks.[100]
        let tx = torch.tensor ([|for i in 0 .. 10 -> i|],dtype=torch.float32)
        let tx_d = Tensor.getData<float32> tx
        let tx2 = tx.index Agent.skipHead
        let tx2_d = Tensor.getData<float32> tx2
        let model = (DDQN.DDQNModel.load Policy.createModel @"E:\s\tradestation\models_eval\model_42_57599.bin").Online
        *)
        let modelDevice = model.Module.parameters() |> Seq.head |> fun t -> t.device
        let s' = 
            (s,dataChunks) 
            ||> Array.fold (fun s bars -> 
                let inp = bars |> Array.collect (fun b -> [|b.Open;b.High;b.Low;b.Close;b.Volume|])
                use t_inp = torch.tensor(inp,dtype=torch.float32,dimensions=[|1L;40L;5L|])                
                use t_inp = t_inp.``to``(modelDevice)
                //let t_inp_d = Tensor.getDataNested<float32> t_inp
                //let t_inp_1 = t_inp.index Agent.skipHead
                //let t_inp_1_d = Tensor.getDataNested<float32> t_inp_1
                //let t_inp_2 = t_inp.index [|torch.TensorIndex.Colon; torch.TensorIndex.Slice(1,41)|]
                //let t_inp_2_d = Tensor.getDataNested<float32> t_inp_2
                use q = model.forward t_inp
                let act = q.argmax(-1L).flatten().ToInt32()               
                let s = 
                    //printfn $"act = {act}"
                    if act = 0 then 
                        Agent.buy market s
                    else
                        Agent.sell market s
                //printfn $" {s.TimeStep} act: {act}, cash:{s.CashOnHand}, stock:{s.Stock}"
                {s with Step = DQN.updateStep exp s.Step})

        let avgP1 = Agent.avgPrice (Array.last data)
        let sGain = ((avgP1 * float s'.Stock + s'.CashOnHand) - s'.InitialCash) / s'.InitialCash
        let adjGain = sGain /  float data.Length * float refLen
        adjGain
        //printfn $"model: {modelFile}, gain: {gain}, adjGain: {adjGain}"
        //modelFile,adjGain

    let evalModel (name:string) (model:IModel) =
        try
            model.Module.eval()
            let testMarket,testData = testMarket(), dataTest
            let trainMarket,trainData = trainMarket(), data
            let gainTest = evalModelTT model testMarket testData data.Length
            let gainTrain = evalModelTT model trainMarket trainData data.Length
            printfn $"model: {name}, Adg. Gain -  Test: {gainTest}, Train: {gainTrain}"
            name,gainTest,gainTrain
        finally
            model.Module.train()
    
    let evalModelFile parms modelFile  =
        let model = (DQN.DQNModel.load parms.CreateModel modelFile).Online
        evalModel modelFile model

    let copyModels() =
        let dir = root @@ "models_eval" 
        if Directory.Exists dir |> not then Directory.CreateDirectory dir |> ignore
        dir |> Directory.GetFiles |> Seq.iter File.Delete        
        let dirIn = Path.Combine(root,"models")
        Directory.GetFiles(dirIn,"*.bin")
        |> Seq.map FileInfo
        |> Seq.sortByDescending (fun f->f.CreationTime)
        |> Seq.truncate 50                                  //most recent 50 models
        |> Seq.map (fun f->f.FullName)
        |> Seq.iter (fun f->File.Copy(f,Path.Combine(dir,Path.GetFileName(f)),true))

    let evalModels parms = 
        copyModels()
        let evals = 
            Directory.GetFiles(Path.Combine(root,"models_eval"),"*.bin")
            |> Seq.map (evalModelFile parms)
            |> Seq.toArray
        evals
        |> Seq.map (fun (m,tst,trn) -> tst)
        |> Chart.Histogram
        |> Chart.show
        evals
        |> Seq.map (fun (m,tst,trn) -> trn,tst)
        |> Chart.Point
        |> Chart.withXAxisStyle "Train"
        |> Chart.withYAxisStyle "Test"
        |> Chart.show

    let runTest parms = 
        saveInterim parms
        evalModel interimModel

    let clearModels() = 
        root @@ "models" |> Directory.GetFiles |> Seq.iter File.Delete
        root @@ "models_eval" |> Directory.GetFiles |> Seq.iter File.Delete

let market = {prices = data}

let runEpisode parms (p,s) =
    let rec loop (p,s) =
        if market.IsDone (s.Step.Num + 1) |> not then
            let s_isDone_reward = RL.step parms market Agent.agent p s
            let p,s = p.update parms [s_isDone_reward]
            loop (p,s.[0])
        else
           p,s
    loop (p,s)

let run parms (p,s) =
    let rec loop (p,s:RLState) = 
        if s.Episode < 150 then
            let s = RLState.Reset s
            let p,s = runEpisode parms (p,s)
            printfn $"Run: {s.Episode}, R:{s.S_reward}, E:%0.3f{s.Step.ExplorationRate}; Cash:%0.2f{s.CashOnHand}; Stock:{s.Stock}; Gain:%03f{s.S_gain}; Experienced:{s.ExpBuff.Buffer.Length}"
            Test.evalModel "current" parms.DQN.Model.Online |> ignore
            let s = {s with Episode = s.Episode + 1}
            loop (p,s) 
        else
            printfn "done"
            p,s
    loop (p,s)

let resetRun parms = 
    let p,s = Policy.policy parms, RLState.Default 1.0 1000000.
    run parms (p,s)


let mutable _ps = Unchecked.defaultof<_>

let startResetRun parms =
    async {
        try 
            let ps = resetRun parms
            _ps <- ps
        with ex -> printfn "%A" (ex.Message,ex.StackTrace)    
    }
    |> Async.Start

let startReRun parms = 
    async {
        try 
            let p,s = _ps
            let s = {RLState.Reset s with Episode = 0}
            let ps = run parms (p,s)
            _ps <- ps
        with ex -> printfn "%A" (ex.Message,ex.StackTrace)
    } |> Async.Start

//
let parms1() = 
    let createModel() = 
        torch.nn.Conv1d(40L, 512L, 4L, stride=2L, padding=3L)     //b x 64L x 4L   
        ->> torch.nn.BatchNorm1d(512L)
        ->> torch.nn.Dropout(0.5)
        ->> torch.nn.ReLU()
        ->> torch.nn.Conv1d(512L,64L,3L)
        ->> torch.nn.BatchNorm1d(64L)
        ->> torch.nn.Dropout(0.5)
        ->> torch.nn.ReLU()
        ->> torch.nn.Flatten()
        ->> torch.nn.Linear(128L,20L)
        ->> torch.nn.SELU()
        ->> torch.nn.Linear(20L,int64 ACTIONS)

    let model = DQNModel.create createModel
    let exp = {Decay=0.9995; Min=0.01}
    let ddqn = DQN.create model 0.9999f exp ACTIONS device
    {Parms.Default createModel ddqn 0.00001 with 
        SyncEverySteps = 15000
        BatchSize = 300}

(*
Test.clearModels()
let p1 = parms1()
startResetRun p1
startReRun p1
*)

(*
verbose <- true
verbose <- false

Test.runTest()

async {Test.evalModels p1} |> Async.Start
(fst _ps).sync (snd _ps)

Policy.model.Online.Module.save @"e:/s/tradestation/temp.bin" 

let m2 = DDQN.DDQNModel.load Policy.createModel  @"e:/s/tradestation/temp.bin" 

Policy.model.Online.Module.parameters() |> Seq.iter (printfn "%A")

m2.Online.Module.parameters() |> Seq.iter (printfn "%A")

let p1 = m2.Online.Module.parameters() |> Seq.head |> Tensor.getDataNested<float32>
let p2 = Policy.model.Online.Module.parameters() |> Seq.head |> Tensor.getDataNested<float32>
p1 = p2
*)
