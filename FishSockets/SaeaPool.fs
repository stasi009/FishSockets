
namespace FishSockets

open System
open System.Net.Sockets
open System.Collections.Concurrent
open System.Diagnostics

[<Sealed>]     
type SaeaPool(numChannel0 : int,m_perBufSize : int,m_callback) =    

    [<VolatileField>]
    let mutable m_numChannel = numChannel0 // initial number of channels

    let mutable m_increaseRatio = 0.5f
    let mutable m_disposed = false
    
    // build the pool without bound, because it may be auto-increased in the future
    let m_pool = new BlockingCollection<ChannelEvtArgs>()

    let reset (saea : SocketAsyncEventArgs) =
        try
            if saea.Count < m_perBufSize then
                saea.SetBuffer(saea.Offset,m_perBufSize)
            saea.UserToken <- null
            saea.RemoteEndPoint <- null
        with
        | :? ObjectDisposedException ->
            // such exceptions only happens when the whole object is disposed
            // so just ignore them
            () 

    do
        m_numChannel
        |> ChannelEvtArgs.Make m_perBufSize m_callback
        |> Seq.iter m_pool.Add

    static member inline TryTakeAsTuple (pool : BlockingCollection<'t>) (timeout : int)=
        let item = ref Unchecked.defaultof<'t>
        let isSuccess = pool.TryTake(item,timeout)
        (isSuccess,item.Value)

    member this.IncreaseRatio
        with get() = m_increaseRatio
        // set negative values to disable "auto-increase"
        and set(newvalue) = m_increaseRatio <- newvalue

    member this.CheckIn (item : ChannelEvtArgs) =
        if m_disposed then
            (item :> IDisposable).Dispose()
        else
            reset item.RecvSaea
            reset item.SendSaea
            m_pool.Add item
            Trace.TraceInformation("ChannelArgs are returned back to the pool")

    member this.CheckOut timeout =
        if m_disposed then
            raise <| new ObjectDisposedException("SAEA pool has been disposed")
        else
            match SaeaPool.TryTakeAsTuple m_pool timeout with
            | (true,_) as taken -> taken
            | (false,_) as taken-> 
                if m_increaseRatio <= 0.0f then
                    taken             
                else
                    // double-lock pattern
                    lock m_pool (fun() ->
                                        match SaeaPool.TryTakeAsTuple m_pool (timeout/2) with
                                        | (true,_) as taken -> taken
                                        | (false,_) ->
                                            let newItems = 
                                                (float32 m_numChannel) * m_increaseRatio
                                                |> int
                                                |> max 1
                                                |> ChannelEvtArgs.Make m_perBufSize m_callback
                                                |> Array.ofSeq

                                            // the first item will be returned immediately
                                            // so we only insert the rest into queue
                                            for index=1 to (newItems.Length-1) do
                                                m_pool.Add newItems.[index]

                                            m_numChannel <- m_numChannel + newItems.Length
                                            Trace.TraceWarning("{0} new ChannelArgs inserted into pool, current pool size={1}", newItems.Length, m_numChannel)

                                            (true,newItems.[0])
                                        )
    interface IDisposable with
        member this.Dispose() =
            if not m_disposed then
                try
                    m_pool.CompleteAdding()

                    m_pool.GetConsumingEnumerable()
                    |> Seq.iter (fun item -> (item :> IDisposable).Dispose())

                    m_pool.Dispose()
                finally
                    m_disposed <- true
             
