# Changelog

All notable changes to Anemo Widget and Anemo Scanner are documented here, one section
per app since they're versioned and released independently (see [CLAUDE.md](CLAUDE.md)).
Dates are release dates, in UTC.

## Anemo Widget

> Shipped as **NetworkWidget** prior to 1.3.0 - see [Renamed](#renamed) below.

### 1.3.2 - 2026-09-18

#### Fixed
- Adapter selection no longer prefers Tailscale (or any other VPN/tunnel adapter) over a
  real Ethernet or WiFi connection, even when the VPN happens to carry a default route.

#### Added
- A per-adapter exclusion list in Settings, so specific adapters (e.g. Tailscale) can be
  hidden from the main window's adapter dropdown entirely.

### 1.3.1 - 2026-09-02

#### Added
- The current WiFi network's security type (e.g. `WPA3-Personal`) now shows in the WiFi
  section.

#### Removed
- Traceroute, since Anemo Scanner already has it and is the better home for it.

### 1.3.0 - 2026-08-14

#### Changed
- Renamed from **NetworkWidget** to **Anemo Widget** (window title, tray text, exe name,
  settings path, Velopack identity) - see
  [CHANGELOG.md#renamed](https://github.com/TheSingularis/anemo/blob/main/CHANGELOG.md#renamed)
  for the two-step history.
- New app icon (wind-mark design).

### 1.2.x and earlier - 2026-08-06 to 2026-08-14

Shipped under the name **NetworkWidget**, before Anemo Scanner existed and before the
Anemo rebrand.

- **1.2.11** - Suppressed Velopack's own update window and skipped the redundant
  post-update re-check.
- **1.2.10** - Version bump only, no functional changes.
- **1.2.9** - Version bump only, no functional changes.
- **1.2.8** - Fixed a cross-thread UI access bug that could silently abort update
  downloads mid-stream.
- **1.2.7** - Update-apply failures now surface to the user instead of being silently
  swallowed.
- **1.2.6** - Added a Traceroute window, later reworked with a resizable layout and
  per-hop RTT bars.
- **1.2.5** - Reworked the update flow with a proper pre-open splash and a real progress
  bar.
- **1.2.4** - WiFi polling is now gated to when the window is visible, at a faster,
  steadier rate.
- **1.2.3** - Added an update-progress window with a spinner for both startup and manual
  checks. Adopted the dev/main branching model used ever since.
- **1.2.2** - Added inline sparklines to the Download/Upload traffic rows.
- **1.2.1** - Ping results are now color-coded; traffic refresh decoupled to a steady 1s
  interval.
- **1.2.0** - Added periodic update checks, an update toast, and network metrics.
- **1.1.0** - Added Velopack-based self-update, a Settings pane (startup toggles,
  version display), start-with-Windows, and Ethernet-over-WiFi adapter prioritization.
- **1.0.0** - First tagged release: the original WPF tray widget, with CI/CD to build a
  self-contained exe and publish a release on version tags.

## Anemo Scanner

### 1.3.2 - 2026-09-18

#### Fixed
- Adapter selection no longer prefers Tailscale (or any other VPN/tunnel adapter) over a
  real Ethernet or WiFi connection (shared fix with Anemo Widget).

### 1.3.1 - 2026-09-02

#### Fixed
- Switching tabs mid-scan no longer freezes the window; a wide port-range scan now runs
  off the UI thread instead of blocking it.

#### Removed
- The "concept preview" label in the header, now that the app has a real release.

### 1.3.0 - 2026-08-14

First release. Full-size network scanning app sharing its core logic
(`Anemo.Core`) with Anemo Widget.

- Dashboard with live IP/gateway/WiFi cards, devices-online count, and a recent-activity
  feed.
- Devices: ping sweep + ARP + hostname + vendor lookup, streamed live as hosts respond;
  the scanning machine's own row is labeled "This device."
- WiFi Analyzer: nearby-network scan with a live 2.4GHz channel-congestion chart and a
  suggested channel.
- Port Scanner: concurrent TCP scan over a user-set range.
- Traceroute, using the same engine as the widget.
- Speed Test using the real LibreSpeed protocol, with a live throughput graph.
- Settings: persisted port-scanner defaults, version display.
- Auto-update via Velopack, on its own release channel isolated from the widget's.

## Renamed

This repo (and the widget's identity) went through two renames - see
[CLAUDE.md](CLAUDE.md#naming-history) for the full history:

1. `NetworkWidget`/`NetworkScanner` &rarr; `Anemo`/`Anemo Scanner` (2026-08-14).
2. `Anemo` (widget) &rarr; `Anemo Widget` (2026-08-14, same day, both shipped together
   in widget-v1.3.0) - purely a naming/identity change, no functional difference.
