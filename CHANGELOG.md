# Changelog

Notable changes to Minne. Format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/);
versions follow [semantic versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [0.1.0] — first public release

Local hybrid search for a Microsoft 365 mailbox — and the eval harness to check,
on your own queries, that it beats Outlook search.

### Added
- Incremental mailbox sync over Microsoft Graph delta queries, with quoted-reply and
  signature stripping in nine European languages.
- SQLite index: message metadata, FTS5 full-text, and chunk embeddings in one file.
- Four search modes — `keyword`, `vector`, `hybrid` (reciprocal rank fusion), and
  `rerank` (multilingual cross-encoder over the fused candidates).
- Quoted tokens are treated as an explicit request for an exact match, so identifiers
  like `"INV-20431"` lean on the keyword side while ordinary words keep the balance.
- Pluggable embedding and reranker models: any Hugging Face ONNX model, a local
  folder, or an OpenAI-compatible HTTP endpoint, all via `config.json`.
- `eval` harness reporting R@1 / R@5 / R@10 / MRR and latency per mode, so ranking
  changes are measured rather than argued about.
- Avalonia desktop UI over the same index — native rendering, no web view.
- Single-file executables for Windows and Linux; no .NET install needed on the target machine.

[Unreleased]: https://github.com/emil-eklund/minne/compare/v0.1.0...HEAD
[0.1.0]: https://github.com/emil-eklund/minne/releases/tag/v0.1.0
