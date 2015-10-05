namespace FishSockets

open System
open System.Threading
open System.Collections.Generic
open System.Collections.Concurrent

type SyncOption =
    | NoLock = 0
    | Lock = 1
    | SpinLock = 2
    | Aggregate = 3

type ISendController<'t> =
    interface
        abstract SyncOption : SyncOption with get
        abstract EnqueueOrSend : 't->bool*'t
        abstract DequeueAndSend : unit -> bool*'t
    end

module SendControllerHelper =
    let unSafeEnqueueOrSend busy (queue : Queue<'t>) item =
        match !busy with
        | true -> 
            queue.Enqueue(item)
            false,Unchecked.defaultof<'t>
        | false ->
            busy := true
            true,item

    let unSafeDequeueAndSend busy (queue : Queue<'t>) =
        try
            let item = queue.Dequeue()
            true,item
        with
            | :? InvalidOperationException ->
                // the queue is empty
                busy := false
                false,Unchecked.defaultof<'t>

open SendControllerHelper

// !!! this class has one potential bug, or we can prefer it as "design flaw in purpose"
// !!! the method below uses a "check-and-action" pattern
// !!! so there is possibility that there will be race between "check" and "action"
// !!! if there is one "SendAsync" after "TryDequeue returns false" but before 
// !!! "reset m_sendingAlive to zero"
// !!! it will cause that last piece staying in the sending queue until the next "SendAsync"
// !!! that can be forever
// !!! so below codes are suitable for the case that sending is very frequent
// !!! in that situation, the lock-free fashion used in the codes will greatly improve the
// !!! throughput and performance, and the risk of keeping the last piece in the sending queue
// !!! is trivial
// !!! however, if in a case that sending is infrequent, then add lock can guarantee
// !!! any piece will always be sent, and since IO is not frequent, so locking price will not
// !!! be that high
[<Sealed>]
type NoLockSendController<'t>() =
    let m_queue = new ConcurrentQueue<'t>()
    let m_busy = ref 0

    interface ISendController<'t> with
        member this.SyncOption 
            with get() = SyncOption.NoLock

        member this.EnqueueOrSend item =
            // pay attention here, we must always "enqueue first, and dequeue after"
            // due to the potential race condition, we CANNOT guarantee that 
            // when "m_busy=0", the queue must be empty
            // so we always "dequeue and send", which makes sure that the sending item is always
            // at the rear of the queue
            m_queue.Enqueue item

            if Interlocked.CompareExchange(m_busy,1,0) = 0 then
                // since we just enqueue one, so this TryQueue must return true
                // but we have to raise an alert when unexpected case happens
                match m_queue.TryDequeue() with
                | (true,_) as result -> result
                | (false,_) -> failwith "Dequeue failed even after Enqueue"
            else
                false,Unchecked.defaultof<'t>

        member this.DequeueAndSend () =
            let isSuccess,item = m_queue.TryDequeue()
            if isSuccess then
                true,item
            else
                Interlocked.Exchange(m_busy,0) |> ignore
                false,Unchecked.defaultof<'t>

[<Sealed>]
type LockSendController<'t>() =
    let m_queue = new Queue<'t>()
    let m_syncroot = new obj()
    let m_busy = ref false

    interface ISendController<'t> with
        member this.SyncOption 
            with get() = SyncOption.Lock

        member this.EnqueueOrSend item =
            lock m_syncroot (fun() -> unSafeEnqueueOrSend m_busy m_queue item )

        member this.DequeueAndSend () =
            lock m_syncroot (fun() -> unSafeDequeueAndSend m_busy m_queue)


[<Sealed>]
type SpinLockSendController<'t>() =
    let m_queue = new Queue<'t>()
    // due to SpinLock is Value Type, never store it into readonly fields
    // which the compiler may copy for optimization
    let mutable m_spinlock = new SpinLock() 
    let m_busy = ref false

    interface ISendController<'t>  with
        member this.SyncOption
            with get() = SyncOption.SpinLock

        member this.EnqueueOrSend item =
            let lockTaken = ref false
            try
                m_spinlock.Enter(lockTaken)
                unSafeEnqueueOrSend m_busy m_queue item
            finally
                if !lockTaken then 
                    m_spinlock.Exit()

        member this.DequeueAndSend ()=
            let lockTaken = ref false
            try
                m_spinlock.Enter(lockTaken)
                unSafeDequeueAndSend m_busy m_queue
            finally
                if !lockTaken then 
                    m_spinlock.Exit()

module SendControllerFactory =
    let make<'t> syncoption =
        match syncoption with
        | SyncOption.NoLock -> new NoLockSendController<'t>() :> ISendController<'t>
        | SyncOption.Lock -> new LockSendController<'t>() :> ISendController<'t>
        | SyncOption.SpinLock -> new SpinLockSendController<'t>() :> ISendController<'t>
        | _ -> failwith "invalid lock option"
            
    

            

            
