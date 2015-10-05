
namespace FishSockets.Test

module TestCrc =

    open System
    open System.IO
    open System.Diagnostics
    open TVA.IO.Checksums

    let private makeOriginal () =
        let random = new Random(DateTime.Now.Second)
        let numBatch = random.Next(10,1000)
        let crc = new CrcCCITT()

        use memstream = new MemoryStream()
        for index = 1 to numBatch do
            let length = random.Next(100,1000)
            let buffer = Array.zeroCreate<byte>(length)
            random.NextBytes(buffer)

            memstream.Write(buffer,0,buffer.Length)
            crc.Update buffer

        (memstream.ToArray(),crc.Value)

    let private restore (buffer : byte[]) =
        let crc = new CrcCCITT()
        crc.Update buffer
        crc.Value

    let testmain() =
        let buffer,oriChecksum = makeOriginal()
        printfn "original checksum = %d" oriChecksum

        let restoreChecksum = restore buffer 
        printfn "restored checksum = %d" restoreChecksum

        let matched = (oriChecksum = restoreChecksum)
        assert matched
        printfn "\tmatched? %b" matched

        // ------------------ change and test again
        buffer.[0] <- Checked.byte <| buffer.[0] + (byte 1)
        let changedChecksum = restore buffer
        printfn "changed checksum = %d" changedChecksum

        let unmatch = (oriChecksum = changedChecksum)
        assert (not unmatch)
        printfn "\tmatched? %b" unmatch 





        
        

