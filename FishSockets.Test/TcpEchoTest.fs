
namespace FishSockets.Test

open System
open System.Net
open System.Text
open System.Threading
open System.Collections.Generic
open System.Diagnostics
open TVA
open TVA.IO.Checksums
open FishSockets
open TestHelper

module TcpEchoTest =

    [<Sealed>]
    type private Server(numChannel0,perBufSize,m_payloadtype,sendlock) =
        let m_server = new TcpServer(numChannel0,perBufSize,m_payloadtype,sendlock)
        let m_numClients = ref 0

        let onClientConnected _ =
            let newTotal = Interlocked.Increment m_numClients
            printfn "+++ %d-th client connected" newTotal

        let onClientDisconnected _ =
            let newTotal = Interlocked.Decrement m_numClients
            printfn "--- client disconnected, %d clients left" newTotal

        let onDataReceived(clientId,data) =
            m_server.SendAsync(clientId,data,0,data.Length)
            // printf "."

        do
            m_server.ClientConnectedEvt.Add onClientConnected
            m_server.ClientDisconnectedEvt.Add onClientDisconnected
            m_server.DataReceivedEvt.Add onDataReceived

        interface IDisposable with
            member this.Dispose() =
                (m_server :> IDisposable).Dispose()

        member this.PayloadType = m_payloadtype

        member this.Start lsnPort =
            m_server.Start lsnPort

    type private ClientConfigs = {
        PayloadType : PayloadType
        SendLock: SyncOption
        SvrEndPnt : IPEndPoint
        PerBufSize : int
        MaxSendSize : int
        NumTimes : int
        Interval : int
        MsgPort : int
    }

    [<Sealed>]
    type private ClientProxy(m_configs : ClientConfigs) =
        let m_client = new TcpClient(m_configs.SvrEndPnt,m_configs.PerBufSize,m_configs.PayloadType,m_configs.SendLock)
        let m_endWaitHandle = new ManualResetEvent(false)
        let m_msgProxy = new UdpProxy(1024, SyncOption.NoLock)
        let m_msgEndpnt = new IPEndPoint(IPAddress.Loopback,m_configs.MsgPort)

        member this.Complete (msg : string) =
            let buffer = Encoding.ASCII.GetBytes msg
            m_msgProxy.SendToAsync(m_msgEndpnt,buffer,0,buffer.Length)
            m_endWaitHandle.Set() |> ignore

        member this.Client = m_client

        member this.Run() =
            m_client.ConnectAsync()
            m_endWaitHandle.WaitOne() |> ignore

        interface IDisposable with
            member this.Dispose() =
                (m_client :> IDisposable).Dispose()
                (m_msgProxy :> IDisposable).Dispose()
                m_endWaitHandle.Close()
        
    [<Sealed>]
    type private ConcurrentClient(m_configs : ClientConfigs) =
        let m_proxy = new ClientProxy(m_configs)
        let m_random = newRandom()
        let m_sendchecksum = new CrcCCITT()
        let m_recvchecksum = new CrcCCITT()

        let check() =
            let sendcrc = m_sendchecksum.Value
            let recvcrc = m_recvchecksum.Value

            let isEqual = (recvcrc = sendcrc)
            assert isEqual

            let msg = sprintf "sending CRC=%d, receiving CRC=%d, Equal? %b" sendcrc recvcrc isEqual
            printfn "%s" msg
            m_proxy.Complete msg

        let onConnected() =
            printfn "server connected"
            async {
                for index = 1 to m_configs.NumTimes do
                    let buffer = Array.zeroCreate<byte> <| m_random.Next(1,m_configs.MaxSendSize)
                    m_random.NextBytes buffer

                    m_sendchecksum.Update buffer
                    m_proxy.Client.SendAsync(buffer,0,buffer.Length)

                    printfn "%3.2f%% sent" <| (float index) * 100.0/(float m_configs.NumTimes)
                    do! Async.Sleep m_configs.Interval

                do! Async.Sleep 3000 // wait for the last Receive to complete
                check()
            } |> Async.StartImmediate

        let onDataReceived (_ ,data : byte[]) =
            m_recvchecksum.Update data
            printf "."

        do
            m_proxy.Client.ConnectedEvt.Add onConnected
            m_proxy.Client.DataReceivedEvt.Add onDataReceived

        member this.Run() =
            m_proxy.Run()

        interface IDisposable with
            member this.Dispose() =
                (m_proxy :> IDisposable).Dispose()
             
    type private SequenceClient(m_configs : ClientConfigs) =
        let m_proxy = new ClientProxy(m_configs)
        let m_random = newRandom()
        let m_crc = new CrcCCITT()

        [<VolatileField>]
        let mutable m_index = 0

        [<VolatileField>]
        let mutable m_sendcrc = 0us

        let calculateCRC (buffer : byte[]) =
            m_crc.Update buffer
            let crc = m_crc.Value
            m_crc.Reset()
            crc
        
        let sendNext() =
            let senddata = Array.zeroCreate<byte> <| m_random.Next(1,m_configs.MaxSendSize)
            m_random.NextBytes senddata
            m_sendcrc <- calculateCRC senddata
            m_proxy.Client.SendAsync(senddata,0,senddata.Length)

        // !!! pay attention that, we cannot use "Async.AwaitEvent" to here to write 
        // !!! loop within async{}, because it is possible that
        // !!! "Async.AwaitEvent" runs after the data is received, and miss the event 
        // !!! so we have to register the event before the async-sending, which 
        // !!! guarantee that we can always catch the event, as a result, we have to write 
        // !!! the loop ourself
        let onDataReceived (_,data) =
            m_index <- m_index + 1
            let recvcrc = calculateCRC data
            assert (m_sendcrc = recvcrc)
            printfn "******************[%d]\n\tsend crc=%d\n\trecv crc=%d\n" m_index m_sendcrc recvcrc

            if m_index < m_configs.NumTimes then 
                sendNext()
            else 
                let msg = "COMPLETED"
                m_proxy.Complete msg
                printfn "%s" msg

        do
            m_proxy.Client.ConnectedEvt.Add (fun() -> 
                                            printfn "server connected"
                                            m_index <- 0
                                            sendNext())
            m_proxy.Client.DataReceivedEvt.Add onDataReceived

        member this.Run() =
            m_proxy.Run()

        interface IDisposable with
            member this.Dispose() =
                (m_proxy :> IDisposable).Dispose()

    let private makeServerFromConfigs (configs: Dictionary<string,string>) =
        let payloadtype = getEnum configs "payloadtype"
        let sendlock = getEnum configs "sendlock"
        let perBufSize = getInt configs "serverPerBufSize"
        let numClients0 = getInt configs "numClients0"
        new Server(numClients0,perBufSize,payloadtype,sendlock)

    let private retrieveClientConfigs svraddress svrport (configs: Dictionary<string,string>)=
        {
            PayloadType = getEnum configs "payloadtype"
            SendLock = getEnum configs "sendlock"
            SvrEndPnt = new IPEndPoint((IPAddress.Parse svraddress),svrport)
            PerBufSize = getInt configs "clientPerBufSize"
            MaxSendSize = getInt configs "clientMaxSendSize"
            NumTimes = getInt configs "clientNumTimes"
            Interval = getInt configs "clientInterval"
            MsgPort = getInt configs "msgPort"
        }

    // at the client side, the sending and receiving are concurrent
    // sending and receiving happens at the same time
    let private concurrent_test (configs : Dictionary<string,string>) =
        let svraddress = "127.0.0.1"
        let svrport = 7027

        match configs.["role"] with
        | "server" ->
            use server = makeServerFromConfigs configs
            server.Start svrport
            printfn "server runs on port[%d]" svrport

            // !!! pause()
            // !!! until now, Win8 has bug which causes "Console.ReadLine()" blocks "AcceptAsync"
            // !!! so we have to use "ManualResetEvent" instead
            (new ManualResetEvent(false)).WaitOne() |> ignore

        | "client" ->
            use client = new ConcurrentClient(retrieveClientConfigs svraddress svrport configs)
            client.Run()

        | _ ->
            failwith "unrecognized startup option"

    let private sequence_test (configs : Dictionary<string,string>) =
        let svraddress = "127.0.0.1"
        let svrport = 7027

        match configs.["role"] with
        | "server" ->
            use server = makeServerFromConfigs configs
            assert (server.PayloadType = PayloadType.Boundary)

            server.Start svrport
            printfn "server runs on port[%d]" svrport

            // pause() // Win8 has bug which cause "Console.ReadLine" blocks "AcceptAsync"
            (new ManualResetEvent(false)).WaitOne() |> ignore

        | "client" ->
            let clientConfigs = retrieveClientConfigs svraddress svrport configs
            assert (clientConfigs.PayloadType = PayloadType.Boundary)

            use client = new SequenceClient(clientConfigs)
            client.Run()

        | _ ->
            failwith "unrecognized startup option"
        
    let testmain (args : string[]) =
        let configs = args.[0].ParseKeyValuePairs()
        match configs.["testtype"] with
        | "concurrent" -> 
            concurrent_test configs
        | "sequence" -> 
            sequence_test configs
        | _ ->
            failwith "invalid test type"

