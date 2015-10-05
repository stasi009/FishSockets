
namespace FishSockets.Test

open System
open System.Net
open System.Text
open System.Threading
open System.Diagnostics

open TVA
open TVA.IO.Checksums

open FishSockets
open TestHelper

module SendLockPerfTests =

    type private ClientConfigs = {
        SendLock : SyncOption
        SvrEndPnt : IPEndPoint
        PerBufSize : int
        DataSize : int
    }

    [<Sealed>]
    type private PeriodReporter()=
        // --------------------------- member fields
        let _evtMsg = new Event<string>()
        let _bytesPerM = float (1024 * 1024)
        let _bytesReceived = ref 0
        let mutable _cts : CancellationTokenSource = null

        // --------------------------- member fields
        member this.MsgEvt = _evtMsg.Publish

        member this.Increase numRecved =
            Interlocked.Add(_bytesReceived,numRecved) |> ignore

        member this.Start() =
            if _cts <> null then
                _cts.Cancel()
            _cts <- new CancellationTokenSource()

            let job = async {
                while true do
                    let lastValue = Interlocked.Exchange(_bytesReceived,0)

                    (float lastValue)/_bytesPerM
                    |> sprintf "[%3.2f]M received"
                    |> _evtMsg.Trigger

                    do! Async.Sleep 1000
            } 

            Async.StartImmediate(job,_cts.Token)

    [<Sealed>]
    type private Server(numClients0,perBufSize,sendlock,msgport) =
        let m_server = new TcpServer(numClients0,perBufSize,PayloadType.Stream,sendlock)
        let m_reporter = new PeriodReporter()
        let m_msgproxy = new UdpProxy(1024)
        let m_msgendpnt = new IPEndPoint(IPAddress.Loopback,msgport)

        let onDataReceived (clientId,data : byte[]) =
            m_server.SendAsync(clientId,data,0,data.Length)
            m_reporter.Increase data.Length

        let onNewMsg (newMsg : string) =
            let buffer = Encoding.ASCII.GetBytes newMsg
            m_msgproxy.SendToAsync(m_msgendpnt,buffer,0,buffer.Length)

        do
            m_server.ClientConnectedEvt.Add (fun _ -> printfn "new client connected")
            m_server.ClientDisconnectedEvt.Add (fun _ -> printfn "client disconnected")
            m_server.DataReceivedEvt.Add onDataReceived
            m_reporter.MsgEvt.Add onNewMsg

        member this.Start lsnport =
            m_server.Start lsnport
            m_reporter.Start()

        interface IDisposable with
            member this.Dispose() =
                (m_server :> IDisposable).Dispose()
                (m_msgproxy :> IDisposable).Dispose()

    [<Sealed>]
    type private Client (m_configs : ClientConfigs) =
        let m_client = new TcpClient(m_configs.SvrEndPnt,m_configs.PerBufSize,PayloadType.Stream,m_configs.SendLock)
        let m_reporter = new PeriodReporter()

        let onConnected() =
            let data0 = 
                let buffer = Array.zeroCreate<byte> m_configs.DataSize
                let random = new Random()
                random.NextBytes buffer
                buffer
            m_client.SendAsync(data0,0,data0.Length)
            m_reporter.Start()

        let onDataReceived (_,data : byte[]) =
            m_client.SendAsync(data,0,data.Length)
            m_reporter.Increase data.Length

        do
            m_client.ConnectedEvt.Add onConnected
            m_client.DataReceivedEvt.Add onDataReceived
            m_reporter.MsgEvt.Add (printfn "%s")

        member this.Start() =
            m_client.ConnectAsync()

        interface IDisposable with
            member this.Dispose() =
                (m_client :> IDisposable).Dispose()

    let testmain (args : string[]) =
        let configs = args.[0].ParseKeyValuePairs()

        let sendlock : SyncOption = getEnum configs "sendlock"
        printfn "############# LockOption: %O #############" sendlock
        
        let perBufSize = getInt configs "perBufSize"
        let svrport = 7027

        match configs.["role"] with
        | "server"->
            let numClients = getInt configs "numClients"
            let msgPort = getInt configs "msgPort"

            use server = new Server(numClients,perBufSize,sendlock,msgPort)
            server.Start svrport
            wait4exit()

        | "client" ->
            let configs = {
                SendLock = sendlock
                SvrEndPnt = new IPEndPoint(IPAddress.Parse("127.0.0.1"),svrport)
                PerBufSize = perBufSize
                DataSize = getInt configs "dataSize"
            }
            use client = new Client(configs)
            client.Start()
            wait4exit()

        | _ ->
            failwith "invalid startup option"
        
        
                

    

    
    

