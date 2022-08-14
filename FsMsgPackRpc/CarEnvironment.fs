﻿///RL 'environment' for training with the AirSim car simulator
///The agent can perform actions, compute rewards and observe state
module CarEnvironment
open System
open AirSimCar
open TorchSharp

let discreteActions = 6

///state we need for reinforcement learning
type RLState =
    {
        Pose                : torch.Tensor
        Speed               : float
        Collision           : bool
        DepthImage          : torch.Tensor       //depth perspective from front camera transformed
        PrevDepthImage      : torch.Tensor
    }
    with 
        static member Default =
            {
                Pose                = torch.zeros([|3L|],dtype=torch.float)
                DepthImage          = torch.zeros(1,84,84,dtype=torch.float)
                PrevDepthImage      = torch.zeros(1,84,84,dtype=torch.float)
                Speed               = 3.0
                Collision           = false
            }

type DoneReason = LowReward | Collision | Stuck | NotDone

///send the given action to car after translating it to the appropriate control message
let doAction (c:CarClient) action carCtrl (waitMs:int) = 
    let carCtrl = {carCtrl with throttle = 1.0; brake = 0.0}
    async {
        let ctl =
            match action with
            | 0 -> {carCtrl with throttle = 0.0; brake = 1.0}
            | 1 -> {carCtrl with steering = 0.0}
            | 2 -> {carCtrl with steering = 0.5}
            | 3 -> {carCtrl with steering = -0.5}
            | 4 -> {carCtrl with steering = 0.25}
            | _ -> {carCtrl with steering = -0.25}
        do! c.setCarControls(ctl) |> Async.AwaitTask
        do! Async.Sleep waitMs
        return ctl
    }

///cached image request type
let imageRequest : ImageRequest[] = 
    [|
        {
            camera_name = "0"                           //get depth perspective from front camera
            image_type = ImageType.DepthPerspective
            pixels_as_float = true
            compress = false
        }
    |]

//scale camera image and resize to 84x84 
let transformImage (resp:ImageResponse) =
    use t1 = torch.tensor resp.image_data_float
    use t2 = 255.f.ToScalar() / torch.maximum(torch.ones_like t1, t1)  //inverts distance near is higher; max 'nearness' is 255
    use t3 = t2.reshape(resp.height,resp.width,1)
    use t4 = t3.permute(2,0,1)                                         //HWC -> CHW 
    torchvision.transforms.Resize(84,84).forward(t4)

///compute next state from previous state and 
///new observations from the environment
let getObservations (c:CarClient) prevState =
    task {
        let! images = c.simGetImages(imageRequest)
        let img = transformImage( images.[0])
        let! carState = c.getCarState()
        let! collInfo = c.simGetCollisionInfo()
        let nextState =
            {
                Speed           = carState.speed
                Collision       = collInfo.has_collided
                Pose            = carState.kinematics_estimated.position.ToArray() |> torch.tensor
                DepthImage      = img
                PrevDepthImage  = prevState.DepthImage
            }
        return nextState
    }

//points that represent the center points of the road network corners in NED coordinates
let roadPts =
    [
        (0, -1); (130, -1); (130, 125); (0, 125);
        (0, -1); (130, -1); (130, -128); (0, -128);
        (0, -1);        
    ]
    |> List.map (fun (x,y) -> torch.tensor([|float x;float y; 0.0|],dtype=torch.float))

