# Testing

## Measured on Windows (2026-11-06)

Windows 11 Pro for Workstations (build 26200), .NET SDK 8.0.427, from the v1.4.0 sources.

| Check | Result |
|---|---|
| `dotnet build BiliIndexTranslate.sln -c Release` | 0 errors, 0 warnings |
| `dotnet test tests/BiliIndexTranslate.Core.Tests -c Release` | **123 passed, 0 failed** (session state machine, placement/anchoring, endpoint resolution, response parsing, vision dual output, study cards, cache, coordinator races, settings load/migrate) |
| `dotnet publish … -r win-x64 --self-contained false` | `BiliIndexTranslate.exe` — PE32+ x64 GUI, version 1.4.0 embedded (864 KB folder) |
| `dotnet publish … --self-contained true -p:PublishSingleFile=true` | single 139 MB exe produced |
| Icon | `tools/make-icon.py` output inspected at 256 px |

Start-up reaches `ready` about 60 ms after `file logging enabled`. The idle process uses
18.5 MB private memory and 569 handles.

### End-to-end walkthrough (2026-11-06)

The app was driven against a self-built WPF source window (read-only multi-line `TextBox`)
and a local mock upstream on `127.0.0.1`, started from the framework-dependent publish output
with an isolated `USERPROFILE`, `diagnosticsFileLogging=true`, onboarding completed and both
hotkeys disabled. The source window is **not** a browser — Chromium's own UIA surface was not
exercised.

| # | Scenario | Result |
|---|---|---|
| 1 | Select text in a read-only multi-line text box | ✅ popup shown; probe `60 chars, 1 line(s), 12.0 ms`; one upstream request; card rect `230,76,686,212` |
| 2 | Click outside the card | ✅ `hidden: OutsideClick`; card gone 1.2 s later |
| 3 | Slow upstream (2.5 s) | ✅ loading state (`Translating`) observed before the result; `translate ok #14 via Direct 2501 ms` |
| 4 | HTTP 412 | ✅ error card: `Request rejected by the service (HTTP 412).` + hint about the relay; `Retry` and `Open settings` present; log `fail #18 Http 412 via Direct` |
| 5 | Control without a text pattern | ✅ `probe unsupported: no text pattern near focus (control type 50000, …)`; no text in the log line, no card |
| 6 | Foreground moves to another window | ✅ `hidden: ForegroundChanged` |
| 7 | Source window minimized | ✅ `track source minimized` → `hidden: SourceWindowLost` |
| 8 | Same selection repeated | ✅ first request sent, second served from the cache (0 upstream requests) |
| 9 | 200 repeated selections | ✅ 19.5 s, 20 samples, no upward drift |

Probe latency over the run was 2.4–12.7 ms; the two translation paths measured 1 ms (cache
warm mock) and 67 ms against the mock upstream.

### Resource behaviour over 200 selections

Sampled every 10th selection of the app process:

| Metric | First sample | Last sample | Note |
|---|---|---|---|
| Handles | 569 | 576 | one reused popup window; no per-selection window leak |
| USER objects | 64 | 66 | flat after the first few selections |
| GDI objects | 17 | 17 | flat |
| Private bytes | 18.5 MB | 19.0 MB | +0.5 MB over 200 selections |
| Working set | 83.7 MB | 85.0 MB | flat |

The 200 selections used the same source text, so most were served from the translation cache;
this measures the UI/UIA path, not 200 upstream round trips.

Timing targets (200–350 ms settle, prompt loading, cache hits without network) are design
parameters, not results. The Diagnostics window reports p50/p95 per category while the app
runs — the categories are `Detect`, `Probe`, `Cache`, `Render`, `Http` and `Refresh`.

