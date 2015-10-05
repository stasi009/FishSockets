
namespace FishSockets

open System
open TVA
open TVA.Parsing

type ParserState =
    | Head = 0
    | Body = 1

[<Sealed>]
type HeadBodyParser() =
    inherit BinaryImageParserBase()

    // -------------------------- primary constructor
    let m_evtBodyParsed = new Event<byte[]*int*int>()
    let m_headSize = 4
    let mutable m_bodySize = 0
    let mutable m_state = ParserState.Head

    let parseHead buffer offset size =
        if size >= m_headSize then
            m_bodySize <- EndianOrder.LittleEndian.ToInt32(buffer,offset)

            if m_bodySize > 0 then
                m_state <- ParserState.Body

            m_headSize
        else
            0

    let parseBody buffer offset size =
        if size >= m_bodySize then
            m_evtBodyParsed.Trigger(buffer,offset,m_bodySize)

            m_state <- ParserState.Head
            m_bodySize
        else
            0

    // -------------------------- 
    [<CLIEvent>]
    member this.BodyParsedEvt = m_evtBodyParsed.Publish

    // -------------------------- override methods
    override this.Start() =
        base.Start()
        // to deal with re-connect
        // clear the remaining unparsed buffer left by previous connection
        this.UnparsedBuffer <- null

    override this.ProtocolUsesSyncBytes
        with get() = false

    override this.ParseFrame(buffer,offset,size) =
        match m_state with
        | ParserState.Head -> 
            parseHead buffer offset size
        | ParserState.Body ->
            parseBody buffer offset size
        | _ ->
            failwith "impossible parser state"
