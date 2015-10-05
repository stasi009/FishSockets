

namespace FishSockets.Test

open System
open System.Net
open System.Threading
open System.Diagnostics
open TVA.IO.Checksums
open FishSockets
open TestHelper

module UdpEchoTest =

    [<Sealed>]
    type private Server(bufSize,sendlock) =
        let m_proxy = new UdpProxy(bufSize,sendlock)

        let onDataReceived (srcEndpnt,data) =
            m_proxy.SendToAsync(srcEndpnt,data,0,data.Length)
            // printf "."

        do
            m_proxy.DataReceivedEvt.Add onDataReceived

        member this.Start localport =
            m_proxy.StartReceive localport

        interface IDisposable with
            member this.Dispose() =
                (m_proxy :> IDisposable).Dispose()

    type private ClientConfigs = {
        SvrEndPnt : IPEndPoint
        SendLock : SyncOption
        BufSize : int
        MaxSendSize : int
        NumTimes : int
        Interval : int
    }

    type private ConcurrentClient(m_configs : ClientConfigs) =
        let m_proxy = new UdpProxy(m_configs.BufSize,m_configs.SendLock)
        let m_random = new Random()
        let m_sendcrc = new CrcCCITT()
        let m_recvcrc = new CrcCCITT()

        let onDataReceived (_,data: byte[]) =
            m_recvcrc.Update data
            printf "."

        do
            m_proxy.DataReceivedEvt.Add onDataReceived

        member this.Start localport =
            m_proxy.StartReceive localport

            async {
                for index = 1 to m_configs.NumTimes do
                    let data = Array.zeroCreate<byte> <| m_random.Next(1,m_configs.MaxSendSize)
                    m_random.NextBytes(data)

                    m_sendcrc.Update data
                    m_proxy.SendToAsync(m_configs.SvrEndPnt,data,0,data.Length)

                    printfn "%d-th: %d bytes sent" index data.Length
                    do! Async.Sleep m_configs.Interval
            } |> Async.StartImmediate

        member this.Check() =
            let sendcrc = m_sendcrc.Value
            let recvcrc = m_recvcrc.Value

            let isEqual = (recvcrc = sendcrc)
            assert isEqual
            printfn "########### sending CRC=%d\n########### receiving CRC=%d\n########### Equal? %b" sendcrc recvcrc isEqual

        interface IDisposable with
            member this.Dispose() =
                (m_proxy :> IDisposable).Dispose()

    type private SequenceClient(m_configs : ClientConfigs) =
        let m_proxy = new UdpProxy(m_configs.BufSize)
        let m_random = new Random()
        let m_crc = new CrcCCITT()

        [<VolatileField>]
        let mutable m_index = 0

        [<VolatileField>]
        let mutable m_sendcrc = 0us

        let calculateCRC (data : byte[]) =
            m_crc.Update data
            let crc = m_crc.Value
            m_crc.Reset()
            crc

        // !!! pay attention that, we cannot use "Async.AwaitEvent" to here to write 
        // !!! loop within async{}, because it is possible that
        // !!! "Async.AwaitEvent" runs after the data is received, and miss the event 
        // !!! so we have to register the event before the async-sending, which 
        // !!! guarantee that we can always catch the event, as a result, we have to write 
        // !!! the loop ourself
        let sendNext() =
            m_index <- m_index + 1
            if m_index <= m_configs.NumTimes then
                let senddata = Array.zeroCreate<byte> <| m_random.Next(1,m_configs.MaxSendSize)
                m_random.NextBytes senddata
                m_sendcrc <- calculateCRC senddata
                m_proxy.SendToAsync(m_configs.SvrEndPnt,senddata,0,senddata.Length)
            else
                printfn "##################### COMPLETED #####################"

        let onDataReceived (_,data) =
            let recvcrc = calculateCRC data
            assert (m_sendcrc = recvcrc)
            printfn "******************[%d]\n\tsend crc=%d\n\trecv crc=%d\n" m_index m_sendcrc recvcrc
            sendNext()

        do
            m_proxy.DataReceivedEvt.Add onDataReceived

        member this.Start localport =
            m_proxy.StartReceive localport
            sendNext()

        interface IDisposable with
            member this.Dispose() =
                (m_proxy :> IDisposable).Dispose()

    let private concurrent_test (args: string[]) =

        let svraddress = "127.0.0.1"
        let svrport = 7027
        let bufsize = 4096
        let sendlock = parseLockOption args.[1]

        match args.[0] with
        | "server" ->
            use server = new Server(bufsize,sendlock)
            server.Start svrport
            pause()

        | "client" ->
            let localport = int args.[2]

            let configs = {
                SvrEndPnt = new IPEndPoint(IPAddress.Parse(svraddress),svrport)
                SendLock = sendlock
                BufSize = bufsize
                MaxSendSize = bufsize-1
                NumTimes = int args.[3]
                Interval = int args.[4]
            }
            
            use client = new ConcurrentClient(configs)
            client.Start localport

            pause()
            client.Check()
        | _ ->
            failwith "unrecognized startup option"

    let private sequence_test (args: string[]) =

        let svraddress = "127.0.0.1"
        let svrport = 7027
        let bufsize = 4096
        let sendlock = parseLockOption args.[1]

        match args.[0] with
        | "server" ->
            use server = new Server(bufsize,sendlock)
            server.Start svrport
            pause()

        | "client" ->
            let localport = int args.[2]

            let configs = {
                SvrEndPnt = new IPEndPoint(IPAddress.Parse(svraddress),svrport)
                SendLock = sendlock
                BufSize = bufsize
                MaxSendSize = bufsize-1
                NumTimes = int args.[3]
                Interval = -1 // we don't need to sleep in this test
            }
            
            use client = new SequenceClient(configs)
            client.Start localport
            pause()
        | _ ->
            failwith "unrecognized startup option"

    let testmain args =
        concurrent_test args
        // sequence_test args
        
        

