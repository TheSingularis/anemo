# NetworkWidget

This repo hosts two apps that share a common library:

- **NetworkWidget** (`NetworkWidget.csproj`) - the tray widget.
- **NetworkScanner** (`NetworkScanner/`) - a full-size network scanning app.
- **NetworkWidget.Core** (`NetworkWidget.Core/`) - shared logic (network info, Wi-Fi,
  ping/port/device scanning, traceroute, auto-update, DWM styling). Both apps reference
  it via `ProjectReference`; neither app should reimplement something Core already has.

## Branching and releases

- `dev` is the integration branch. All ongoing work (features, fixes) is committed and pushed to `dev`.
- `main` only ever advances together with a tagged release - `main` == the latest shipped state, always. Merge `dev` into `main` exactly when cutting a release, never as a standalone push.
- The two apps are versioned and released **independently** - each has its own `<Version>` in its own `.csproj`, and its own tag prefix:
  - Widget: `git tag widget-vX.Y.Z && git push origin widget-vX.Y.Z`
  - Scanner: `git tag scanner-vX.Y.Z && git push origin scanner-vX.Y.Z`
- Every merge to `main` bumps at least the patch version (`X.Y.Z` -> `X.Y.(Z+1)`) of whichever app is being released, in that app's own `.csproj`, as part of that merge. Minor/small changes (bug fixes, small feature additions) get a patch bump specifically, not a minor version bump. Reserve a minor bump for a clearly larger feature or a breaking change. A release for one app doesn't require bumping the other's version.
- Tagging happens as part of that same main-merge, only when explicitly asked to ship/release/send it for that specific app - not on every dev push.
- CI/CD: pushes to `dev` trigger a quick sanity build for *both* apps (plain exe artifacts, no packaging). Pushing a `widget-vX.Y.Z` or `scanner-vX.Y.Z` tag triggers the full Velopack release build (Setup.exe, portable zip, update feed) for just that app, and publishes a GitHub Release titled with the app name.

## Auto-update

Both apps auto-update via Velopack, sharing one implementation (`NetworkWidget.Core/AppUpdater.cs` + `UpdateProgressWindow`). They point at the same GitHub repo, but are packaged under **different Velopack channels** so their release feeds never collide:

- Widget: default channel (`win`) - **never change this**, already-installed copies expect `releases.win.json` and would silently stop finding updates otherwise.
- Scanner: explicit `scanner` channel (`vpk pack -c scanner`) - produces `releases.scanner.json`, isolated from the widget's feed.

If a third app is ever added to this repo, give it its own distinct channel too.
