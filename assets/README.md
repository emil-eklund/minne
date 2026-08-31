# Brand assets

| File | Use |
|---|---|
| `logo.svg` | Horizontal lockup for light backgrounds (README, docs) |
| `logo-dark.svg` | Same lockup for dark backgrounds |
| `icon.svg` | Square mark — source of truth for every raster below |
| `icon.ico` | Windows executable icon (`ApplicationIcon` in both csproj files) |
| `icon-256.png` | Avalonia window icon, and the GitHub social preview image |

## Design

The mark is an envelope whose flap valley doubles as the **m** of Minne, with a
magnifier knocked out of the lower-right corner by a wider ink-coloured stroke
underneath. Everything is monoline at a single stroke weight so it holds together
from 16 px to arbitrarily large.

The wordmark is drawn as paths, not text. That is deliberate: SVG `<text>` renders
with whatever font the viewer happens to have, and GitHub's image pipeline has none
of them. Paths render identically everywhere.

```
ink     #12242F    tile, wordmark on light backgrounds
paper   #FBF7F0    envelope outline, wordmark on dark backgrounds
amber   #E8A33D    flap and magnifier — the single accent
```

## Regenerating the rasters

`icon.svg` is the source; `icon.ico` and `icon-256.png` are derived from it. There is
no rasteriser in this repo on purpose — it would be a build dependency that exists
only to redraw an icon that changes approximately never. Regenerate with any SVG
renderer at 16, 32, 48, 64, 128 and 256 px, and pack those PNGs into the `.ico`.

## Name

"Minne" is Swedish for *memory*. Under the MIT licence you may fork this project
freely, including the assets — but please rename your fork rather than shipping
something different under this name and mark.
