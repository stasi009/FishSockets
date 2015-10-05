
namespace FishSockets.Test

open System
open System.IO
open TVA
open TVA.IO.Checksums
open FishSockets

module TestHeadBodyParsing =

    let private testAddHeader() =

        // ------------------------- prepare
        let checksum buffer offset size =
            let crc = new CrcCCITT()
            crc.Update(buffer,offset,size)
            crc.Value

        let random = new Random()
        let buffer = Array.zeroCreate<byte>(random.Next(10,100))
        random.NextBytes buffer

        // ------------------------- action
        for index = 1 to 10 do
            let offset = random.Next(0,buffer.Length)
            let size = buffer.Length - offset;
            let oricrc = checksum buffer offset size

            let newbuffer,newoffset,newsize =
                let strategy = new BoundaryPayloadStrategy(Guid.NewGuid(),(new Event<Guid*byte[]>()))
                (strategy :> IPayloadStrategy).PreSending(buffer,offset,size)

            // ------------------------- check
            assert (newoffset = 0)
            assert (newsize = (4+size))

            let bodyLength = EndianOrder.LittleEndian.ToInt32(newbuffer,0)
            assert (bodyLength = size)

            let parsedcrc = checksum newbuffer 4 (newsize - 4)
            assert (oricrc = parsedcrc)

            printfn "[%d] test succeeds" index

    let private makeInput numBatches maxSize =
        let random = new Random(DateTime.Now.Second)
        let crc = new CrcCCITT()
        let strategy = 
            new BoundaryPayloadStrategy(Guid.NewGuid(),(new Event<Guid*byte[]>())) :> IPayloadStrategy
        let emptyHead = EndianOrder.LittleEndian.GetBytes 0
        
        use memstream = new MemoryStream()
        for index = 1 to numBatches do
            let buffer = Array.zeroCreate<byte>(random.Next(0,maxSize))
            random.NextBytes(buffer)

            crc.Update(buffer,0,buffer.Length)

            let newbuffer,newoffset,newsize =
                strategy.PreSending(buffer,0,buffer.Length)

            memstream.Write(newbuffer,newoffset,newsize)
            printfn "[%d] %d bytes are written" index buffer.Length
            
            // add empty head to increase the complexity
            memstream.Write(emptyHead,0,emptyHead.Length)

        (memstream.ToArray(),crc.Value)

    let private split (buffer : byte[]) numBatches =
        seq {
            let offset = ref 0
            let average = buffer.Length / numBatches

            while !offset < buffer.Length do
                let pieceSize = min (buffer.Length - !offset) average
                let piece = (buffer,!offset,pieceSize)

                offset := !offset + pieceSize
                yield piece
        }

    let private parseOut buffer numBatches =
        let bodyCounter = ref 0
        let crc = new CrcCCITT()

        let onparsed(id,buffer: byte[]) =
            incr bodyCounter
            printfn "[%d] %d bytes are parsed out" !bodyCounter buffer.Length
            crc.Update(buffer)

        let evtBodyParsed = new Event<Guid*byte[]>()
        evtBodyParsed.Publish.Add onparsed

        let strategy = new BoundaryPayloadStrategy(Guid.NewGuid(),evtBodyParsed) :> IPayloadStrategy
        let parseCounter = ref 0
        
        split buffer numBatches
        |> Seq.iter (fun (buffer,offset,size) -> 
                                                incr parseCounter
                                                printfn "\t>>>>>>[%d]-th parsing" !parseCounter
                                                strategy.ProcessReceived(buffer,offset,size))

        crc.Value

    let private checkParsing() =
        let buffer,oriCRC = makeInput 88 10240
        printfn "***************** original CRC = %d" oriCRC

        let parsedCRC = parseOut buffer 111
        printfn "***************** parsed CRC = %d" parsedCRC

        let isEqual = (oriCRC = parsedCRC)
        assert isEqual
        printfn "CRC (%d and %d) matched? %b" oriCRC parsedCRC  isEqual


    let testmain() =
        // testAddHeader()
        checkParsing()
        


        


