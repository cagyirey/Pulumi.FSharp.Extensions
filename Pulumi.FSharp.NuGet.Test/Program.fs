﻿module Program

open Pulumi.FSharp.Azure.Compute.Inputs
open Pulumi.FSharp.Azure.Compute
open Pulumi.FSharp.Config
open Pulumi.FSharp.Output
open Pulumi.FSharp

let infra () =
    let vm =
        windowsVirtualMachine {
            name "development"
            resourceName "development"
            
            resourceGroup "Development"
            networkInterfaceIds [ config.["vmNicId"] ]
            size "Standard_D4s_v3"
            
            windowsVirtualMachineOsDisk {
                name config.["vmDiskName"]
                caching "ReadWrite"
                storageAccountType "Standard_LRS"
            }
            
            adminUsername config.["vmUser"]
            adminPassword secret.["vmPass"]
            
            windowsVirtualMachineSourceImageReference {
                offer "visualstudio2019latest"
                publisher "microsoftvisualstudio"
                sku "vs-2019-comm-latest-win10-n"
                version "latest"
            }
        }

    let secretValue =
        secretOutput {
            return vm.PublicIpAddress
        }

    let pipCird =
        output {
            let! pip = vm.PublicIpAddress
            
            return pip + "/32"
        }

    dict [ "SecretPublicIP",      secretValue :> obj
           "VisiblePublicIPCIDR", pipCird     :> obj ]

[<EntryPoint>]
let main _ =
  Deployment.run infra
