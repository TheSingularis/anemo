# Anemo

This repo hosts two apps that share a common library:

- **Anemo** (`Anemo.csproj`) - the tray widget. Flagship app, keeps the bare product name.
- **Anemo Scanner** (`Anemo.Scanner/`) - a full-size network scanning app.
- **Anemo.Core** (`Anemo.Core/`) - shared logic (network info, Wi-Fi, ping/port/device
  scanning, traceroute, auto-update, DWM styling). Both apps reference it via
  `ProjectReference`; neither app should reimplement something Core already has.

## Branching and releases

- `dev` is the integration branch. All ongoing work (features, fixes) is committed and pushed to `dev`.
- `main` only ever advances together with a tagged release - `main` == the latest shipped state, always. Merge `dev` into `main` exactly when cutting a release, never as a standalone push.
- The two apps are versioned and released **independently** - each has its own `<Version>` in its own `.csproj`, and its own tag prefix:
  - Widget: `git tag widget-vX.Y.Z && git push origin widget-vX.Y.Z`
  - Scanner: `git tag scanner-vX.Y.Z && git push origin scanner-vX.Y.Z`
- **Major.minor (`X.Y`) stays aligned between the two apps; patch (`Z`) is independent.** Whenever either app takes a major or minor bump, bump the *other* app's `X.Y` to match at the same time (its patch can stay put or reset to 0 - doesn't matter, patch isn't kept in sync). This keeps "1.3" meaning the same release generation for both apps without forcing them to release in lockstep - one app can ship several patch releases while the other sits still, and they're still both understood as "the 1.3 line."
- Every merge to `main` bumps at least the patch version (`X.Y.Z` -> `X.Y.(Z+1)`) of whichever app is being released, in that app's own `.csproj`, as part of that merge. Minor/small changes (bug fixes, small feature additions) get a patch bump specifically, not a minor version bump. Reserve a minor bump for a clearly larger feature or a breaking change - and remember to bump the other app's major.minor too when this happens. A patch release for one app doesn't require bumping the other's version at all.
- Tagging happens as part of that same main-merge, only when explicitly asked to ship/release/send it for that specific app - not on every dev push.
- CI/CD: pushes to `dev` trigger a quick sanity build for *both* apps (plain exe artifacts, no packaging). Pushing a `widget-vX.Y.Z` or `scanner-vX.Y.Z` tag triggers the full Velopack release build (Setup.exe, portable zip, update feed) for just that app, and publishes a GitHub Release titled with the app name.

## Naming history

This repo (and the widget's AssemblyName/Velopack AppId) was renamed from `NetworkWidget`
to `Anemo`, and `NetworkScanner` to `Anemo.Scanner`. This was a deliberate full cutover -
namespaces, assembly names, and Velopack AppIds all changed together, done early
specifically to avoid a messier migration later once there's real adoption. One direct
consequence: any copy of the widget installed *before* the rename has a different Velopack
AppId than anything shipped after, so it can't auto-update across the rename - a fresh
manual install is required to get onto the new AppId. There's no ongoing legacy-compat
concern from this beyond that.

## Auto-update

Both apps auto-update via Velopack, sharing one implementation (`Anemo.Core/AppUpdater.cs` + `UpdateProgressWindow`). They point at the same GitHub repo, but are packaged under **different Velopack channels** so their release feeds never collide:

- Widget: default channel (`win`) - keep it there. Every install from this point forward expects `releases.win.json`; changing it later would silently break auto-update for everyone who installed since the rename, the same way the rename itself just did to pre-rename installs.
- Scanner: explicit `scanner` channel (`vpk pack -c scanner`) - produces `releases.scanner.json`, isolated from the widget's feed.

If a third app is ever added to this repo, give it its own distinct channel too.
