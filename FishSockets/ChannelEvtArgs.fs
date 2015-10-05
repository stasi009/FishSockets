
namespace FishSockets

open System
open System.Net.Sockets

[<Sealed>]
type ChannelEvtArgs(m_recvSaea : SocketAsyncEventArgs,m_sendSaea : SocketAsyncEventArgs,m_callback,m_maxBufSize) =
    let m_recvSubscription = m_recvSaea.Completed |> Observable.subscribe m_callback
    let m_sendSubscription = m_sendSaea.Completed |> Observable.subscribe m_callback

    member this.RecvSaea = m_recvSaea
    member this.SendSaea = m_sendSaea
    member this.Callback = m_callback
    member this.MaxBufSize = m_maxBufSize

    interface IDisposable with
        member this.Dispose() =
            m_recvSubscription.Dispose()
            m_recvSaea.Dispose()
            
            m_sendSubscription.Dispose()
            m_sendSaea.Dispose()

    static member Make perBufSize callback numChannel =
        // each channel will have two SAEA, one for reading, the other for writing
        let buffer = Array.zeroCreate<byte>(numChannel * 2 * perBufSize)
        seq {
            for index = 0 to (numChannel-1) do
                let recvSaea = new SocketAsyncEventArgs()
                recvSaea.SetBuffer(buffer,index * 2 * perBufSize,perBufSize)

                let sendSaea = new SocketAsyncEventArgs()
                sendSaea.SetBuffer(buffer,(index*2 + 1) * perBufSize,perBufSize)

                yield new ChannelEvtArgs(recvSaea,sendSaea,callback,perBufSize)
        }