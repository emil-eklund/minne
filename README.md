# Local hybrid email search (phase 1)

A console tool that indexes your Microsoft 365 mailbox **locally** and lets you search it with
keyword + semantic (embedding) retrieval. Nothing leaves your machine except the Graph calls
that fetch your own mail. Phase 1 exists to answer one question with numbers: *does local hybrid
search find emails that Outlook search misses?*

```
mailsearch config init          # write config.json, then set graph.clientId
mailsearch login                # sign in (browser)
mailsearch sync                 # fetch inbox + sent items, clean, chunk, embed
mailsearch search kickoff agenda from:anna after:2025-01
mailsearch eval eval/queries.local.json --verbose
```

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
* **Hybrid** fuses both with RRF. Queries containing identifier-like tokens (`INV-20431`, `SAS13524`, addresses)
  automatically lean on keyword matches (`search.identifierVectorWeightFactor`); stopwords in nine European
  languages are dropped from the keyword side but kept for the embedding. `eval` reports all three modes so you
  can see what each contributes.

### Embedding model

Default: [`sentence-transformers/paraphrase-multilingual-MiniLM-L12-v2`](https://huggingface.co/sentence-transformers/paraphrase-multilingual-MiniLM-L12-v2)
— developed by UKP Lab (TU Darmstadt, Germany), 50+ languages including all Nordic and major EU languages,
384 dimensions, ~470 MB ONNX, runs on CPU. Downloaded once into the data directory.

Any other model can be plugged in without code changes:

| Option | config.json |
|---|---|
| Another Hugging Face ONNX model (needs `tokenizer.json`) | `embedding.onnx.modelRepo`, `modelFile`, `tokenizerFile`, optionally `queryPrefix`/`documentPrefix` (e.g. `"query: "`/`"passage: "` for e5) |
| Offline local folder | `embedding.onnx.modelDirectory` pointing at a folder with the model + tokenizer |
| Ollama / LM Studio / any OpenAI-compatible server | `embedding.provider: "http"`, `embedding.http.endpoint`, `model` |

Changing model after indexing requires `mailsearch embed --reset`; the index refuses to mix models.

Alternatives worth trying with the eval harness: `intfloat/multilingual-e5-small` (stronger, needs prefixes),
`jinaai/jina-embeddings-v2-base-de` (German/English), `KBLab/sentence-bert-swedish-cased` (Swedish only).

## Setup

### 1. Entra app registration (one-time, ~3 minutes)

The tool is a *public client*: it signs in as you and reads only your own mailbox.

1. [Entra admin center](https://entra.microsoft.com) → App registrations → **New registration**
2. Name: anything. Supported account types: *Accounts in this organizational directory only* (or multi-tenant if you want to use it with several tenants).
3. Redirect URI: platform **Mobile and desktop applications**, value `http://localhost`
4. After creation: **Authentication** → *Allow public client flows* → **Yes** (needed for device-code login)
5. **API permissions** → Add → Microsoft Graph → *Delegated* → `Mail.Read` and `User.Read`. Admin consent is only needed if your tenant requires it for all apps.
6. Copy the **Application (client) ID**.

### 2. Configure

```
mailsearch config init
```

Edit the printed `config.json` (default location `%LOCALAPPDATA%\MailSearch\config.json`, override with `--data-dir` or `MAILSEARCH_DATA`):

```json
{
  "graph": {
    "clientId": "00000000-0000-0000-0000-000000000000",
    "tenantId": "common",
    "folders": ["inbox", "sentitems"],
    "maxMessagesPerFolder": 0,
    "useDeviceCode": false
  }
}
```

Set `maxMessagesPerFolder` (e.g. 2000) for a quick first experiment. Folder values are Graph well-known
names (`inbox`, `archive`, `sentitems`, `drafts`, `deleteditems`) or folder ids; `archive` is included by
default since many people keep their inbox empty and archive everything.

### 3. Run

```
mailsearch login
mailsearch sync            # first run: downloads model, full sync, embeds everything
mailsearch sync            # later runs: only changes (delta query)
mailsearch stats
mailsearch reindex         # after editing BodyCleaner or indexing.* settings: re-clean + re-chunk from
                           # stored raw bodies and re-embed, without touching Graph (seconds, not minutes)
```

**No app registration for a quick test:** set `graph.clientId` to `14d82eec-204b-4c2f-b7e8-296a70dab67e`
(the Microsoft Graph PowerShell public client, which most tenants already allow and which supports `Mail.Read`).
Good enough for evaluating on your own mailbox; do not ship with it — register your own multi-tenant app instead.

Embedding runs at roughly 50–150 chunks/s on a laptop CPU; a 20k-message mailbox takes ~10 minutes the first time.

## Searching

```
mailsearch search kickoff agenda
mailsearch search "kick-off agenda" from:anna after:2024-06 before:2024-07 has:attachment
mailsearch search budget --mode keyword        # or vector / hybrid (default)
mailsearch search budget --top 25 --ids        # show ids for building an eval set
mailsearch search budget --json
```

Result lines are tagged `[kw ]`, `[vec]` or `[k+v]` to show which retriever(s) found them.

## Desktop UI

A slim desktop front end (Avalonia — native rendering, no web view) over the same index and config:

```
dotnet run --project src/MailSearch.App
```

Type to search (debounced) or press Enter; pick hybrid/keyword/vector and top-N in the toolbar.
Each result shows which retriever found it (`kw` / `vec` / `k+v`) with matched terms in bold; the
preview pane shows the cleaned body, *Open in Outlook* (web link) and *Copy Message-Id* (handy for
building eval sets). *Sync mailbox* runs the same sync + embed as the CLI, with progress in the
status bar. It shares `%LOCALAPPDATA%\MailSearch` with the CLI (`--data-dir` / `MAILSEARCH_DATA`
work the same). Publish like the CLI: `dotnet publish src/MailSearch.App -c Release -r win-x64`
→ `mailsearch-ui.exe`.

## Evaluating (the actual point of phase 1)

1. Write down 30–50 searches you genuinely struggled with in Outlook, in your own words.
2. For each, find the target email (with `search --ids`, or from the Internet-Message-Id in Outlook's message headers) and record it.
3. `mailsearch eval init eval/queries.local.json`, fill it in (see `eval/queries.example.json`).
4. `mailsearch eval eval/queries.local.json --verbose`

```
mode       R@1    R@5   R@10    MRR   avg ms
keyword    43%    60%    63%  0.512       12
vector     37%    70%    83%  0.507      180
hybrid     57%    83%    90%  0.681      195
```

Then compare with Outlook's own search on the same queries. If hybrid does not clearly beat
both keyword-only and Outlook, the project should stop here.

Knobs that matter, all in `config.json`: `indexing.chunkSizeChars`, `indexing.cleanBodies`,
`search.candidateCount`, `search.rrfK`, `search.vectorWeight`, and the embedding model.

## Building

Requires the .NET 10 SDK. No other runtime or tool is needed; SQLite, ONNX Runtime and the tokenizer
ship as native libraries inside the NuGet packages.

```
dotnet build
dotnet test
dotnet run --project src/MailSearch.Cli -- search hello

# single-file executable (no .NET install needed on the target machine)
dotnet publish src/MailSearch.Cli -c Release -r win-x64
#   -> src/MailSearch.Cli/bin/Release/net10.0/win-x64/publish/mailsearch.exe
```

Model integration test (downloads the default model): `MAILSEARCH_RUN_MODEL_TESTS=1 dotnet test`.

## Layout

```
src/MailSearch.Core
  Config/       AppConfig (config.json schema), DataPaths
  Mail/         MailMessage, IMailSource, GraphAuth (MSAL), GraphMailSource (delta sync)
  Text/         BodyCleaner (quoted replies, signatures, multilingual), TextChunker
  Embeddings/   IEmbeddingProvider, OnnxEmbeddingProvider, HttpEmbeddingProvider, tokenizer, downloader
  Storage/      SearchStore (SQLite: messages, FTS5, chunk vectors)
  Search/       QueryParser, HybridSearcher, RankFusion
  Eval/         EvalRunner
  Indexer.cs    sync → clean → chunk → embed orchestration
src/MailSearch.App   desktop UI (Avalonia; mailsearch-ui.exe)
src/MailSearch.Cli   command-line front end (mailsearch.exe)
tests/               xunit tests; the search pipeline is tested end-to-end with a fake embedder
```

## Privacy and security notes

* Mail bodies (raw and cleaned) are stored in plain text in `mail.db` inside the data directory. Keep that directory out of synced folders.
* The Graph refresh token is stored encrypted (DPAPI on Windows, keychain/keyring on macOS/Linux; plain file fallback on headless Linux).
* No telemetry, no network calls other than Graph and the one-time model download from huggingface.co (or none, with `modelDirectory`).

## Known limitations (phase 1)

* Vectors are scanned brute-force in memory on every search (~0.3 s for 100k chunks). Fine for evaluation; a persistent ANN index or quantization is a phase-2 item.
* Attachments are not indexed, only the `has:attachment` flag.
* Body cleaning is heuristic; check `search --json` snippets for quoted-reply leakage and extend `BodyCleaner` patterns.
* Graph delta queries work per folder; `folders` must be listed explicitly.
