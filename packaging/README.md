# Packaging

Three Windows distributions come out of one build, in ascending order of how much they touch
the machine:

| Artifact | Built by | Writes |
|---|---|---|
| `minne-<version>-win-x64.zip` | `dotnet publish` | nothing outside the data directory |
| `Minne-<version>-x64.msi` | `windows/Minne.wxs` | `%LOCALAPPDATA%\Programs\Minne`, a Start menu shortcut, one HKCU key |
| winget package | `winget/*.yaml` | the MSI, fetched and hash-checked by winget |

The portable zip stays the reference distribution — the installer exists for the things a zip
cannot do: appear in Add/Remove Programs, put an entry in the Start menu, and offer to take the
mail index with it when it goes.

## The MSI

Per-user by design. Everything the app writes already lives in `%LOCALAPPDATA%`, so there is
nothing to install for all users, and an install that never raises a UAC prompt is one less
thing to trust.

```
dotnet publish src/MailSearch.App -c Release -r win-x64 -p:Version=0.4.0 -o artifacts/win-x64
dotnet tool install --global wix --version 6.*
wix build packaging/windows/Minne.wxs -arch x64 -d Version=0.4.0 -d PublishDir=$PWD/artifacts/win-x64 -o Minne-0.4.0-x64.msi
```

`ProductCode` defaults to `*` (a fresh one per build); the release workflow passes an explicit
one with `-d ProductCode={…}` so the winget manifest can name the same value. `UpgradeCode` is
fixed forever — changing it would strand every installed copy.

### The mail index on uninstall

The index is not owned by the installer. It can be several gigabytes, it survives reinstalls on
purpose, and `MINNE_DATA` may have moved it somewhere the MSI cannot guess. So the installer
does not delete files it never wrote — it asks the app to, by running `minne.exe --purge-data`
before removing it. The app resolves the same data directory it reads and shows the same dialog
as *Tools → Delete local data*.

| Uninstall route | UILevel | What happens to the index |
|---|---|---|
| Add/Remove Programs | 5 | the app asks; default is to keep |
| `msiexec /x {code} /qr` | 4 | the app asks |
| `winget uninstall`, `msiexec /x … /qn` | 2 | kept, silently |
| `msiexec /x {code} /qn REMOVEDATA=1` | 2 | deleted, no prompt |
| version upgrade | any | kept — `UPGRADINGPRODUCTCODE` excludes the upgrade's uninstall half |

## winget

The release workflow renders `winget/*.yaml` (substituting version, SHA-256, ProductCode and
release date) and uploads them as the `winget-manifests` build artifact. They are deliberately
*not* submitted automatically: winget-pkgs takes a pull request, the first one for a new package
gets human review, and an automated submission would need a fork plus a stored token.

To publish a release to winget, download that artifact and:

```
winget validate --manifest <folder>
wingetcreate submit --token <github-pat> <folder>     # or open the PR against microsoft/winget-pkgs by hand
```

`winget install EmilEklund.Minne` then downloads the MSI, checks it against `InstallerSha256`,
and runs it silently. That path also sidesteps the SmartScreen prompt a browser download gets:
the warning comes from the Mark-of-the-Web that browsers attach to downloads, and winget does
not attach one. It is not a substitute for code signing — the binaries are still unsigned — but
it is the difference between "Windows protected your PC" and one command.

## Code signing

Not done. An EV or Azure Trusted Signing certificate is the only thing that actually clears
SmartScreen for a downloaded, unsigned executable; until there is one, every asset ships with a
`.sha256` next to it and the recommended install path is winget.