let computeReward (state:RLState) (ctrls:CarControls) =
    let MAX_SPEED = 300.
    let MIN_SPEED = 10.
    let THRESH_DIST = 3.5
    let BETA = 3.

    let car_pt = state.Pose

    //find distance to center line of the nearest road
    let dist =         
        (10_000_000., List.pairwise roadPts)
        ||> List.fold (fun st (a,b) -> 
            use nrm_t = torch.linalg.cross(car_pt - a, car_pt - b)
            let nrm = nrm_t.norm().ToDouble()
            use denom_t  = a - b
            let denom = denom_t.norm().ToDouble()
            let dist' = nrm/denom
            min st dist')
    let reward =
        if dist > THRESH_DIST then
            -3.0
        else    
            let reward_dist = Math.Exp(-BETA * dist) - 0.5
            let reward_speed = (((state.Speed - MIN_SPEED)/(MAX_SPEED - MIN_SPEED)) - 0.5)
            reward_dist + reward_speed
    let isDone =
        //printfn $"d:{dist},r:{reward},b:{ctrls.brake},s:{state.Speed},c:{state.Collision}"
        match reward, ctrls.brake, state.Speed, state.Collision with
        | rwrd,_,_,_ when rwrd < -1.0           -> LowReward //prob off course
        | _,br,sp,_ when br = 0.0 && sp < 0.2   -> Stuck
        | _,_,_,true                            -> Collision
        | _                                     -> NotDone
    reward,isDone

let step c (state,ctrls) action waitMs =
    task{  
        let! ctrls' = doAction c action ctrls waitMs
        let! state' = getObservations c state
        let reward,isDone = computeReward state' ctrls'
        return (state',ctrls',reward,isDone)
    }

let rng = Random()
let randRoadPoint() = 
    let i = rng.Next(roadPts.Length-1)
    let t1 = roadPts.[i] 
    let t2 = roadPts.[i+1]
    use n = torch.linalg.norm(t2 - t1)
    let d2 = rng.NextDouble().ToScalar() * n
    let x1 = t1.[0]
    let x2 = t2.[0]
    let y1 = t1.[1]
    let y2 = t2.[1]
    let intrpltd =
        if x1 = x2 then                       
            if y1.le(y2).ToBoolean() then
                let t = t1.clone()
                t.index_put_(y1+d2,1)
            else
                let t = t2.clone()
                t.index_put_(y2+d2,1)
        else
            if x1.le(x2).ToBoolean() then
                let t = t1.clone()
                t.index_put_(x1+d2,0)
            else
                let t = t2.clone()
                t.index_put_(x2+d2,0)
    t1,t2,intrpltd 

let randPose() =
    let t1,t2,t = randRoadPoint()
    let x1 = t1.[0].ToDouble()
    let x2 = t2.[0].ToDouble()
    let sameX = x1=x2 //moved across y (vertical)
    let zmin,zmax =
        if sameX then
            if rng.NextDouble() < 0.5 then
                1.0,1.0
            else
                -1.0,-1.0
        else
            if rng.NextDouble() < 0.5 then
                -0.01,0.01
            else
                1.0,1.0
    let m = rng.NextDouble()
    let z = zmin + (zmax-zmin) * m        
    //printfn $"{x1},{x2},sameX={sameX};z={z}"
    {
        position = 
            {    
                x_val = t.[0].ToDouble()
                y_val = t.[1].ToDouble()
                z_val = t.[2].ToDouble()
            }
        orientation = 
            {
                w_val = if sameX then 1.0 else 0.0
                x_val = 0.0
                y_val = 0.0
                z_val = z
            }
    }
    

let carId = "PhysXCar"
let initCar (c:CarClient) = 
    task {
        let! _ = c.enableApiControl(true) 
        let! isApi = c.isApiControlEnabled() 
        if isApi then
            let! _ = c.armDisarm(true) 
            do! c.reset()
        else
            return failwith "unable to put car in api mode"
        do! Async.Sleep 100
    }

///an agent that uses random actions in the car environment.
///meant for testing the connectivity to AirSim
let startRandomAgent (go:bool ref) =
    let c = new CarClient(AirSimCar.Defaults.options)
    c.Connect(AirSimCar.Defaults.address,AirSimCar.Defaults.port)
    initCar c |> Async.AwaitTask |> Async.RunSynchronously
    let initState = RLState.Default
    let initCtrls = {CarControls.Default with throttle = 1.0}
    let initAction = 1
    let rng = Random()
    let rec loop state ctrls nextAction =
        async {
            try
                let! (state',ctrls',reward,isDone) = step c (state,ctrls) nextAction 1000 |> Async.AwaitTask
                printfn $"reward: {reward}, isDone: {isDone}"
                if not go.Value then
                    //do! initCar c |> Async.AwaitTask
                    c.Disconnect()
                    printfn "stopped"
                else
                    match isDone with
                        | NotDone -> 
                            let action = rng.Next(discreteActions)
                            return! loop state' ctrls' action
                        | doneRsn ->
                            printfn $"Done: {doneRsn}"                        
                            do! c.reset() |> Async.AwaitTask
                            do! Async.Sleep 1000 // need to wait for the car to settle down after reset
                            return! loop initState initCtrls initAction
            with ex -> printfn "%A" ex.Message
        }
    loop initState initCtrls initAction
