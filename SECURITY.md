# Security policy

## Reporting a vulnerability

Please **do not** open a public issue for security problems.

Use [GitHub's private vulnerability reporting](https://github.com/emil-eklund/minne/security/advisories/new)
instead. Expect an acknowledgement within a week.

## What this project touches

Minne is a local desktop tool, but it handles material worth being careful with:

| Asset | Where it lives | Protection |
|---|---|---|
| Mail bodies (raw and cleaned) | `mail.db` in the data directory | **Plain text, unencrypted** |
| Embeddings of your mail | `mail.db` | Plain BLOBs |
| Graph refresh token | `msal_cache.bin` in the data directory | DPAPI (Windows), keychain/keyring (macOS/Linux), plain file fallback on headless Linux |
| Entra client id | `config.json` | Not a secret — it is a public client identifier |

The database is not encrypted. Anyone with read access to your user profile can
read your indexed mail. Keep the data directory out of synced or backed-up folders
if that matters to you.

## Scope

In scope: anything that leaks mail contents or tokens off the machine, escalates
Graph permissions beyond `Mail.Read` + `User.Read`, or lets a crafted email
influence the host (e.g. via body parsing).

Out of scope: the plain-text database (documented above), and the fact that a
local attacker with your user account can read your data.

## Network calls

Minne makes exactly three kinds of outbound request, all of which you can verify:

1. Microsoft identity endpoints, for sign-in.
2. Microsoft Graph, to read your own mailbox.
3. `huggingface.co`, once, to download the embedding and reranker models — avoidable
   entirely by pointing `embedding.onnx.modelDirectory` at a local folder.

There is no telemetry, no crash reporting and no update check.