If `dotnet test` aborts on Windows with `Win32Exception (5): Access is denied` while the test host
looks up its parent process, security software is blocking the test host in `bin\`. Point the
output somewhere else, for example `dotnet test … -o %TEMP%\bit-tests`.

## Scenarios

Status key: ✅ verified on Windows · ◐ partial · ❌ failed · ⬜ not tested yet.
Core-only logic that is covered by unit tests is marked (unit).

| # | Scenario | Expected | Status |
|---|---|---|---|
| 1 | Select a sentence in a browser | Card appears above the selection within settle delay + request time; no focus change in the browser | ◐ verified in a WPF text box (probe + card + upstream request); Chromium itself not tested |
| 2 | Click to clear the selection | Card hides immediately on mouse-down; no reopen on mouse-up (probe → no selection) | ⬜ (session: unit) |
| 3 | Select A, then B while A is loading | A's request cancelled, card shows loading for B, A's late result never rendered | ⬜ (session + coordinator: unit) |
| 4 | Dismiss while a request is pending | Card stays hidden when the response arrives | ⬜ (unit) |
| 5 | Re-select the same text after dismissing | Stays quiet until a different selection or a clear | ◐ repeating a selection is served from the cache; the dismiss-then-reselect path is unit-tested only |
| 6 | Move the pointer into the card | Card remains; source keeps focus and selection | ⬜ |
| 7 | Copy from the card | Clipboard updated; source window not activated | ⬜ |
| 8 | Long translation | Card capped at 45 % of work area height, wheel scrolls the card (hook forwards wheel), source does not scroll | ⬜ |
| 9 | Selection at screen edges | Flips below when no room above; clamped to the work area with 8 px margin | ⬜ (unit) |
| 10 | Second monitor with different DPI | Card appears on the selection's monitor, sized with that monitor's scale; negative coordinates handled | ⬜ (unit for math) |
| 11 | Move / minimize the source window | Card follows on move (WinEvent → refresh); minimize hides it | ◐ minimize hides it (`source minimized` → `hidden: SourceWindowLost`); the move-follow refresh not exercised |
| 12 | Switch to another app (Alt+Tab or click) | Card hides; returning does not reopen it | ✅ `hidden: ForegroundChanged` when another window took the foreground |
| 13 | Unsupported control (no TextPattern) | Nothing appears; Diagnostics → Applications shows "unsupported", log line without text | ✅ `probe unsupported: no text pattern near focus (control type 50000, …)`, no text in the log, no card |
| 14 | Stalled UIA provider | Probe watchdog (2.5 s) reports failure, observer thread recovers on next request, no replacement threads | ⬜ |
| 15 | HTTP 412 / timeout / invalid JSON / empty output | Error card with concise message + route (direct/relay); no HTML rendered; no retry loop | ✅ HTTP 412 mapped to `Request rejected by the service (HTTP 412).` with the relay hint and both action buttons; other status codes are unit-tested |
| 16 | Start a second instance | Second process exits; running instance opens Settings | ✅ second process exited with code 0 after 234 ms; log `second instance signalled; opening settings`; Settings window visible |
| 17 | Exit from tray | Hooks, WinEvent hooks, UIA handlers, HttpClient, windows and tray icon released; process ends | ◐ tray menu → Exit: log `exit requested` → `observer thread stopped` → `stopped` in 2.1 s, process gone; individual handle release not inspected |
| 18 | 200 repeated selections | Stable handle count and memory (single reused popup window, bounded caches/logs) | ✅ handles 569 → 576, USER 64 → 66, GDI flat at 17, private bytes +0.5 MB over 200 selections (cache-served, see above) |

## Applications

| App | Mechanism expected | Status |
|---|---|---|
| Self-built WPF text box (test harness) | UIA TextPattern on the read-only editor | ✅ selection read in 2.4–12.7 ms, card and upstream request confirmed |
| Chrome / Edge (web page) | UIA TextPattern on the document (Chromium exposes it when a screen reader / UIA client is active; first probe may be slow) | ⬜ not tested |
| Firefox | UIA TextPattern (Firefox's UIA support improved in 2024–2025 builds; older builds may be unsupported) | ⬜ not tested |
| Notepad (Windows 11) | TextPattern on the editor control | ⬜ not tested |
| Word | TextPattern on the document body | ⬜ not tested |
| VS Code / Electron editors | Monaco exposes a limited UIA text surface; may read as unsupported → use Alt+Q fallback | ⬜ not tested |
| Adobe Acrobat / PDF viewers | Highly variable; Edge PDF viewer behaves like a web page | ⬜ not tested |
| Single-line edit fields | Skipped by default (policy), unless enabled in Settings | ⬜ (policy: unit) |
| Password fields | Always skipped | ⬜ (policy: unit) |
| Console windows, games, custom-drawn controls | Unsupported; quiet failure | ✅ confirmed for a control without a text pattern |

## How to run the checks on Windows

1. `tools\run.cmd` — complete the welcome window (it has a demo paragraph for the first selection).
2. Open **Diagnostics…** from the tray and keep it on a second monitor if possible; it updates live.
3. Walk through the scenarios above; note p50/p95 from the Timings section after ~20 selections.
4. For scenario 14 use a UIA-heavy app under load, or temporarily set `ConnectionTimeout` lower in
   `UiaObserver` and probe a frozen window (`Suspend` it with Process Explorer).
5. For scenario 18 watch *Handles*, *USER Objects*, *GDI Objects* and *Private Bytes* in Task Manager
   / Process Explorer.