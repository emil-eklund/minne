<div align="center">

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="assets/logo-dark.svg">
  <img src="assets/logo.svg" alt="Minne" width="300">
</picture>

**Local hybrid search for your Microsoft 365 mailbox.**<br>
Finds the email you remember the *meaning* of, not the exact words.

[![ci](https://github.com/emil-eklund/minne/actions/workflows/ci.yml/badge.svg)](https://github.com/emil-eklund/minne/actions/workflows/ci.yml)
[![release](https://img.shields.io/github/v/release/emil-eklund/minne?display_name=tag&sort=semver)](https://github.com/emil-eklund/minne/releases)
[![license](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)
[![.NET 10](https://img.shields.io/badge/.NET-10.0-512BD4.svg)](https://dotnet.microsoft.com/)

</div>

---

You are looking for the agenda someone sent before a kick-off. You search *"kick-off
schedule"*. The email said *"kickoff agenda"* — one word different, one hyphen missing —
and Outlook returns nothing.

Minne indexes your Microsoft 365 mailbox **locally** and searches it with keyword *and*
semantic (embedding) retrieval at the same time. Nothing leaves your machine except the
Graph calls that fetch your own mail and a one-time model download from Hugging Face
(skippable — point the config at a local model folder). *Minne* is Swedish for **memory**.

## Quick start

1. Download `minne` from [releases](https://github.com/emil-eklund/minne/releases) and run it. No install, no configuration.
2. Press **Sync mailbox**. A browser window opens for Microsoft sign-in; grant read access to your mail.
3. Wait while it downloads the embedding model (once), fetches your inbox, archive and
   sent items, and embeds them — a 20k-message mailbox takes ~10 minutes the first time.
   Later syncs fetch only changes (Graph delta queries) and take seconds.
4. Type what you remember.

> **Don't take the pitch on faith — measure it.** *Tools → Evaluate search quality* scores keyword,
> semantic, hybrid and reranked retrieval against searches you actually struggled with in Outlook, so
> you can see with numbers whether Minne finds emails that Outlook search misses on *your* mailbox.
> Background in [docs/motivation.md](docs/motivation.md); what comes next in
> [docs/roadmap.md](docs/roadmap.md).

## How it works

```
Graph delta query ──> body cleaning ──> chunking ──> SQLite
 (incremental)        (strip quoted      (~900 chars,   ├─ messages   (metadata + clean body)
                       replies,           overlap)      ├─ messages_fts (FTS5, BM25)
                       signatures)                      └─ chunks     (text + embedding BLOB)

search:  filters (from/to/date/attachments) ──> SQL row set
         keyword  ──> FTS5 BM25 top-50 ─┐
         semantic ──> cosine over all   ├──> reciprocal rank fusion ──> top N
                      chunk vectors ────┘
```

* **Keyword** retrieval (SQLite FTS5) handles exact terms: names, invoice numbers, domains.
* **Semantic** retrieval (dense vectors) handles paraphrase and typos: "kickoff schedule" finds "kick-off agenda".
* **Hybrid** fuses both with RRF. Quoted tokens (`"INV-20431"`, `"SAS13524"`) are the explicit request for an exact match: quoted identifiers
  lean on keyword matches (`search.identifierVectorWeightFactor`; unquoted words keep the balanced weighting); stopwords in nine European
  languages are dropped from the keyword side but kept for the embedding.
* **Hybrid + rerank** retrieves like hybrid, then re-scores the top candidates (`rerank.depth`, default 50)
  with a multilingual cross-encoder, so a target both retrievers ranked low can still surface at the top.

## Searching

Type to search (debounced) or press Enter; pick **Hybrid**, **Hybrid + rerank**, **Exact words**
or **By meaning** (plus top-N) in the toolbar. Query syntax:

```
kickoff agenda
"kick-off agenda" from:anna after:2024-06 before:2024-07 has:attachment
invoice folder:inbox
```

Each result is tagged with which retriever found it (`exact` / `similar` / `both`) with matched
terms in bold; the preview pane shows the cleaned body, *Open in Outlook* (web link) and
*Copy Message-Id* (handy for building eval sets).

## Configuration

Everything lives in one data directory — `%LOCALAPPDATA%\Minne` on Windows, overridable
with `--data-dir` or the `MINNE_DATA` environment variable. A default `config.json` is
written there on first launch; open it via *Tools → Open config file*, edit, and restart
the app to apply.

```json
{
  "graph": {
    "clientId": "",
    "tenantId": "common",
    "folders": ["inbox", "archive", "sentitems"],
    "maxMessagesPerFolder": 0,
    "useDeviceCode": false
  }
}
```

An empty `clientId` means "sign in through the shared app registration that ships with the app"
(which also lets a later release rotate that registration without touching your config); put your
own registration's id there to use yours instead.

Set `maxMessagesPerFolder` (e.g. 2000) for a quick first experiment. Folder values are Graph well-known
names (`inbox`, `archive`, `sentitems`, `drafts`, `deleteditems`) or folder ids; `archive` is included by
default since many people keep their inbox empty and archive everything.

### Use your own Entra app registration (optional)

By default Minne signs in through a shared app registration (a *public client* — it holds no
secret and can only sign in as you, with delegated `Mail.Read` + `User.Read`). If you would
rather the app asking for access to your mail be one you control and can revoke — or your
tenant blocks unverified third-party apps — register your own in ~3 minutes:

1. [Entra admin center](https://entra.microsoft.com) → App registrations → **New registration**
2. Name: anything. Supported account types: *Accounts in this organizational directory only* (or multi-tenant).
3. Redirect URI: platform **Mobile and desktop applications**, value `http://localhost`
4. After creation: **Authentication** → *Allow public client flows* → **Yes** (needed for device-code login)
5. **API permissions** → Add → Microsoft Graph → *Delegated* → `Mail.Read` and `User.Read`. Admin consent is only needed if your tenant requires it for all apps.
6. Put the **Application (client) ID** in `graph.clientId` (and your tenant id in `graph.tenantId` for single-tenant registrations).

The shared registration's full manifest is in [src/app-registration.json](src/app-registration.json)
if you want to replicate it exactly.

### Embedding model

Default: [`sentence-transformers/paraphrase-multilingual-MiniLM-L12-v2`](https://huggingface.co/sentence-transformers/paraphrase-multilingual-MiniLM-L12-v2)
— developed by UKP Lab (TU Darmstadt, Germany), 50+ languages including all Nordic and major EU languages,
384 dimensions, ~470 MB ONNX, runs on CPU. Downloaded once into the data directory.

Any other model can be plugged in without code changes:

| Option | config.json |
|---|---|
| Another Hugging Face XLM-R model (needs `sentencepiece.bpe.model`) | `embedding.onnx.modelRepo`, `modelFile`, `tokenizerFile`, optionally `queryPrefix`/`documentPrefix` (e.g. `"query: "`/`"passage: "` for e5) |
| Offline local folder | `embedding.onnx.modelDirectory` pointing at a folder with the model + tokenizer |
| Ollama / LM Studio / any OpenAI-compatible server | `embedding.provider: "http"`, `embedding.http.endpoint`, `model` |

Changing model after indexing requires *Tools → Re-embed everything*; the index refuses to mix models.

Alternatives worth trying with the eval harness: `intfloat/multilingual-e5-small` (stronger, needs prefixes).
The local ONNX path reads SentencePiece models, so it takes XLM-R-family repos only; WordPiece models
(`jinaai/jina-embeddings-v2-base-de`, `KBLab/sentence-bert-swedish-cased`) need the HTTP provider instead.

### Reranker (mode "Hybrid + rerank")

Hybrid retrieval is optimised for not *missing* the target, not for putting it first. The rerank mode
re-scores the top fused candidates with a cross-encoder that reads query and passage together:
[`cross-encoder/mmarco-mMiniLMv2-L12-H384-v1`](https://huggingface.co/cross-encoder/mmarco-mMiniLMv2-L12-H384-v1)
— the multilingual mMARCO reranker from the same sentence-transformers family as the default embedding
model (~450 MB ONNX, CPU, downloaded on first use). Expect a few hundred extra ms per search for
`rerank.depth` = 50 candidates. Any other XLM-R ONNX cross-encoder can be plugged in via `rerank.onnx.modelRepo`
or `modelDirectory`, exactly like the embedding model. The eval harness includes the rerank mode automatically, so
whether re-ranking earns its latency is a measurement, not a guess.

## The Tools menu

| Item | What it does |
|---|---|
| Full resync | Re-fetches every folder from scratch instead of applying deltas |
| Re-embed everything | Drops all vectors and embeds again — required after changing the embedding model or token budget |
| Rebuild index | Re-runs body cleaning + chunking from stored raw bodies (after changing `indexing.*` settings or cleaning rules), then re-embeds; no Graph access needed |
| Evaluate search quality… | Scores keyword / vector / hybrid / rerank against a query set (below) |
| Create example eval set… | Writes a template query set to fill in |
| Open config file / data folder | Edit `config.json` (restart to apply) or inspect `mail.db` and the models |
| Sign out | Forgets the signed-in account; the next sync asks again |

## Evaluating search quality

1. Write down 30–50 searches you genuinely struggled with in Outlook, in your own words.
2. For each, find the target email and record its id (*Copy Message-Id* in the preview pane, or the Internet-Message-Id from Outlook's message headers).
3. *Tools → Create example eval set…*, fill it in (see [eval/queries.example.json](eval/queries.example.json)).
4. *Tools → Evaluate search quality…*

```
mode       R@1    R@5   R@10    MRR   avg ms
keyword    43%    60%    63%  0.512       12
vector     37%    70%    83%  0.507      180
hybrid     57%    83%    90%  0.681      195
rerank     63%    90%    93%  0.742      420
```

Then compare with Outlook's own search on the same queries. The table shows what each mode
contributes; the comparison with Outlook shows whether the index is earning its keep.

Knobs that matter, all in `config.json`: `indexing.chunkSizeChars`, `indexing.cleanBodies`,
`search.candidateCount`, `search.rrfK`, `search.vectorWeight`, `rerank.depth`, and the embedding and reranker models.
The app unloads the models and the vector index after `search.idleUnloadSeconds` (default 120)
without a search, dropping idle memory to a few tens of MB; the next search reloads them in about a second. Set `0` to keep everything loaded.

## Installing

Grab a build from [releases](https://github.com/emil-eklund/minne/releases) — single-file
executables that need no .NET installed:

| Download | Contents |
|---|---|
| `minne-<version>-win-x64.zip` | `minne.exe` |
| `minne-<version>-linux-x64.tar.gz` | `minne` (desktop app — needs a graphical session, e.g. X11/Wayland) |

Each archive has a `.sha256` published next to it. The binaries are not code-signed, so
Windows SmartScreen will warn on first run.

## Building

Requires the .NET 10 SDK. No other runtime or tool is needed; SQLite and ONNX Runtime ship as
native libraries inside their NuGet packages, and the tokenizer is pure managed code.

```
dotnet build
dotnet test
dotnet run --project src/MailSearch.App

# single-file executable (no .NET install needed on the target machine)
dotnet publish src/MailSearch.App -c Release -r win-x64
#   -> src/MailSearch.App/bin/Release/net10.0/win-x64/publish/minne.exe
```

Model integration test (downloads the default model): `MINNE_RUN_MODEL_TESTS=1 dotnet test`.

## Layout

```
src/MailSearch.Core
  Config/       AppConfig (config.json schema), DataPaths
  Mail/         MailMessage, IMailSource, GraphAuth (MSAL), GraphMailSource (delta sync)
  Text/         BodyCleaner (quoted replies, signatures, multilingual), TextChunker
  Embeddings/   IEmbeddingProvider, OnnxEmbeddingProvider, HttpEmbeddingProvider, tokenizer, downloader
  Storage/      SearchStore (SQLite: messages, FTS5, chunk vectors)
  Search/       QueryParser, HybridSearcher, RankFusion
  Rerank/       IReranker, OnnxReranker (cross-encoder), RerankerFactory
  Eval/         EvalRunner
  Indexer.cs    sync → clean → chunk → embed orchestration
src/MailSearch.App   desktop app (Avalonia — native rendering, no web view; minne.exe)
tests/               xunit tests; the search pipeline is tested end-to-end with a fake embedder
```

The product is *Minne*; the code is namespaced `MailSearch.*`. That is deliberate — the
source stays branding-free so the name can change without a repo-wide rename.

## Privacy and security notes

* Mail bodies (raw and cleaned) are stored in plain text in `mail.db` inside the data directory. Keep that directory out of synced folders.
* The Graph refresh token is stored encrypted (DPAPI on Windows, keychain/keyring on macOS/Linux; plain file fallback when no secure store is available).
* No telemetry, no network calls other than Graph and the one-time model download from huggingface.co (or none, with `modelDirectory`).
* The default sign-in uses a shared public-client app registration; it contains no secret, but if you would rather control the app identity yourself, [register your own](#use-your-own-entra-app-registration-optional).

The full threat model, and how to report a vulnerability privately, are in [SECURITY.md](SECURITY.md).

## Known limitations

* Vectors are scanned brute-force in memory on every search (~0.3 s for 100k chunks). Fine for mailboxes of that size; a persistent ANN index or quantization is on the [roadmap](docs/roadmap.md).
* Attachments are not indexed, only the `has:attachment` flag.
* Body cleaning is heuristic; check result snippets for quoted-reply leakage and extend `BodyCleaner` patterns.
* Graph delta queries work per folder; `folders` must be listed explicitly.

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md). Short version: anything that touches ranking
needs eval numbers rather than opinions, because intuitions about retrieval are usually
wrong.

## License

[MIT](LICENSE) (c) 2026 Emil Eklund
