
namespace FishSockets.Test

open System
open System.Net
open System.Threading
open System.Text
open FishSockets
open TestHelper

module UdpSimpleTest = 

    type private Receiver(bufsize) =
        let m_proxy = new UdpProxy(bufsize)

        // this callback will only be invoked in sequence
        let onDataReceived (srcEndpnt :IPEndPoint,data :byte[])=
            data
            |> Encoding.ASCII.GetString
            |> printfn "<%O>: %s" srcEndpnt

        do
            m_proxy.DataReceivedEvt.Add onDataReceived

        member this.Start localport =
            m_proxy.StartReceive localport

        interface IDisposable with
            member this.Dispose() =
                (m_proxy :> IDisposable).Dispose()

    type private SendConfigs = {
        Prefix : string
        NumTimes : int
        Interval : int
    }

    type private Sender(m_svrEndpnt,bufsize)=
        let m_proxy = new UdpProxy(bufsize)

        member this.Start (configs : SendConfigs) =
            async {
                for index = 1 to configs.NumTimes do

                    let data = 
                        sprintf "%s - %d" configs.Prefix index
                        |> Encoding.ASCII.GetBytes
                    m_proxy.SendToAsync(m_svrEndpnt,data,0,data.Length)
                    printf "."

                    do! Async.Sleep configs.Interval
            } |> Async.StartImmediate

        interface IDisposable with
            member this.Dispose() =
                (m_proxy :> IDisposable).Dispose()

    let testmain (args : string[]) =
        
        let svrEndpnt = new IPEndPoint(IPAddress.Loopback,7027)
        let bufsize = 1024

        match args.[0] with
        | "server" ->
            use receiver = new Receiver(bufsize)
            receiver.Start svrEndpnt.Port
            pause()

        | "client" ->
            let configs = {
                Prefix = args.[1]
                NumTimes = int args.[2]
                Interval = int args.[3]
            }

            use sender = new Sender(svrEndpnt,bufsize)
            sender.Start configs
            pause()

        | _ ->
            failwith "unrecognized startup option"

