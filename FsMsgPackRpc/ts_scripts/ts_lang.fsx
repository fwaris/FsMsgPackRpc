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
open FSharp.Collections.ParallelSeq
open SeqUtils

type LoggingLevel = Q | L | M | H 
    with  
        member this.IsLow = match this with L | M | H -> true | _ -> false
        member this.isHigh = match this with H -> true | _ -> false
        member this.IsMed = match this with M | H -> true | _ -> false

let mutable verbosity = LoggingLevel.H

let device = if torch.cuda_is_available() then torch.CUDA else torch.CPU
let ACTIONS = 3 //0,1,2 - buy, sell, hold
let ( @@ ) a b = Path.Combine(a,b)
let data_dir = System.Environment.GetEnvironmentVariable("DATA_DRIVE")

let root = data_dir @@ @"s\tradestation"
let fn = root @@ "mes_hist_td.csv"
let fnL = File.ReadLines fn |> Seq.filter (fun l -> String.IsNullOrWhiteSpace l |> not) |> Seq.length
let TRAIN_SIZE = float fnL * 0.7 |> int
let LOOKBACK = 40L

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
        })
    |> Seq.pairwise
    |> Seq.map (fun (a,b) ->
        {b with 
            Open = log(b.Open/a.Open)
            High = log(b.High/a.High)
            Low = log(b.Low/a.Low)
            Close = log(b.Close/a.Close)
            Volume = log(b.Volume/a.Volume)
        }
    )
     
let dataRaw = loadData()
let data = dataRaw |> Seq.truncate TRAIN_SIZE |> Seq.toArray
let dataTest = dataRaw |> Seq.skip TRAIN_SIZE |> Seq.toArray
dataTest.Length

let trainSets = 
    let chks = data |> Array.chunkBySize (data.Length / 10)
    let ls = chks.Length    
    let last = Array.append chks.[ls-2] chks.[ls-1]        //combine last two chunks
    Array.append chks.[0..ls-3] [|last|]

trainSets |> Array.iteri(fun  i t -> printfn $"t {i} length {t.Length}")

//Properties not expected to change over the course of the run (e.g. model, hyperparameters, ...)
//can support multiple concurrent runs
type Parms =
    {
        LearnRate        : float
        CreateModel      : unit -> IModel                   //need model creation function so that we can load weights from file
        DQN             : DQN
        LossFn           : Loss<torch.Tensor,torch.Tensor,torch.Tensor>
        Opt              : torch.optim.Optimizer
        LearnEverySteps  : int
        SyncEverySteps   : int
        BatchSize        : int
        Epochs           : int
        RunId            : int
    }
    with 
        static member Default modelFn ddqn lr id = 
            let mps = ddqn.Model.Online.Module.parameters()
            {
                LearnRate       = lr
                CreateModel     = modelFn
                DQN             = ddqn
                LossFn          = torch.nn.SmoothL1Loss()
                Opt             = torch.optim.Adam(mps, lr=lr)
                LearnEverySteps = 3
                SyncEverySteps  = 1000
                BatchSize       = 32
                Epochs          = 6
                RunId           = id
                
            }

