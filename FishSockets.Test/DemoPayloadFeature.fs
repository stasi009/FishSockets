
namespace FishSockets.Test

open System
open System.Net
open System.Net.Sockets
open System.Diagnostics
open TVA.IO.Checksums
open FishSockets
open TestHelper

module DemoPayloadFeature =

    [<Sealed>]
    type Server(perBufSize,m_expectCrc,payloadtype) =
        let m_server = new TcpServer(1,perBufSize,payloadtype)     
        let m_crc = new CrcCCITT()
        let mutable m_counter = 0

        let onDataReceived (clientId,data : byte[]) =
            m_counter <- m_counter + 1
            printfn "%d-th %d bytes received" m_counter data.Length
            m_crc.Update(data)

        let onClientDisconnected _ =
            printfn "client disconnected"

            let isEqual = (m_crc.Value = m_expectCrc)
            assert isEqual
            printfn "********* received CRC=%d\n********* expected CRC=%d\n********* Equal?%b" m_crc.Value m_expectCrc isEqual

            m_crc.Reset()

        do
            m_server.DataReceivedEvt.Add onDataReceived
            m_server.ClientConnectedEvt.Add (fun _ -> printfn "new client connected")
            m_server.ClientDisconnectedEvt.Add onClientDisconnected
            m_server.ClientErrorEvt.Add (fun (_,error) -> printfn "client error: %O" error)

        member this.Start lsnPort =
            m_server.Start lsnPort

        interface IDisposable with
            member this.Dispose() =
                (m_server :> IDisposable).Dispose()

    type SendConfigs = {
        Data : byte[]
        NumTimes: int
        Interval: int
    }

    [<Sealed>]
    type Client(svrendpnt,perBufSize,m_configs : SendConfigs,payloadtype) =
        let m_client = new TcpClient(svrendpnt,perBufSize,payloadtype)
        
        let onConnected () =
            printfn "server connected"
            
            async {
                for index = 1 to m_configs.NumTimes do
                    m_client.SendAsync(m_configs.Data,0,m_configs.Data.Length)
                    printfn "[%d] sent" index
                    do! Async.Sleep m_configs.Interval
            } |> Async.StartImmediate

        do
            m_client.ConnectedEvt.Add onConnected

        member this.ConnectAsync() =
            m_client.ConnectAsync()

        interface IDisposable with
            member this.Dispose() =
                (m_client :> IDisposable).Dispose() 


    let testmain (args : string[]) =
        let payloadtype = PayloadType.Stream

        let totalLength = 69745
        let data = Array.init totalLength (fun index -> byte index % Byte.MaxValue)
        let numtimes = 10

        let svraddress = "127.0.0.1"
        let svrport = 7027

        match args.[0] with
        | "server" ->
            let bufsize = 360
            
            let expectedCrc =
                let crc = new CrcCCITT()
                for index = 1 to numtimes do
                    crc.Update data
                crc.Value

            use server = new Server(bufsize,expectedCrc,payloadtype)
            server.Start svrport
            pause()

        | "client" ->
            let configs = {
                Data = data
                NumTimes = numtimes
                Interval = 10
            }
            let bufsize = 1122
            let svrendpnt = new IPEndPoint((IPAddress.Parse svraddress),svrport)
            
            use client = new Client(svrendpnt,bufsize,configs,payloadtype)
            client.ConnectAsync()
            pause() 

        | _ ->
            failwith "unrecognized startup option"

        


