# Roadmap

Phase 1 is the evaluation described in [motivation.md](motivation.md): prove hybrid
retrieval beats Outlook on a real mailbox, or stop. Everything below is phase 2 and
conditional on that result.

## Decided by measurement, in rough priority order

**Approximate nearest-neighbour index.** Vectors are currently scanned brute-force
in memory on every search — about 0.3 s for 100k chunks. Fine for evaluating, wrong
for daily use. Either a persistent ANN index or scalar quantisation of the stored
vectors.

**OCR of embedded images.** People paste screenshots into email constantly, and that
text is currently invisible to the index. A local OCR pass over image attachments
and inline images would recover it.

**Attachment indexing.** Today only the `has:attachment` flag is stored. PDF and
Office document text is the obvious next body of content.

**MCP server.** Exposing the local index as [Model Context Protocol](https://modelcontextprotocol.io)
resources would let an agent search your mail without any of it leaving the machine —
which is exactly the property that makes a local index worth having.

## Design notes carried forward

*Identifiers versus concepts.* Something shaped like `SAS13524` should lean on exact
matching; a word like "travel" needs the embedding to find messages that are *about*
travel rather than ones that merely contain the word. **Implemented** — quoted tokens
tip the fusion weighting toward the keyword side.

*UI weight.* Mail clients are mostly React on Electron, which is good for portability
and terrible for memory. Since indexing and embedding already want the RAM, the UI
should be slim. **Implemented** — Avalonia, native rendering, no web view.

## Not planned

Composing, replying, filing, deleting. Other mail backends until the Graph path is
genuinely good. Any feature that requires mail to leave the machine.
