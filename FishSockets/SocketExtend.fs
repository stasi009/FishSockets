
namespace FishSockets

// Sep 11, 2012
// Diaoyu Islands belongs to China, because I am Chinese

open System
open System.Net
open System.Net.Sockets
open System.Diagnostics

module SocketExtend =
    
    let inline invokeAsyncMethod (asyncmethod: SocketAsyncEventArgs->bool) (callback : SocketAsyncEventArgs->unit) saea =
        if not (asyncmethod saea) then
            callback saea

    let inline NewTcpSocket() = 
        new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)

    let inline NewUdpSocket()=
        new Socket(AddressFamily.InterNetwork,SocketType.Dgram,ProtocolType.Udp)

    let inline checkBuffer (buffer : byte[]) offset size=
        if buffer = null || offset < 0 || offset >= buffer.Length || size <= 0 || (offset+size) > buffer.Length then
            raise <| new ArgumentException("invalid buffer for sockets")

    // ***************************** socket extension
    type Socket with
        // ------------------------- TCP ------------------------- //
        member this.ConnectAsyncSafe saea callback =
            invokeAsyncMethod this.ConnectAsync callback saea

        member this.DisconnectAsyncSafe saea callback =
            invokeAsyncMethod this.DisconnectAsync callback saea

        member this.AcceptAsyncSafe (saea : SocketAsyncEventArgs) callback =
            saea.AcceptSocket <- null
            invokeAsyncMethod this.AcceptAsync callback saea

        member this.ReceiveAsyncSafe saea callback =
            invokeAsyncMethod this.ReceiveAsync callback saea

        member this.SendAsyncSafe saea callback =
            invokeAsyncMethod this.SendAsync callback saea

        // ------------------------- UDP ------------------------- //
        member this.SendToAsyncSafe saea callback =
            invokeAsyncMethod this.SendToAsync callback saea

        member this.ReceiveFromAsyncSafe saea callback =
            invokeAsyncMethod this.ReceiveFromAsync callback saea

        // ------------------------- OTHERS ------------------------- //
        member this.ShutClose() =
            if this.Connected then
                try
                    this.Shutdown(SocketShutdown.Both)
                finally
                    this.Close()

    // ***************************** SocketAsyncEventArgs extension
    type SocketAsyncEventArgs with
        member this.ThrowException() =
            Trace.TraceError("!!!!!! SocketError: {0}",this.SocketError)
            raise <| new SocketException(int(this.SocketError))
            


