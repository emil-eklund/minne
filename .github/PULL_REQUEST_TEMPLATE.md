## What this changes

<!-- One or two sentences. Link the issue if there is one. -->

## Why

<!-- What problem does it solve? -->

## If this touches ranking

Chunking, body cleaning, fusion weights, models or rerank depth all move retrieval
quality. Paste before/after eval numbers (Tools → Evaluate search quality) on the same query set — otherwise
there is no way to tell an improvement from a regression.

```
mode       R@1    R@5   R@10    MRR   avg ms
before
after
```

## Checklist

- [ ] `dotnet build -warnaserror` and `dotnet test` pass
- [ ] No product name ("Minne") introduced into type or namespace names
- [ ] README updated if user-visible behaviour changed
- [ ] No real email content, addresses or tenant identifiers in the diff
