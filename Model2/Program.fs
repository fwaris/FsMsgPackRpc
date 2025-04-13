﻿module Program
open System
open TorchSharp
open Types

let NUM_MKT_SLICES = Data.TRAIN_SIZE / EPISODE_LENGTH

let trainMarkets =
    let episodes = Data.dataTrain.Length / EPISODE_LENGTH    
    let idxs = [0 .. episodes-1] |> List.map (fun i -> i * EPISODE_LENGTH)
    idxs
    |> List.map(fun i -> 
        let endIdx = i + EPISODE_LENGTH - 1
        if endIdx <= i then failwith $"Invalid index {i}"
        {Market = Data.pricesTrain; StartIndex=i; EndIndex=endIdx})

printfn $"Running with {NUM_MKT_SLICES} market slices each of length {EPISODE_LENGTH} *  ; [left over {Data.TRAIN_SIZE % int NUM_MKT_SLICES}]"

let mutable _ps = Unchecked.defaultof<_>

let startReRun parms = 
    async {
        try 
            let plcy = Policy.policy parms
            let agent = Train.trainEpisodes parms plcy trainMarkets
            _ps <- agent
        with ex -> 
            printfn "%A" (ex.Message,ex.StackTrace)
    }

let restartJobs = 
    Model.parms 
    |> List.map(fun p -> Policy.loadModel p device |> Option.defaultValue p) 
    |> List.map (fun p -> 
        p.DQN.Model.Online.Module.``to`` device |> ignore
        p.DQN.Model.Target.Module.``to`` device |> ignore
        p
    )
    |> List.map startReRun
 
let run() =
    Test.clearModels()
    Data.resetLogs()
    restartJobs |> Async.Parallel |> Async.Ignore |> Async.RunSynchronously

verbosity <- LoggingLevel.L
run()

