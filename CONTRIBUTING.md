# Contributing

Thanks for taking a look. This is a small project with an opinionated scope, so a
quick word on what fits before you spend time on a patch.

## Scope

Minne finds email. It does not send, compose, organise or delete it — Outlook and
the web apps already do that well. Retrieval quality, indexing speed and privacy
are the things worth optimising here.

## Getting set up

You need the .NET 10 SDK and nothing else. SQLite, ONNX Runtime and the tokenizer
all arrive as native libraries inside NuGet packages.

```
git clone https://github.com/emil-eklund/minne
cd minne
dotnet build
dotnet test
```

Running against a real mailbox works out of the box: the app ships with a shared
Entra app registration. You can also register your own and point `graph.clientId`
at it — see the README.

The two ONNX model tests are skipped by default because they download roughly
900 MB. Opt in when you touch the embedding or reranking code:

```
MINNE_RUN_MODEL_TESTS=1 dotnet test
```

## Naming

The product is called Minne. The code is not: namespaces and assemblies use the
generic `MailSearch.*` prefix so the product name can change without a source-wide
rename. Please keep new code branding-free — the string "Minne" belongs in user-facing
output, the README and the csproj metadata, not in type names.

## Changes that need a measurement, not an opinion

Anything that alters ranking — chunking, body cleaning, fusion weights, the models,
reranking depth — should come with eval numbers, because intuitions about retrieval
are usually wrong. Build a query set as described in the README and paste the before
and after table into the pull request:

```
mode       R@1    R@5   R@10    MRR   avg ms
hybrid     57%    83%    90%  0.681      195
```

Your query set will be personal and unshareable, and that is fine. What matters is
that the comparison was actually run on the same set, before and after.

## Style

- Match the surrounding code; there is no separate style guide. `.editorconfig` covers the mechanics.
- CI builds with `-warnaserror`. Warnings will fail the build.
- Comments should explain *why*, not restate the code. The existing comments are the reference.
- Keep the dependency list short. A new NuGet package needs a reason in the pull request.

## Pull requests

Small and focused beats large and comprehensive. Say what you changed, why, and how
you convinced yourself it works. If it changes behaviour a user would notice, update
the README in the same PR.
