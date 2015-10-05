
namespace FishSockets

open System
open System.Net
open System.Net.Sockets
open System.Collections.Concurrent
open System.Diagnostics
open SocketExtend

#nowarn "40"

[<Sealed>]
type TcpServer(numChannel0,perBufSize,payloadType : PayloadType,?sendLockOption)=
    // ********************************* primary constructor
    let m_evtClientConnected = new Event<Guid>()
    let m_evtClientDisconnected = new Event<Guid>()
    let m_evtClientError = new Event<Guid*SocketError>()
    let m_evtDataReceived = new Event<Guid*byte[]>()

    let m_sendLockOption = defaultArg sendLockOption SyncOption.Lock
    let m_lsnSocket = NewTcpSocket()
    let m_clients = new ConcurrentDictionary<Guid,TcpRemoteChannel>()
    let mutable m_disposed = false

    let rec m_saeaPool = new SaeaPool(numChannel0,perBufSize,onIoCompleted) 

    and closeClient clientId =
        let found,channel = m_clients.TryRemove clientId
        if found then 
            m_saeaPool.CheckIn channel.EvtArgs
            channel.Close (ChannelCloseFlag.All ^^^ ChannelCloseFlag.Saea)
        found
    
    and onIoCompleted (saea : SocketAsyncEventArgs) =
        match saea.LastOperation with
        | SocketAsyncOperation.Receive -> processReceived saea
        | SocketAsyncOperation.Send -> processSent saea
        | _ -> failwith "only support Receive and Send"

    and processReceived (saea : SocketAsyncEventArgs) =
        let channel = saea.UserToken :?> TcpRemoteChannel

        if saea.SocketError = SocketError.Success && saea.BytesTransferred > 0 then
            channel.HandleReceived(saea.Buffer,saea.Offset,saea.BytesTransferred)
        elif saea.SocketError = SocketError.ConnectionReset || saea.BytesTransferred = 0 then
            if closeClient channel.Id then
                m_evtClientDisconnected.Trigger channel.Id 
        else
            Trace.TraceError("failed to receive from client[{0}] due to: {1}",channel.RemoteEndPnt,saea.SocketError)
            if closeClient channel.Id then
                m_evtClientError.Trigger (channel.Id,saea.SocketError)

    and processSent (saea : SocketAsyncEventArgs) =
        let channel = saea.UserToken :?> TcpRemoteChannel
        match saea.SocketError with
        | SocketError.Success ->
            channel.HandleSent saea.BytesTransferred
        | _ ->
            Trace.TraceError("failed to send to client[{0}] due to: {1}",channel.RemoteEndPnt,saea.SocketError)
            if closeClient channel.Id then
                m_evtClientError.Trigger (channel.Id,saea.SocketError)
            

    let m_acceptSaea = new SocketAsyncEventArgs()
    let rec onAccepted (acceptSaea : SocketAsyncEventArgs) =
        match acceptSaea.LastOperation with
        | SocketAsyncOperation.Accept -> 
            match acceptSaea.SocketError with
            | SocketError.Success -> 
                
                let clientSocket = acceptSaea.AcceptSocket
                acceptSaea.AcceptSocket <- null

                match m_saeaPool.CheckOut 1000 with
                | (false,_) -> 
                    clientSocket.ShutClose()
                    Trace.TraceError("failed to take SAEA from the pool, close the accepted socket")
                | (true,channelArgs)->
                    let clientId = Guid.NewGuid()
                    let payloadStrategy = 
                        PayloadStrategyFactory.makeStrategy payloadType clientId m_evtDataReceived

                    let channel = new TcpRemoteChannel(clientSocket,channelArgs,payloadStrategy,m_sendLockOption)
                    if not (m_clients.TryAdd(clientId,channel)) then
                        m_saeaPool.CheckIn channel.EvtArgs
                        channel.Close (ChannelCloseFlag.All ^^^ ChannelCloseFlag.Saea)
                        failwith "failed to accept client"

                    m_evtClientConnected.Trigger clientId
                    channel.ReceiveAsync()

                m_lsnSocket.AcceptAsyncSafe acceptSaea onAccepted

            | SocketError.OperationAborted ->
                Trace.TraceError("Accepting aborted due to disposed")
            | _ ->
                acceptSaea.ThrowException()
                
        | _ -> failwith "only support Accept"

    do
        m_acceptSaea.Completed.Add onAccepted

    // ********************************* destructor
    interface IDisposable with
        member this.Dispose() =
            try
                m_lsnSocket.Close()
                m_acceptSaea.Dispose()

                m_clients 
                |> Seq.iter (fun kv-> kv.Value.Close ChannelCloseFlag.All)

                (m_saeaPool :> IDisposable).Dispose()
            finally
                m_disposed <- true

    // ********************************* public API
    [<CLIEvent>]
    member this.ClientConnectedEvt = m_evtClientConnected.Publish
    [<CLIEvent>]
    member this.ClientDisconnectedEvt = m_evtClientDisconnected.Publish
    [<CLIEvent>]
    member this.ClientErrorEvt = m_evtClientError.Publish
    [<CLIEvent>]
    member this.DataReceivedEvt = m_evtDataReceived.Publish

    member this.TryGetRemoteClient clientId =
        m_clients.TryGetValue clientId        
               
    member this.Start(lsnPort,?backlog)=
        let lsnEndpnt = new IPEndPoint(IPAddress.Any,lsnPort)
        m_lsnSocket.Bind lsnEndpnt
        m_lsnSocket.Listen <| defaultArg backlog 100

        m_lsnSocket.AcceptAsyncSafe m_acceptSaea onAccepted

    member this.SendAsync(clientId,data,offset,size) =
        let found, channel = m_clients.TryGetValue clientId
        if found then
            channel.SendAsync(data,offset,size)

    member this.Broadcast (data,offset,size) =
        m_clients
        |> Seq.iter (fun kv -> kv.Value.SendAsync(data,offset,size))
           
