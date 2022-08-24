﻿#load "../scripts/packages.fsx"
#load "../TsData.fs"
#load "../RL.fs"
open System.Threading.Tasks
open TorchSharp
open TorchSharp.Fun
open TsData
open FSharpx.Collections
open RL

let device = torch.CUDA

let fn = @"E:\s\tradestation\mes_5_min.bin"

let data = TsData.loadBars fn 
let d1,d2 = data.[0],Array.last data
let mutable verbose = false

//keep track of all the information we need to run RL in here
type RLState =
    {
        State           : torch.Tensor
        PrevState       : torch.Tensor
        TimeStep        : int
        Stock           : int
        CashOnHand      : float
        InitialCash     : float
        LookBack        : int64
        ExpBuff         : DDQN.ExperienceBuffer
        LearnEvery      : int
        SyncEvery       : int
        S_reward        : float
        S_expRate       : float
        S_gain          : float
    }
    with 
        ///reset for new episode
        static member Reset x = 
            {x with 
                TimeStep=0
                CashOnHand=x.InitialCash
                Stock=0
                State           = torch.zeros([|x.LookBack;5L|],dtype=torch.float32)
                PrevState       = torch.zeros([|x.LookBack;5L|],dtype=torch.float32)
            }

        static member Default initialCash = 
            let expBuff = {DDQN.Buffer=RandomAccessList.empty; DDQN.Max=50000}
            let lookback = 40L
            {
                State           = torch.zeros([|lookback;5L|],dtype=torch.float32)
                PrevState       = torch.zeros([|lookback;5L|],dtype=torch.float32)
                TimeStep        = 0
                Stock           = 0
                CashOnHand      = initialCash
                InitialCash     = initialCash
                LookBack        = lookback
                ExpBuff         = expBuff
                LearnEvery      = 3
                SyncEvery       = 100                
                S_reward        = -1.0
                S_expRate       = -1.0
                S_gain          = -1.0
            }


type Market = {prices : Bar array}
    with 
        member this.IsDone t = t >= this.prices.Length 
        member this.reset() = ()

module Agent = 
    open DDQN
    let bar (env:Market) t = if t < env.prices.Length then env.prices.[t] |> Some else None
    let avgPrice bar = 0.5 * (bar.High + bar.Low)        

    let buy (env:Market) (s:RLState) = 
        bar env s.TimeStep
        |> Option.map (fun bar -> 
            let avgP = avgPrice bar
            let newStock = s.CashOnHand / avgP |> floor
            let cash = if newStock > 0 then s.CashOnHand - (newStock * avgP) else s.CashOnHand
            let stock = s.Stock + (int newStock)
            {s with CashOnHand=cash; Stock=stock})
        |> Option.defaultValue s

    let sell (env:Market) (s:RLState) =
        bar env s.TimeStep
        |> Option.map (fun bar -> 
            let avgP = avgPrice bar
            let newCash = float s.Stock * avgP + s.CashOnHand
            {s with CashOnHand=newCash; Stock=0})
        |> Option.defaultValue s

    let doAction env s act = 
        if act = 0 then buy env s else sell env s      

    let skipHead = torch.TensorIndex.Slice(1L)

    let getObservations (env:Market) (s:RLState) =         
        if env.IsDone s.TimeStep then s 
        else                                
            let b =  env.prices.[s.TimeStep]
            let t1 = torch.tensor([|b.Open;b.High;b.Low;b.Close;b.Volume|],dtype=torch.float32)
            let ts = torch.vstack([|s.State;t1|])
            let ts2 = if ts.shape.[0] > s.LookBack then ts.index skipHead else ts  // 40 x 5 
            {s with State = ts2; PrevState = s.State}
        
    let computeRewards env s action =         
        bar env s.TimeStep
        |> Option.bind (fun b1 -> bar env (s.TimeStep+1) |> Option.map (fun b2 -> b1,b2))
        |> Option.map (fun (bar,nextBar) -> 
            let avgP1 = avgPrice  bar            
            let avgP2 = avgPrice nextBar
            let sign = if action = 0 (*buy*) then 1.0 else -1.0
            let reward = (avgP2-avgP1) * sign * float s.Stock
            let tPlus1 = s.TimeStep + 1
            let isDone = env.IsDone (tPlus1 + 1)
            let sGain = (avgP1 * float s.Stock + s.CashOnHand) / s.InitialCash
            if verbose then
                printfn $"{s.TimeStep} - P:%0.3f{avgP1}, OnHand:{s.CashOnHand}, S:{s.Stock}, R:{reward}, A:{action}, Exp:{s.S_expRate} Gain:{sGain}"
            let experience = {NextState = s.State; Action=action; State = s.PrevState; Reward=float32 reward; Done=isDone }
            let experienceBuff = Experience.append experience s.ExpBuff  
            {s with ExpBuff = experienceBuff; TimeStep=tPlus1; S_reward=reward; S_gain = sGain},isDone,reward
        )
        |> Option.defaultWith (fun _ -> failwith "should not reach here")

    let agent = 
        {
            doAction = doAction
            getObservations = getObservations
            computeRewards = computeRewards
        }

