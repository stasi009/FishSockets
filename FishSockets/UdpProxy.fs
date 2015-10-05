
namespace FishSockets

open System
open System.Net
open System.Net.Sockets
open System.Threading
open System.Collections.Concurrent
open System.Diagnostics
open SocketExtend

[<Sealed>]
type UdpProxy(bufSize,?sendLockOption) =
    let m_evtDataReceived = new Event<IPEndPoint*byte[]>()

    let m_sendcontroller = SendControllerFactory.make<IPEndPoint*byte[]*int*int> <| defaultArg sendLockOption SyncOption.Lock
    let m_socket = NewUdpSocket()

    let beginReceive (saea : SocketAsyncEventArgs) callback =
        // since saea.RemoteEndPoint will be published by "m_evtDataReceived" without copying
        // to avoid sharing, I have to assign a new empty IPEndPoint each time before receiving
        saea.RemoteEndPoint <- new IPEndPoint(IPAddress.Any,0)
        m_socket.ReceiveFromAsyncSafe saea callback

    let beginSend (saea : SocketAsyncEventArgs) callback (destEndpnt,buffer,offset,length) =
        let sendsize =
            if length > bufSize then
                Trace.TraceWarning("sending {0} bytes, larger than buffer size({1} bytes), truncate", length,bufSize)
                bufSize
            else
                length

        Buffer.BlockCopy(buffer,offset,saea.Buffer,saea.Offset,sendsize)
        saea.SetBuffer(saea.Offset,sendsize)
        saea.RemoteEndPoint <- destEndpnt

        m_socket.SendToAsyncSafe saea callback

    let rec onIoCompleted (saea: SocketAsyncEventArgs) =
        match saea.LastOperation with
        | SocketAsyncOperation.ReceiveFrom -> processReceiveFrom saea
        | SocketAsyncOperation.SendTo -> processSendTo saea
        | _ -> failwith "only support ReceiveFrom and SendTo"

    and processReceiveFrom (saea : SocketAsyncEventArgs) =
        match saea.SocketError with
        | SocketError.Success ->
            if (saea.BytesTransferred > 0) then
                let cpyBuffer = Array.zeroCreate<byte> saea.BytesTransferred
                Buffer.BlockCopy(saea.Buffer,saea.Offset,cpyBuffer,0,saea.BytesTransferred)
                m_evtDataReceived.Trigger (saea.RemoteEndPoint :?> IPEndPoint,cpyBuffer)

            beginReceive saea onIoCompleted
        | _ ->
            saea.ThrowException()

    and processSendTo (saea: SocketAsyncEventArgs) =
        match saea.SocketError with
        | SocketError.Success ->
            // even it is a partial sending, I CANNOT do anything to fix
            // I CANNOT send again, because the next partial piece will be transported in another new packet
            // since at the receiver side, I have no method implemented to re-assemble different pieces
            // together into a whole packet, so even I send again, it will only confuse the receiver
            // instead doing any help
            let tosend,item = m_sendcontroller.DequeueAndSend()
            if tosend then
                beginSend saea onIoCompleted item
        | _ ->
            saea.ThrowException()

    let m_channelEvtArgs = 
        ChannelEvtArgs.Make bufSize onIoCompleted 1
        |> Seq.head
    
    do
        // if we use the same UDP socket for both reading and writing
        // and if we send packet to inaccessible remote address
        // an IMCP error will cause the next "RecvFrom" failed with WSAECONNRESET (10054)
        // below codes solve such problem
        let IOC_IN = 0x80000000
        let IOC_VENDOR = 0x18000000
        let SIO_UDP_CONNRESET = IOC_IN ||| IOC_VENDOR ||| 12
        let optionInValue = [|Convert.ToByte(false)|]
        let optionOutValue = Array.zeroCreate<byte> 4
        m_socket.IOControl(SIO_UDP_CONNRESET, optionInValue, optionOutValue) |> ignore

    [<CLIEvent>]
    member this.DataReceivedEvt = m_evtDataReceived.Publish

    member this.StartReceive localPort =
        m_socket.Bind <| new IPEndPoint(IPAddress.Any,localPort)
        beginReceive m_channelEvtArgs.RecvSaea onIoCompleted

    member this.SendToAsync (destEndPnt,buffer,offset,size) =
        SocketExtend.checkBuffer buffer offset size

        let tosend,item = m_sendcontroller.EnqueueOrSend(destEndPnt,buffer,offset,size)
        if tosend then
            beginSend m_channelEvtArgs.SendSaea onIoCompleted item

    interface IDisposable with
        member this.Dispose() =
            m_socket.ShutClose()
            (m_channelEvtArgs :> IDisposable).Dispose()

