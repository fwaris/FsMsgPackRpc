module Opt
open CA
open System.IO
open Types
open TorchSharp

let OPT_LOG = root @@ "opt.csv"

let clearLog () = if File.Exists OPT_LOG then File.Delete OPT_LOG

let toFloat basis low hi value =    
    (float hi - float low) / (float low) * basis * (float value)

let toVal = function
    | I(v,l,h) -> toFloat 0.1 l h v
    | F(v,_,_) -> v

let basis = 0.01
let toTParms (ps:float[]) =
    {
        GoodBuyInterReward = ps.[0]  * basis
        BadBuyInterPenalty = ps.[1] * basis
        ImpossibleBuyPenalty = -0.57 //ps.[2] * basis
        GoodSellInterReward = ps.[2] * basis
        BadSellInterPenalty = ps.[3] * basis
        ImpossibleSellPenalty = 0.0 // ps.[5] * basis
        NonInvestmentPenalty = 0.0 //ps.[6] * basis
        Layers = int64 ps.[4]
    }

let caparms = 
    [|                        
        I(1,0,100) //GoodBuyInterReward = 0.01
        I(-1,-100,0) //BadBuyInterPenalty = -0.001
        //I(-5,-100,0) //ImpossibleBuyPenalty = -0.05
        I(1,0,100) //GoodSellInterReward = 0.01
        I(-1,-100,0) //BadSellInterPenalty = - 0.001
        //I(-1,-100,0) //ImpossibleSellPenalty = -0.05
        //I(-1,-100,0) //NonInvestmentPenalty = -0.0101                        
        I(1,1,20) //NonInvestmentPenalty = -0.0101                        
    |]

let optLogger = MailboxProcessor.Start(fun inbox -> 
    async {
        while true do
            let! (gain:float, actDist:List<int*int>, tp:TuneParms) = inbox.Receive()
            let line = $"""{gain},"%A{actDist}",{tp.GoodBuyInterReward},{tp.BadBuyInterPenalty},{tp.ImpossibleBuyPenalty},{tp.GoodSellInterReward},{tp.BadSellInterPenalty},{tp.ImpossibleSellPenalty},{tp.NonInvestmentPenalty},{tp.Layers}"""
            try               
                if File.Exists OPT_LOG |> not then
                    let header = $"""gain,actDist,GoodBuyInterReward,BadBuyInterPenalty,ImpossibleBuyPenalty,GoodSellInterReward,BadSellInterPenalty,ImpossibleSellPenalty,NonInvestmentPenalty,Layers"""
                    File.AppendAllLines(OPT_LOG,[header;line])
                else
                    File.AppendAllLines(OPT_LOG,[line])
            with ex -> 
                printfn $"logger: {ex.Message}"
    })

let runOpt parms = 
    async {
        try 
            parms.DQN.Model.Online.Module.``to``(device) |> ignore
            parms.DQN.Model.Target.Module.``to``(device) |> ignore
            let plcy = Policy.policy parms
            let agent = Train.trainEpisodes parms plcy Data.trainMarkets
            let testGain,testDist = Test.evalModelTT parms.DQN.Model.Online (Test.testMarket())
            printfn $"Gain; {testGain}; Test dist: {testDist}"
            optLogger.Post (testGain,testDist,parms.TuneParms)
            DQN.DQNModel.dispose parms.DQN.Model
            parms.Opt.Value.Dispose()
            let adjGain = 
                if testGain > 0.0 then testGain
                elif testGain = 0.0 then 
                    if testDist.Length > 1 then testGain + (float testDist.Length * 0.001) else testGain
                else 
                    testGain
            return adjGain
        with ex -> 
            printfn "%A" (ex.Message,ex.StackTrace)
            return -0.9999
    }

let mutable _id = 0
let nextId () = System.Threading.Interlocked.Increment &_id

let fopt (parms:float[]) =
    async {
        let tp = toTParms parms
        let baseParms = Model.parms1 (nextId()) (0.001, tp.Layers )                   //every fitness evaluation needs separate optimizer and model
        let baseParms = {baseParms with LogSteps=false; SaveModels=false}
        let optParms = {baseParms with TuneParms = tp}
        let! gain = runOpt optParms
        System.GC.Collect()
        return gain
    }

let optimize() =
    clearLog()
    let fitness ps = fopt ps |> Async.RunSynchronously    
    let mutable step = CALib.API.initCA(caparms, fitness , Maximize)
    for i in 0 .. 15000 do 
        step <- CALib.API.Step step
        //step <- CALib.API.Step(step, maxParallelism=8)