module Policy =
    open DDQN

    let createModel() = 
        torch.nn.Conv1d(40L, 64L, 4L, stride=2L, padding=3L)     //b x 64L x 4L   
        ->> torch.nn.BatchNorm1d(64L)
        ->> torch.nn.Dropout(0.3)
        ->> torch.nn.ReLU()
        ->> torch.nn.Conv1d(64L,64L,3L)
        ->> torch.nn.ReLU()
        ->> torch.nn.Flatten()
        ->> torch.nn.Linear(128L,2L)

    let model = DDQNModel.create createModel
    let lossFn = torch.nn.functional.smooth_l1_loss()

    let exp = {Rate = 1.0; Decay=0.999; Min=0.01}
    let ddqn = DDQN.create model 0.9999f exp 2 device
    let batchSize = 32
    let opt = torch.optim.Adam(model.Online.Module.parameters(), lr=0.00025)

    let updateQ td_estimate td_target =
        use loss = lossFn.Invoke(td_estimate,td_target)
        opt.zero_grad()
        loss.backward()
        use t = opt.step() 
        loss.ToDouble()

    let rec policy ddqn = 
        {
            selectAction  = fun (s:RLState) -> 
                let act,ddqn = DDQN.selectAction s.State ddqn
                (policy ddqn),act

            update = fun (s:RLState) isDone reward ->    
                if s.TimeStep >= int s.LookBack then 
                    if s.TimeStep % s.LearnEvery = 0 then  
                        let states,nextStates,rewards,actions,dones = Experience.recall batchSize s.ExpBuff  //sample from experience
                        use states = states.``to``(ddqn.Device)
                        use nextStates = nextStates.``to``(ddqn.Device)
                        let td_est = DDQN.td_estimate states actions ddqn           //estimate the Q-value of state-action pairs from online model
                        let td_tgt = DDQN.td_target rewards nextStates dones ddqn   //
                        let loss = updateQ td_est td_tgt //update online model 
                        if verbose then 
                            printfn $"Loss  %0.4f{loss}"
                        if s.TimeStep % s.SyncEvery = 0 then                  
                            System.GC.Collect()
                            DDQNModel.sync ddqn.Model ddqn.Device
                            if verbose then
                                printfn "Synced"
                        let s = {s with S_expRate = ddqn.Step.ExplorationRate}
                        policy ddqn,s
                    else
                        policy ddqn,s
                else
                    policy ddqn,s
        }

    let initPolicy() = policy ddqn 
        
let market = {prices = data}

let runEpisode (policy,state) =
    let rec loop (policy,state) =
        if market.IsDone (state.TimeStep + 1) |> not then
            let p,s = RL.step market Agent.agent (policy,state)
            loop (p,s)
        else
           policy,state
    loop (policy,state)

let run() =
    let rec loop (p,s:RLState) count = 
        if count < 1000 then
            let s = RLState.Reset s
            let p,s = runEpisode (p,s)
            printfn $"Run: {count}, R:{s.S_reward}, E:%0.3f{s.S_expRate}; Cash:%0.2f{s.CashOnHand}; Stock:{s.Stock}; Gain:%03f{s.S_gain}; Experienced:{s.ExpBuff.Buffer.Length}"
            loop (p,s) (count+1)
        else
            printfn "done"
    let p,s = Policy.initPolicy(), RLState.Default 1000000.
    loop (p,s) 0

async {try run() with ex -> printfn "%A" (ex.Message,ex.StackTrace)} |> Async.Start


//Agent.bar market 0 |> Option.map Agent.avgPrice
//let s = RLState.Default Policy.expBuff 1000_000.
//let s' = Agent.buy market s
//let s'' = Agent.sell market s'
(*
verbose <- true
verbose <- false
Data*)

