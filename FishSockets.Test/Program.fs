
namespace FishSockets.Test

module Program =

    open System
    open System.Diagnostics

    [<EntryPoint>]
    let main args =

        Trace.Listeners.Add <| new ConsoleTraceListener()
        |> ignore
        
        // TestCrc.testmain()
        // TestSvrAccept.testmain args
        // TestHeadBodyParsing.testmain()
        // DemoPayloadFeature.testmain args
        // TcpEchoTest.testmain args
        // UdpSimpleTest.testmain args
        // UdpEchoTest.testmain args
        SendLockPerfTests.testmain args

        0