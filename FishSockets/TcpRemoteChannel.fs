
namespace FishSockets

open System
open System.Net
open System.Net.Sockets
open System.Threading
open SocketExtend

[<Flags>]
type ChannelCloseFlag =
    | Nil       = 0b00000000
    | Saea      = 0b00000001
    | Socket    = 0b00000010
    | All       = 0b00000011

[<AllowNullLiteral>]
[<Sealed>]
type TcpRemoteChannel(m_socket : Socket, m_evtArgs: ChannelEvtArgs, m_payloadStrategy : IPayloadStrategy,sendSyncOption : SyncOption) as this=
    let m_remoteEndPnt = m_socket.RemoteEndPoint :?> IPEndPoint

    // ----------------- for sending
    let m_sendcontroller : ISendController<byte[]*int*int> = 
        match sendSyncOption with
        | SyncOption.Aggregate -> new AggregateSendController() :> ISendController<byte[]*int*int>
        | _ -> SendControllerFactory.make<byte[]*int*int> sendSyncOption

    [<VolatileField>]
    let mutable m_sendingBuffer : byte[] = null

    [<VolatileField>]
    let mutable m_sendingOffset = 0

    [<VolatileField>]
    let mutable m_sendingTotal = 0

    let sendNextPiece() =
        let sendSaea = m_evtArgs.SendSaea

        let pieceSize = min m_sendingTotal m_evtArgs.MaxBufSize
        Buffer.BlockCopy(m_sendingBuffer,m_sendingOffset,sendSaea.Buffer,sendSaea.Offset,pieceSize)
        sendSaea.SetBuffer(sendSaea.Offset,pieceSize)

        m_socket.SendAsyncSafe sendSaea m_evtArgs.Callback

    let beginSend( buffer,offset,size) =
        m_sendingBuffer <- buffer
        m_sendingOffset <- offset
        m_sendingTotal <- size
        sendNextPiece()

    // ----------------- 
    do
        m_evtArgs.RecvSaea.UserToken <- this
        m_evtArgs.SendSaea.UserToken <- this

    member this.Id = m_payloadStrategy.Id
    member this.EvtArgs = m_evtArgs
    member this.RemoteEndPnt = m_remoteEndPnt

    // ############################### for receiving
    member this.ReceiveAsync() =
        m_socket.ReceiveAsyncSafe m_evtArgs.RecvSaea m_evtArgs.Callback

    member this.HandleReceived(buffer: byte[],offset:int,size:int)=
        m_payloadStrategy.ProcessReceived(buffer,offset,size)
        this.ReceiveAsync()

    // ############################### for sending
    member this.SendAsync(buffer : byte[],offset,size) =
        SocketExtend.checkBuffer buffer offset size

        let tosend, item = 
            (buffer,offset,size)
            |> m_payloadStrategy.PreSending
            |> m_sendcontroller.EnqueueOrSend
        
        if tosend then
            beginSend item

    member this.HandleSent(bytesSent : int) =
        if bytesSent <= 0 then
            // no matter how, it should always send next
            // otherwise, the "asynchronous loop" will break
            // causing the sending queue grow infinitely
            failwith <| sprintf "invalid %d bytes sent" bytesSent

        m_sendingOffset <- m_sendingOffset + bytesSent
        m_sendingTotal <- m_sendingTotal -  bytesSent 
        
        if m_sendingTotal > 0 then
            sendNextPiece()

        elif m_sendingTotal = 0 then
            let tosend, item = m_sendcontroller.DequeueAndSend()
            if tosend then
                beginSend item

        else
            failwith "impossible negative sending total"

    // ############################### dispose
    member this.Close(flag : ChannelCloseFlag) =
        if flag &&& ChannelCloseFlag.Socket <> ChannelCloseFlag.Nil then
            m_socket.ShutClose()

        (m_payloadStrategy :> IDisposable).Dispose()

        if flag &&& ChannelCloseFlag.Saea <> ChannelCloseFlag.Nil then
            (m_evtArgs :> IDisposable).Dispose()
        
    interface IDisposable with
        member this.Dispose() =
            this.Close(ChannelCloseFlag.All ^^^ ChannelCloseFlag.Saea)
            