//keep track of the information we need to run RL in here
type RLState =
    {
        AgentId          : int
        State            : torch.Tensor
        PrevState        : torch.Tensor
        Step             : Step
        InitialCash      : float
        Stock            : float
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
            let a = 
                {x with 
                    Step            = {x.Step with Num=0} //keep current exploration rate; just update step number
                    CashOnHand      = x.InitialCash
                    Stock           = 0
                    State           = torch.zeros([|x.LookBack;5L|],dtype=Nullable torch.float32)
                    PrevState       = torch.zeros([|x.LookBack;5L|],dtype=Nullable torch.float32)
                }            
            if verbosity.IsLow then 
                printfn  $"Reset called {x.AgentId} x={x.Step.ExplorationRate} a={a.Step.ExplorationRate}"
            a

        static member Default agentId initExpRate initialCash = 
            let expBuff = {DQN.Buffer=RandomAccessList.empty; DQN.Max=50000}
            let lookback = 40L
            {
                AgentId          = agentId
                State            = torch.zeros([|lookback;5L|],dtype=Nullable torch.float32)
                PrevState        = torch.zeros([|lookback;5L|],dtype=Nullable torch.float32)
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
let MAX_TRADE_SIZE = 50.

module Agent = 
    open DQN
    let bar (env:Market) t = if t < env.prices.Length && t >= 0 then env.prices.[t] |> Some else None
    let avgPrice bar = 0.5 * (bar.High + bar.Low)        

    let buy (env:Market) (s:RLState) = 
        bar env s.Step.Num
        |> Option.map (fun bar -> 
            let avgP = avgPrice bar
            let priceWithCost = avgP + TX_COST_CNTRCT
            let stockToBuy = s.CashOnHand / priceWithCost |> floor |> max 0. |> min MAX_TRADE_SIZE
            let outlay = stockToBuy * priceWithCost
            let coh = s.CashOnHand - outlay |> max 0.            
            let stock = s.Stock + stockToBuy 
            assert (stock >= 0.)
            {s with CashOnHand=coh; Stock=stock})
        |> Option.defaultValue s

    let sell (env:Market) (s:RLState) =
        bar env s.Step.Num
        |> Option.map (fun bar -> 
            let avgP = avgPrice bar
            let priceWithCost = avgP - TX_COST_CNTRCT
            let stockToSell = s.Stock |> min MAX_TRADE_SIZE
            let inlay = stockToSell * priceWithCost
            let coh = s.CashOnHand + inlay
            let remStock = s.Stock - stockToSell |> max 0.
            {s with CashOnHand=coh; Stock=remStock})
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
            if verbosity.isHigh then
                printfn $"{s.AgentId}-{s.Step.Num} - P:%0.3f{avgP}, OnHand:{s.CashOnHand}, S:{s.Stock}, R:{reward}, A:{action}, Exp:%0.3f{s.Step.ExplorationRate} Gain:%0.2f{sGain}"
            let experience = {NextState = s.State; Action=action; State = s.PrevState; Reward=float32 reward; Done=isDone }
            let experienceBuff = Experience.append experience s.ExpBuff  
            {s with ExpBuff = experienceBuff; S_reward=reward; S_gain = sGain},isDone,reward
        )
        |> Option.defaultValue (s,false,0.0)

    let agent  = 
        {
            doAction = doAction
            getObservations = getObservations
            computeRewards = computeRewards
        }

module Policy =

    let updateQ parms (losses:torch.Tensor []) =        
        parms.Opt.zero_grad()
        let losseD = losses |> Array.map (fun l -> l.backward(); l.ToDouble())
        torch.nn.utils.clip_grad_norm_(parms.DQN.Model.Online.Module.parameters(),25.) |> ignore
        use t = parms.Opt.step() 
        losseD |> Array.average

    let loss parms s = 
        let states,nextStates,rewards,actions,dones = Experience.recall parms.BatchSize s.ExpBuff  //sample from experience
        use states = states.``to``(parms.DQN.Device)
        use nextStates = nextStates.``to``(parms.DQN.Device)
        let td_est = DQN.td_estimate states actions parms.DQN           //estimate the Q-value of state-action pairs from online model
        let td_tgt = DQN.td_target rewards nextStates dones parms.DQN   //
        let loss = parms.LossFn.forward(td_est,td_tgt)
        loss

    let syncModel parms s = 
        System.GC.Collect()
        DQNModel.sync parms.DQN.Model parms.DQN.Device
        let fn = root @@ "models" @@ $"model_{parms.RunId}_{s.Episode}_{s.Step.Num}.bin"
        DQNModel.save fn parms.DQN.Model 
        if verbosity.IsLow then printfn "Synced"

    let rec policy parms = 
        {
            selectAction = fun parms (s:RLState) -> 
                let act =  DQN.selectAction s.State parms.DQN s.Step
                act

            update = fun parms sdrs  ->      
                let losses = sdrs |> PSeq.map (fun (s,_) -> loss parms s) |> PSeq.toArray
                let avgLoss = updateQ parms losses
                let avgGain = sdrs |> PSeq.map (fun (s,_) -> s.S_gain) |> PSeq.average
                let episode = sdrs |> List.map (fun (s,_) -> s.Episode) |> List.max
                let step = sdrs |> List.map(fun (s,_) -> s.Step.Num) |> List.max
                if verbosity.IsMed then printfn $"{episode}/{step} avg gain: %0.3f{avgGain}, avg loss: %0.3f{avgLoss}"
                let s0,_ = sdrs.[0]
                if s0.Step.Num % parms.SyncEverySteps = 0 then
                    syncModel parms s0
                let rs = sdrs |> List.map fst
                policy parms, rs

            sync = syncModel
        }
        
module Test = 
    let interimModel = root @@ "test_DQN.bin"

    let saveInterim parms =    
        DQN.DQNModel.save interimModel parms.DQN.Model

    let testMarket() = {prices = dataTest}
    let trainMarket() = {prices = data}

    let evalModelTT (model:IModel) market data refLen = 
        let s = RLState.Default -1 0.0 1_000_000 
        let exp = Exploration.Default
        let lookback = 40
        let dataChunks = data |> Array.windowed lookback
        let modelDevice = model.Module.parameters() |> Seq.head |> fun t -> t.device
        let s' = 
            (s,dataChunks) 
            ||> Array.fold (fun s bars -> 
                let inp = bars |> Array.collect (fun b -> [|b.Open;b.High;b.Low;b.Close;b.Volume|])
                use t_inp = torch.tensor(inp,dtype=torch.float32,dimensions=[|1L;40L;5L|])                
                use t_inp = t_inp.``to``(modelDevice)
                use q = model.forward t_inp
                let act = q.argmax(-1L).flatten().ToInt32()               
                let s = 
                    match act with
                    | 0 -> Agent.buy market s
                    | 1 -> Agent.sell market s
                    | _ -> s
                //printfn $" {s.TimeStep} act: {act}, cash:{s.CashOnHand}, stock:{s.Stock}"
                {s with Step = DQN.updateStep exp s.Step})

        let avgP1 = Agent.avgPrice (Array.last data)
        let sGain = ((avgP1 * float s'.Stock + s'.CashOnHand) - s'.InitialCash) / s'.InitialCash
        let adjGain = sGain /  float data.Length * float refLen
        adjGain
        //printfn $"model: {modelFile}, gain: {gain}, adjGain: {adjGain}"
        //modelFile,adjGain

    let evalModel parms (name:string) (model:IModel) =
        try
            model.Module.eval()
            let testMarket,testData = testMarket(), dataTest
            let trainMarket,trainData = trainMarket(), data
            let gainTest = evalModelTT model testMarket testData data.Length
            let gainTrain = evalModelTT model trainMarket trainData data.Length
            printfn $"model: {parms.RunId} {name}, Adg. Gain -  Test: {gainTest}, Train: {gainTrain}"
            name,gainTest,gainTrain
        finally
            model.Module.train()
    
    let evalModelFile parms modelFile  =
        let model = (DQN.DQNModel.load parms.CreateModel modelFile).Online
        evalModel parms modelFile model

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
        evalModel parms interimModel

    let clearModels() = 
        root @@ "models" |> Directory.GetFiles |> Seq.iter File.Delete
        root @@ "models_eval" |> Directory.GetFiles |> Seq.iter File.Delete

let markets = trainSets |> Array.map (fun brs -> {prices=brs})

let acctBlown (s:RLState) = s.CashOnHand < 10000.0 && s.Stock <= 0
let isDone (m:Market,s) = m.IsDone (s.Step.Num+1) || acctBlown s

let processAgent parms plcy (m,s) = 
    if isDone (m,s) then 
        (m,s),((0,true,0.),false) //skip
    else
        let s',adr = step parms m Agent.agent plcy s                           
        let s'' = {s' with Step = DQN.updateStep parms.DQN.Exploration s'.Step} // update step number and exploration rate for each agent
        (m,s''),(adr,true)
    
let runEpisodes  parms plcy (ms:(Market*RLState) list) =
    let rec loop ms =
        let ms' = ms |> PSeq.map (processAgent parms plcy) |> PSeq.toList // operate agents in parallel
        let processed =
            ms'
            |> List.filter (fun (_,(_,t)) -> t)
            |> List.map (fun ((m,s),(adr,_)) -> (m,s),adr)
        if List.isEmpty processed |> not then                       //if at least 1 ageant is not done 
            let s0 = processed.[0] |> fst |> snd
            if s0.Step.Num > 0 &&  s0.Step.Num % parms.LearnEverySteps = 0 then
                let sdrs = processed |> List.map (fun ((m,s),adr) -> s,adr)
                plcy.update parms sdrs |> ignore
            loop (ms' |> List.map fst)
        else
            ms' |> List.map fst
    loop ms

let mutable _ps = Unchecked.defaultof<_>

let runAgents parms p ms = 
    (ms,[1..parms.Epochs])
    ||> List.fold(fun ms i ->
        let ms' = runEpisodes  parms p ms
        printfn $"run {parms.RunId} {i} done"
        Test.evalModel parms "current" parms.DQN.Model.Online |> ignore
        let ms'' = ms' |> List.map (fun (m,s) -> m, RLState.Reset s)
        ms'')

let startResetRun parms =
    async {
        try 
            let p = Policy.policy parms
            let ms = markets |> Seq.mapi(fun i m -> m,RLState.Default i 1.0 1000000) |> Seq.toList
            let ps = runAgents parms p ms
            _ps <- ps
        with ex -> printfn "%A" (ex.Message,ex.StackTrace)    
    }

let startReRun parms = 
    async {
        try 
            let p = Policy.policy parms
            let ms = _ps |> List.map (fun (m,s) ->m, {RLState.Reset s with Episode = 0})
            let ps = runAgents parms p ms
            _ps <- ps
        with ex -> printfn "%A" (ex.Message,ex.StackTrace)
    }


let parms1 id lr  = 
    let emsize = 64
    let dropout = 0.1
    let max_seq = LOOKBACK
    let nheads = 4
    let nlayers = 2L

    let createModel() = 
        let proj = torch.nn.Linear(5L,emsize)
        let ln = torch.nn.LayerNorm(emsize)
        let pos_encoder = PositionalEncoder.create dropout emsize max_seq
        let encoder_layer = torch.nn.TransformerEncoderLayer(emsize,nheads,emsize,dropout)
        let transformer_encoder = torch.nn.TransformerEncoder(encoder_layer,nlayers)
        let sqrtEmbSz = (sqrt (float emsize)).ToScalar()
        let projOut = torch.nn.Linear(emsize,ACTIONS)

        let initRange = 0.1

        let mdl = 
            F [] [proj; pos_encoder; transformer_encoder; projOut; ln]  (fun t -> //B x S x 5
                use p1 = proj.forward(t) // B x S x emsize
                use p = ln.forward(p1)
                use pB2 = p.permute(1,0,2) //batch second - S x B x emsize
                use mask = Masks.generateSubsequentMask (t.size().[1]) t.device // S x S
                use src = pos_encoder.forward(pB2 * sqrtEmbSz) //S x B x emsize
                use enc = transformer_encoder.forward(src,mask) //S x B x emsize
                use encB = enc.permute(1,0,2)  //batch first  // B x S x emsize
                use dec = encB.[``:``,LAST,``:``]    //keep last value as output to compare with target - B x emsize
                let pout = projOut.forward(dec) //B x ACTIONS
                pout
            )

        mdl

    let model = DQNModel.create createModel
    let exp = {Decay=0.9995; Min=0.01}
    let DQN = DQN.create model 0.9999f exp ACTIONS device
    {Parms.Default createModel DQN lr id with 
        SyncEverySteps = 15000
        BatchSize = 32
        Epochs = 78}


let lrs = [0.00001]///; 0.0001; 0.0002; 0.00001]
let parms = lrs |> List.mapi (fun i lr -> parms1 i lr)
let jobs = parms |> List.map (fun x -> startResetRun x) 

(*
Test.clearModels()
jobs |> Async.Parallel |> Async.Ignore |> Async.Start

jobs |> Async.Parallel |> Async.Ignore |> Async.RunSynchronously

*)

(*
verbosity <- LoggingLevel.H
verbosity <- LoggingLevel.M
verbosity <- LoggingLevel.L
verbosity <- LoggingLevel.Q

Test.runTest()

async {Test.evalModels p1} |> Async.Start
(fst
 _ps).sync (snd _ps)

Policy.model.Online.Module.save @"e:/s/tradestation/temp.bin" 

let m2 = DQN.DQNModel.load Policy.createModel  @"e:/s/tradestation/temp.bin" 

Policy.model.Online.Module.parameters() |> Seq.iter (printfn "%A")

m2.Online.Module.parameters() |> Seq.iter (printfn "%A")

let p1 = m2.Online.Module.parameters() |> Seq.head |> Tensor.getDataNested<float32>
let p2 = Policy.model.Online.Module.parameters() |> Seq.head |> Tensor.getDataNested<float32>
p1 = p2
*)


