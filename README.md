# Anemo

A small always-on-top desktop widget + tray icon for Windows showing live
IPv4 config and WiFi status, with a one-click Release & Renew button.

This repo also hosts **Anemo Scanner** (`Anemo.Scanner/`), a full-size network
scanning app (device discovery, Wi-Fi analyzer, port scanner, speed test,
traceroute) that shares its core logic with the widget via `Anemo.Core/`.

## Requirements to build

- .NET 8 SDK (Windows desktop workload): https://dotnet.microsoft.com/download/dotnet/8.0
- Windows 10/11 (WPF + WinForms interop, so this only builds/runs on Windows,
  not in this Linux sandbox)

## Build

From this folder, in a normal (non-admin) terminal:

```
dotnet build -c Release
```

The exe will land in `bin\Release\net8.0-windows\Anemo.Widget.exe`.

## Publish as a single self-contained exe (recommended for GravityZone)

```
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

Output lands in `bin\Release\net8.0-windows\win-x64\publish\Anemo.Widget.exe`.
This is a real compiled .NET binary rather than a PS2EXE wrapper around a
script, so it should read very differently to AV heuristics that flag
PS2EXE-packed executables specifically. If GravityZone still flags it, the
usual next step is submitting it to Bitdefender for whitelisting/false-positive
review with a code-signing certificate attached, same as you did with
NetworkMonitorGUI, since a signed binary from a known publisher carries more
trust than an unsigned one regardless of language.

## Notes

- The app itself runs unelevated (`app.manifest` requests `asInvoker`). Only
  clicking "Release & Renew IP" triggers a UAC prompt, since only that action
  actually needs admin rights.
- Closing the window (X) hides it to the tray instead of exiting. Use the
  tray icon's right-click menu to fully exit.
- Real RSSI in dBm only shows if your Windows build exposes it directly via
  `netsh wlan show interfaces` (newer Windows 11 builds). Otherwise the
  widget shows an approximate dBm value calculated from signal %, clearly
  labeled "(approx)".
- To run it automatically at login, drop a shortcut to the published exe in
  `shell:startup`.
