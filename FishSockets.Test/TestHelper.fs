
namespace FishSockets.Test

open System.Threading
open FishSockets

module TestHelper =

    open System
    open System.Collections.Generic

    type PrintAgent() =
        let m_queue = MailboxProcessor.Start(fun inbox ->
            let rec loop() = async {
                let! msg = inbox.Receive()
                printfn "%s" msg
                return! loop()
            }
            loop()
        )

        member this.Print msg =
            m_queue.Post msg

        interface IDisposable with
            member this.Dispose() =
                (m_queue :> IDisposable).Dispose()

    let inline newRandom() =
        new Random(int (DateTime.Now.Ticks &&& 0x0000FFFFL))

    let inline pause() =
        printfn "Press <ENTER> to continue, ......"
        Console.ReadLine() |> ignore

    let inline wait4exit() =
        (new ManualResetEvent(false)).WaitOne() |> ignore

    let inline parseLockOption txt =
        Enum.Parse(typeof<SyncOption>,txt,true) :?> SyncOption

    let inline getInt (configs: IDictionary<string,string>) key =
        int configs.[key]

    let inline getEnum<'t> (configs: IDictionary<string,string>) key =
        Enum.Parse(typeof<'t>,configs.[key],true) :?> 't

        
           
        
          

