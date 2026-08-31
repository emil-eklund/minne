# Why Minne exists

Email search is bad in a specific, frustrating way: it only finds the words you
happened to remember correctly.

You are looking for the agenda someone sent before a kick-off. You search
"kick-off schedule". The email said "kickoff agenda" — one word different, one
hyphen missing — and Outlook returns nothing. You know the email exists. You can
picture it. You cannot find it, so you scroll, or you give up and ask the person
to resend.

That failure is not a bug in Outlook. Keyword search is doing exactly what it was
built to do. The problem is that human memory of a message is *semantic* — you
remember what it was about — while the index is *lexical*. Minne closes that gap by
keeping a local vector embedding of every message alongside the keyword index, so a
search can match on meaning as well as on spelling.

## Why both, and not just embeddings

Semantic search on its own is worse than keyword search at the thing keyword search
is best at. Ask for invoice `SAS13524` and an embedding model will cheerfully return
a dozen emails that are *about invoices*. Identifiers, names, domains and order
numbers need exact matching; concepts like "travel policy" need meaning.

So Minne runs both retrievers and fuses the results. Quoted tokens are read as an
explicit request for an exact match, which lets you tip the balance yourself when
you know which kind of search you are doing.

## Why local

Mail is the most sensitive archive most people own. A search tool that ships it to
a third party to be embedded is a bad trade for a marginally better ranking. All of
Minne's processing happens on the machine: mail is fetched from Microsoft Graph with
your own credentials, embedded on your CPU, and stored in a SQLite file you own.
Nothing else leaves. (The embedding model itself arrives the other way — downloaded
once from Hugging Face, or supplied from a local folder for fully offline use.)

The cost of that decision is real — CPU-based embedding of a large mailbox takes
minutes, and the models are a few hundred megabytes on disk. It is worth it.

## Scope

Finding email, not managing it. Composing, replying, filing and deleting are all
things existing clients already do well, and there is no reason to rebuild them.
Minne is the index and the search box.

## Decided by measurement

The claim Minne has to keep earning is: local hybrid search finds emails that
Outlook search misses. The `eval` command scores keyword, vector, hybrid and rerank
modes against a query set of real searches that were genuinely difficult, so every
change that touches ranking — chunking, cleaning, fusion weights, models — is
measured on that set rather than argued about.

See [roadmap.md](roadmap.md) for what comes next.
