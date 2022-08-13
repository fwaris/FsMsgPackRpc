﻿//#load "packages.fsx"
open AirSimCar
open TorchSharp
open TorchSharp.Fun
open System.IO
open DDQN

let root = System.Environment.GetEnvironmentVariable("AIRSIM_DDQN")
let (@@) a b = Path.Combine(a,b)

//ddqn pytorch model
let createModel () = 
    torch.nn.Conv2d(1L,32L,8L,stride=4L)
    ->> torch.nn.ReLU()
    ->> torch.nn.Conv2d(32L,64L,4L,stride=2L)
    ->> torch.nn.ReLU()
    ->> torch.nn.Conv2d(64L,64L,3L,stride=1L)
    ->> torch.nn.ReLU()
    ->> torch.nn.Flatten()
    ->> torch.nn.Linear(3136L,512L)
    ->> torch.nn.ReLU()
    ->> torch.nn.Linear(512L,CarEnvironment.discreteActions)

let modelFile = root @@ "ddqn_airsim.bin"
let exprFile = root @@ "expr_buff_airsim.bin"
let model = 
    if File.Exists modelFile then         //restart session
        DDQNModel.load createModel modelFile
    else
        DDQNModel.create createModel

(*
*)
