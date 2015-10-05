
namespace FishSockets

open System
open System.Net
open System.Net.Sockets
open System.Diagnostics
open SocketExtend

[<Sealed>]
type TcpConnector(m_socket :Socket,svrEndpnt : IPEndPoint) =

    let m_evtEstablished = new Event<unit>()
    let m_evtTerminated = new Event<unit>()
    // if return true, then try connecting again
    // otherwise, stop trying, abandon the connection
    let mutable m_onDisconnected = new Func<bool>(fun() -> true)

    let m_connectSaea = new SocketAsyncEventArgs(RemoteEndPoint = svrEndpnt,DisconnectReuseSocket =true)

    let rec handleConnection (saea : SocketAsyncEventArgs) =
        match saea.LastOperation with
        | SocketAsyncOperation.Connect -> 
            processConnect saea
        | SocketAsyncOperation.Disconnect -> 
            processDisconnect saea
        | _ ->
            failwith "only support Connect and Disconnect"

    and reconnect (saea : SocketAsyncEventArgs) (sleeptime : int) =
        if (m_onDisconnected.Invoke()) then
            async {
                Trace.TraceWarning("try connecting again after {0}ms",sleeptime)
                do! Async.Sleep sleeptime
                m_socket.ConnectAsyncSafe saea handleConnection
            } |> Async.StartImmediate
        else
            m_evtTerminated.Trigger()
        
    and processConnect saea =
        match saea.SocketError with
        | SocketError.Success ->
            m_evtEstablished.Trigger ()

        | SocketError.ConnectionRefused
        | SocketError.TimedOut ->
            Trace.TraceWarning("connection failed due to '{0}'",saea.SocketError)
            reconnect saea 1000

        | _ ->
            Trace.TraceError("connection failed due to '{0}'",saea.SocketError)
            saea.ThrowException()

    and processDisconnect saea =
        match saea.SocketError with
        | SocketError.Success ->
            Trace.TraceWarning("disconnected with server")
            reconnect saea 1000
        | _ ->
            Trace.TraceError("disconnection failed due to '{0}'",saea.SocketError)
            saea.ThrowException()

    do
        m_connectSaea.Completed.Add handleConnection

    interface IDisposable with
        member this.Dispose() =
            m_connectSaea.Dispose()

    [<CLIEvent>]
    member this.EstablishedEvt = m_evtEstablished.Publish
    [<CLIEvent>]
    member this.TerminatedEvt = m_evtTerminated.Publish
    member this.DisconnectedCallback 
        with set newvalue = m_onDisconnected <- newvalue

    member this.ConnectAsync() =
        m_socket.ConnectAsyncSafe m_connectSaea handleConnection

    member this.DisconnectAsync() =
        m_socket.DisconnectAsyncSafe m_connectSaea handleConnection
    
[<Sealed>]
type TcpClient(svrEndpnt : IPEndPoint,bufSize, payloadType : PayloadType,?sendLockOption) =
    // ***************************** primary constructor
    let m_evtConnected = new Event<unit>()
    let m_evtDataReceived = new Event<Guid*byte[]>()

    let m_sendLockOption = defaultArg sendLockOption SyncOption.Lock
    let m_socket = NewTcpSocket()
    let m_connector = new TcpConnector(m_socket,svrEndpnt)

    [<VolatileField>]
    let mutable m_channel : TcpRemoteChannel = null

    let rec onIoCompleted (saea : SocketAsyncEventArgs) =
        match saea.LastOperation with
        | SocketAsyncOperation.Receive -> processReceived saea
        | SocketAsyncOperation.Send -> processSent saea
        | _ -> failwith "only support Receive and Send"

    and processReceived (saea : SocketAsyncEventArgs) =
        let channel = saea.UserToken :?> TcpRemoteChannel
        
        if saea.SocketError = SocketError.Success && saea.BytesTransferred > 0 then
            channel.HandleReceived(saea.Buffer,saea.Offset,saea.BytesTransferred)
        elif saea.SocketError = SocketError.ConnectionReset || saea.BytesTransferred = 0 then
            // dispose the SAEA (construct new ones when connected), but reuse the socket
            m_channel.Close (ChannelCloseFlag.All ^^^ ChannelCloseFlag.Socket)
            m_connector.DisconnectAsync()
        else
            Trace.TraceError("failed to receive from server, due to: {0}",saea.SocketError)    
            saea.ThrowException()

    and processSent saea =
        let channel = saea.UserToken :?> TcpRemoteChannel

        match saea.SocketError with
        | SocketError.Success -> 
            channel.HandleSent saea.BytesTransferred
        | SocketError.ConnectionReset 
        | SocketError.ConnectionAborted ->
            // we don't want to handle "ConnectionReset" twice
            // so here, we just do nothing but print warning
            // let the 'Receive' thread to detect and react
            Trace.TraceWarning("failed to send due to '{0}', wait 'Receive' thread to react",saea.SocketError)    
        | _ ->
            Trace.TraceError("failed to send to server, due to: {0}",saea.SocketError)    
            saea.ThrowException()

    let close() =
        if m_channel <> null then
            m_channel.Close ChannelCloseFlag.All
        (m_connector :> IDisposable).Dispose()
        Trace.TraceError("connection is completely closed")

    let onConnected() =
        // create new SAEA, the old ones have been disposed before disconnecting
        let channelArgs = 
            ChannelEvtArgs.Make bufSize onIoCompleted 1
            |> Seq.head

        let payloadStrategy = PayloadStrategyFactory.makeStrategy payloadType Guid.Empty m_evtDataReceived
        // because we create a new channel, the previous sending queue is dropped
        // no impact from last connection
        m_channel <- new TcpRemoteChannel(m_socket,channelArgs,payloadStrategy,m_sendLockOption)

        m_evtConnected.Trigger()
        m_channel.ReceiveAsync()

    do
        m_connector.EstablishedEvt.Add onConnected
        m_connector.TerminatedEvt.Add close

    // ***************************** destructor
    interface IDisposable with
        member this.Dispose() =
            close()

    // ***************************** public API
    [<CLIEvent>]
    member this.ConnectedEvt = m_evtConnected.Publish
    [<CLIEvent>]
    member this.DataReceivedEvt = m_evtDataReceived.Publish
    member this.DisconnectedCallback
        with set newvalue = m_connector.DisconnectedCallback <- newvalue

    member this.ConnectAsync() =
        m_connector.ConnectAsync()

    member this.SendAsync(data,offset,size) =
        m_channel.SendAsync(data,offset,size)
        
            
            
