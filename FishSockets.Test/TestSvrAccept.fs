
namespace FishSockets.Test

module TestSvrAccept =

    open System
    open System.Net
    open System.Net.Sockets
    open System.Threading

    open FishSockets
    open FishSockets.SocketExtend
    open TestHelper

    let private svrmain numChannel0 lsnport =
        use svr = new TcpServer(numChannel0,1024,PayloadType.Stream)
        let count = ref 0

        let onClientConnected id =
            match svr.TryGetRemoteClient id with
            | (true,info)->
                let newcount = Interlocked.Increment count
                printfn "[%d] <%s> connected" newcount (string info.RemoteEndPnt)
            | (false,_) ->
                failwith "failed to find client"
        svr.ClientConnectedEvt.Add onClientConnected

        svr.Start(lsnport,100)
        pause()

    type private Client(m_index:int,svraddress,svrport,m_printer : PrintAgent)=
        let m_socket = NewTcpSocket()
        let m_saea = new SocketAsyncEventArgs()

        let onConnected (saea : SocketAsyncEventArgs) =
            match saea.LastOperation with
            | SocketAsyncOperation.Connect ->
                match saea.SocketError with
                | SocketError.Success -> 
                    m_printer.Print <| sprintf "[%d] connected with server" m_index
                | _ ->
                    saea.ThrowException()
            | _ ->
                failwith "only support CONNECT"

        do
            m_saea.RemoteEndPoint <- new IPEndPoint(IPAddress.Parse(svraddress),svrport)
            m_saea.Completed.Add onConnected

        member this.ConnectAsync() =
            m_socket.ConnectAsyncSafe m_saea onConnected

        interface IDisposable with
            member this.Dispose() =
                m_socket.ShutClose()
                (m_saea :> IDisposable).Dispose()

    let testmain (args: string[]) =

        let lsnport = 7027

        match args.[0] with
        | "server" -> 
            svrmain 2 lsnport

        | "client" ->
            use printer = new PrintAgent()

            let clients = 
                Array.init 20 (fun index -> new Client(index,"127.0.0.1",lsnport,printer))

            clients
            |> Seq.iter (fun c -> c.ConnectAsync())

            pause()

            clients
            |> Seq.iter (fun c -> (c :> IDisposable).Dispose())

        | _ ->
            failwith "unrecognized startup option"
        


        
        