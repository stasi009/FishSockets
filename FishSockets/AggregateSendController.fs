
namespace FishSockets

open System
open System.IO
open System.Collections.Generic

[<Sealed>]
type AggregateSendController()=
    // ---------------------------- member field
    let m_queue = new Queue<byte[]*int*int>()
    let m_syncroot = new obj()
    let m_memstream = new MemoryStream()
    let m_invalid = Unchecked.defaultof<byte[]*int*int>
    let mutable m_busy = false

    // ---------------------------- private methods
    let unsafeEnqueueOrSend(data,offset,size)=
        match m_busy with
        | true->
            m_queue.Enqueue(data,offset,size)
            false,m_invalid
        | false->
            m_busy <- true
            true,(data,offset,size)

    let unsafeDequeueAndSend() =
        m_memstream.SetLength 0L
        m_memstream.Position <- 0L

        let aggregated =
            (m_queue
            |> Seq.fold (fun (stream : MemoryStream) (data,offset,size) -> stream.Write(data,offset,size);stream) m_memstream).ToArray()
        m_queue.Clear()

        if aggregated.Length > 0 then
            true,(aggregated,0,aggregated.Length)
        else
            m_busy <- false
            false,m_invalid

    // ---------------------------- implement ISendController
    interface ISendController<byte[]*int*int> with
        member this.SyncOption 
            with get() = SyncOption.Aggregate

        member this.EnqueueOrSend item =
            lock m_syncroot <| fun() -> unsafeEnqueueOrSend item

        member this.DequeueAndSend () =
            lock m_syncroot unsafeDequeueAndSend
            
            
