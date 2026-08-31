# Roadmap

Minne today: incremental Graph sync, hybrid and reranked search, the eval harness, and
a slim desktop UI. Everything below is future work. Anything that touches ranking gets
decided by eval numbers, not opinions — see [motivation.md](motivation.md).

## In rough priority order

**Approximate nearest-neighbour index.** Vectors are currently scanned brute-force
in memory on every search — about 0.3 s for 100k chunks. Fine at that scale, wrong
for a decade of mail. Either a persistent ANN index or scalar quantisation of the stored
vectors.

**OCR of embedded images.** People paste screenshots into email constantly, and that
text is currently invisible to the index. A local OCR pass over image attachments
and inline images would recover it.

**Attachment indexing.** Today only the `has:attachment` flag is stored. PDF and
Office document text is the obvious next body of content.

**MCP server.** Exposing the local index as [Model Context Protocol](https://modelcontextprotocol.io)
resources would let an agent search your mail without any of it leaving the machine —
which is exactly the property that makes a local index worth having.

**Packaging.** The zipped single-file executables are deliberate — no installer to
trust, nothing written outside the data directory. A `winget` manifest would make
install and update one command without giving that up; an installer proper only if
demand shows up.

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
