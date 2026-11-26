# Architecture

## Boundaries

| Concern | Where | Notes |
|---|---|---|
| Selection observation | `App/Uia/UiaSelectionReader`, `App/Uia/UiaObserver` | UI Automation only. Focused element + ≤6 ancestors, `TextPattern.GetSelection()`, `GetText(max+1)`, `GetBoundingRectangles()`. No tree walks, no clipboard. |
| Input triggers | `App/Input/InputHookThread` | `WH_MOUSE_LL` / `WH_KEYBOARD_LL` on their own STA thread. Classify and post; never block, never swallow input (except wheel events over our own card). Optional `RegisterHotKey`. |
| Source-app tracking | `App/Tracking/SourceWindowTracker` | WinEvents filtered to the source pid (location, move/size, scrolling, minimize) + global foreground hook + 200 ms sanity tick. |
| Selection geometry | `Core/Selection/SelectionAnchor`, `Core/Selection/PopupPlacer`, `Core/Geometry/*` | Physical pixels end to end; per-monitor scale only for gap/margins/limits. Pure functions, unit-tested. |
| Popup lifecycle | `Core/Session/ReadingSession` (decides) + `App/AppHost` (executes) + `App/Popup/PopupPresenter` (window) | Event → effects state machine; every async thing carries an id. |
| Translation requests | `Core/Translation/*` | `EndpointResolver`, `PromptBuilder`, `DeeplxProtocol`, `CustomApiProtocol`, `OpenAiCompatibleTranslator`, `HttpTranslationTransport`, `TranslationCoordinator`. |
| Caching | `Core/Translation/TranslationCache` | LRU 500 / TTL 24 h, in-memory only. Key = text + target + model + effective endpoint + prompt version + source language. In the DeepLX format the model and the glossary are not part of the key, and the endpoint part is the URL as entered (the key is never in it). |
| Settings | `Core/Settings/*`, `App/Native/DpapiSecretProtector`, `App/Settings/SettingsWindow` | JSON + DPAPI blob, tolerant load, clamped ranges, schema migration. |
| Interface language | `Core/Localization/UiText`, `Core/Localization/InterfaceLanguage`, `App/Localization/Loc` | Text of both languages in one table with the same keys. XAML reads it through `{loc:Tr Key}`, a binding to `Loc`'s indexer, so open windows follow a language change; code uses `Loc.T` / `Loc.Bind`. A unit test checks that every key the app uses exists and that every key is used. |
| Diagnostics | `Core/Diagnostics/*`, `App/Diagnostics/DiagnosticsWindow` | 300-entry ring buffer, p50/p95 per timing category, per-app compatibility registry. |
| Tray / lifetime | `App/Tray/TrayIcon`, `App/Program`, `App/AppHost` | Single instance (mutex + named event), explicit shutdown, ordered disposal. |
| Updates / start-up | `Core/Updates/*`, `Core/Startup/AutoStartValue`, `App/Updates/UpdateService`, `App/Startup/AutoStart` | Velopack installer and updater; the Run-key entry for "Start with Windows". Rules that can be tested (schedule, value format, error text) are in Core. See "Updates and start-up" below. |

## Threads

* **UI thread** (WPF dispatcher): `ReadingSession`, effect execution, popup windows, tray, WinEvent
  callbacks, timers (stabilization, probe watchdog 2.5 s, refresh watchdog 1 s, geometry grace 400 ms).
* **uia-observer** (MTA): the only thread that touches UI Automation. `CUIAutomation8` with
  `ConnectionTimeout` 1500 ms / `TransactionTimeout` 2000 ms, event coalescing and connection
  recovery when available. Work arrives through three *latest-wins* slots (probe, refresh,
  focus re-subscription). A stalled provider blocks only this thread; the UI watchdog reports the
  probe as failed and ignores the late answer. No replacement threads are ever created.
* **input-hooks** (STA): low-level hooks + hotkey message loop. Callbacks only read a struct and
  post to the UI thread.
* **Thread pool**: HTTP via one long-lived `HttpClient` (`SocketsHttpHandler`, pooled connection
  lifetime 10 min), concurrency bounded to 2, 20 s timeout per request, per-request `CancellationTokenSource`.
  For a streamed reply (study cards, image translation) the timeout is an idle limit: it starts again
  with every line that arrives. A failure the service reports inside the stream is raised as an error.
  Streamed image-translation deltas reach the popup through `LatestValueRelay`: the UI thread is given
  the newest delta once it has handled pending rendering and input, and the deltas in between are skipped.

## State model

`Idle → SelectionPending → Loading → ShowingTranslation | ShowingError`, plus `Dismissed`
(hidden by the user or by loss of context; remembers the dismissed text + source so the same
selection stays quiet until it changes or clears).

Events: `TriggerReceived(MouseUp|KeyUp|UiaEvent|Hotkey)`, `StabilizationElapsed(timerId)`,
`ProbeCompleted(probeId, outcome)`, `TranslationCompleted(requestId, outcome)`,
`GeometryRefreshed(selectionId, update)`, `GeometryGraceElapsed`, `OutsideClick`, `EscapePressed`,
`ForegroundChanged(isSourceOrPopup)`, `SourceWindowLost`, `UserRetry`, `UserClosed`, `UserPinned`,
`SettingsChanged`, `Shutdown`.

Effects: timers, `RequestProbe`, `ShowLoading`, `StartTranslation`, `CancelTranslation`,
`ShowTranslation`, `ShowError`, `HidePopup(reason)`, `Begin/EndTracking`, `UpdateAnchor`,
`Start/CancelGeometryGrace`, `DetachPopup`, `Report`.

Rules that make it race-free:

* Timers, probes, translation requests and selections all have monotonically increasing ids issued
  by the session; anything that completes with a stale id is a no-op (tested).
* Selecting B while A is loading cancels A's request and starts B; A's late result cannot render.
* Dismissal (outside click, Esc, close, foreground change, source lost, geometry lost) cancels the
  in-flight request; a late result cannot reopen the card.
* Geometry lost applies only to selections with their own bounds: missing rects start a 400 ms grace
  and then hide the card. A card anchored at the pointer (`SelectionSnapshot.AnchoredAtPointer`:
  clipboard capture, or a selection reported without bounds) has nothing to follow, so missing
  geometry is ignored and it stays until one of the other dismissals.
* Cache hits are resolved synchronously inside `StartTranslation`, so `Loading → ShowingTranslation`
  happens within one dispatcher turn (no loading flash).
* The same selection probed again while visible only refreshes the anchor.
* Triggers coalesce: a new trigger restarts the single stabilization timer
  (mouse-up = delay, key-up = +30 ms, UIA event = +70 ms, hotkey = immediate). UIA selection events
  while the left button is held are ignored (mouse-up probes anyway).
* An automatic probe that comes back `Unsupported` or `Failed` while no card is shown is repeated
  once after `ProbeRetryDelayMs` (400 ms): a freshly started browser can answer before its page is in
  the accessibility tree. `NoSelection` is never retried, explicit requests use the clipboard
  fallback instead, and every new trigger gets its own single retry.
* `Shutdown` cancels everything and makes the session inert.

## Popup behaviour

`WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TOPMOST`, `ShowActivated=false`, `WM_MOUSEACTIVATE →
MA_NOACTIVATE`, every control `Focusable=False`. The window is reused; content is set, the window
is shown fully transparent, laid out, measured with `GetWindowRect`, placed with `SetWindowPos`
(physical pixels, `SWP_NOACTIVATE`), then faded in over 120 ms. `SizeChanged` re-places. Pinned
cards are detached instances (max 3) with manual drag via `SetWindowPos`.

Placement: horizontally centred on the uppermost visible selected line, 10 logical px above the
selection's union; below the lowest line if there is no room above; clamped to the monitor work
area with an 8 px margin; max width 440 logical px, max height 45 % of the work area, both scaled
per monitor. Mouse wheel over a card is intercepted by the low-level hook and forwarded to the card
so the source document never scrolls underneath it.

## Translation contract

`POST {base}/chat/completions`, `Authorization: Bearer {key}`, JSON body with a system prompt
(preserve meaning, tone, punctuation, line breaks; output only the translation; treat the text
strictly as content) and the selection as the user message, `stream: false`, `temperature 0.2`.
Response text is read from `choices[0].message.content` (string or text-part array). HTML, invalid
JSON, missing/empty content and non-2xx statuses become concise errors with the route
(direct/relay) attached; 412 carries a hint pointing at the relay setting. No retries, no automatic
route changes, no logging of text or keys.

DeepLX format (`apiFormat: Deeplx` in the settings file): `POST` to the endpoint exactly as entered,
with `{apiKey}` replaced by the escaped key (then no `Authorization` header) or, without the
placeholder, `Authorization: Bearer {key}`. Body `{"text", "source_lang", "target_lang"}`; the
translation is `data`. A non-200 `code` inside a 200 response is reported as that HTTP error. There
is no prompt, so the glossary is not applied, and Study Card and image translation fail with a
configuration error before any request is sent. Otherwise the same rules: one request, no retries,
no route changes.

Custom format (`apiFormat: Custom`): the request comes from a template in the settings
(`customMethod`, `customHeaders`, `customBody`) and the endpoint is a template too.
`CustomApiProtocol` fills in `{text}`, `{texts}`, `{apiKey}`, `{target}`, `{targetLang}` and
`{targetLangLower}`, escaping each value for where it lands, and reads the translation at
`customResultPath` (keys, `[n]`, `[*]`). The template is validated completely before anything is
sent; the request goes through `IRawTranslationTransport.SendAsync`, which adds no credential of
its own. The cache key holds a hash of method, headers, body and result path instead of a model.
Study Card and image translation are refused as for DeepLX.

### Streaming the selection card

Only the chat format streams, and only when the caller passes `onPartial` and the transport is an
`ISseTranslationTransport` (`stream: true`, same line reader and idle timeout as image translation).
DeepLX, Custom and transports without SSE behave exactly as before. After each delta the translator
computes the *displayable* text (`ChatCompletionParser.VisibleStreamingText`): nothing while a
`<think>` block is open or the text could still become one, otherwise what follows the last
`</think>`, trimmed. A partial is reported only when it is non-empty and differs from the last one.
The final text goes through the same `ChatCompletionParser.TryFinalize` (strip think, trim, empty →
`EmptyResult`) as a non-streamed body; a server that ignores `stream` and answers with JSON is parsed
the old way. Error mapping is unchanged (an error inside the stream becomes an HTTP error).

`TranslationCoordinator.Start(..., onPartial)` gives each request a `LatestValueRelay<string>` that
runs through `postPartial` (the app uses `DispatcherPriority.Background`; default is `post`). A
partial is delivered only while the request is still in `_active` with the same CTS; the relay is
cleared before the final result is posted and again when it is delivered, so a queued partial can never
draw over it. Cancelled or superseded requests deliver nothing. Only the final success is cached.

In the session, `TranslationProgress(requestId, text)` yields `ShowPartialTranslation` only while the
state is `Loading` and the id is current; the state stays `Loading`. Anything else is ignored with an
Info log. The popup shows the partial text with copy/pin disabled, the study button hidden and a
"streaming…" caption; the final `ShowTranslation` restores the normal card. Growth in height goes
through the existing `SizeChanged → Replace` path.

## Screen snip: cloud vision or local OCR

The App project targets `net10.0-windows10.0.19041.0` (with `SupportedOSPlatformVersion` 10.0.17763.0, Windows 10
1809) so that `Windows.Media.Ocr` and `Windows.Graphics.Imaging` are available; Core stays platform-independent.
The UI test project references the App for build order only (`SkipGetTargetFrameworkProperties`), so it keeps its own
target framework.

`VisionRouting.Decide(VisionMode, ApiFormat)` in Core returns Cloud, Local or Unsupported: Auto → Cloud for the chat
format and Local for DeepLX/Custom; Local → always Local; Cloud → Cloud for the chat format, otherwise Unsupported.
`VisionMode` is stored as a name in `settings.json`; `VisionModeJsonConverter` turns a missing, unknown or oddly typed
value into Auto instead of rejecting the whole file.

`AppHost.BeginVisionCapture` branches on it. Cloud is the old `RunVisionRequestAsync` path. Unsupported shows an error
card without a request. Local runs `RunLocalVisionAsync` under the same state as the cloud path: `_visionCts` cancels
it, and `_visionSeq` / `_visionActive` make every `Post` callback drop a result that arrives after the card was closed
or replaced (`DismissVisionPopup` and the retry key are unchanged). Steps: `WindowsOcr.RecognizeAsync` (PNG data URL →
`BitmapDecoder` over an `InMemoryRandomAccessStream` → `SoftwareBitmap`, scaled down in proportion when an edge exceeds
`OcrEngine.MaxImageDimension` → `OcrEngine.TryCreateFromUserProfileLanguages().RecognizeAsync`) → `OcrText.Join` →
`OpenAiCompatibleTranslator.TranslateAsync` with `onPartial` through a `LatestValueRelay<string>` (the same text
translation path as a selection, so DeepLX and Custom work and the chat format streams). The recognized text is the
snapshot's text, which the card shows as the original. Everything stays in memory; only lengths reach the log.

`OcrText.Join` (pure, tested) joins lines: CJK next to CJK without a space, a trailing `-` between two Latin letters is
dropped, otherwise one space, blank lines skipped. `OcrText.JoinWords` rebuilds a line from the engine's words, because
for Chinese and Japanese the engine separates words with spaces. A failed engine creation, an empty result, an
undecodable image or an exception becomes an `EmptyResult` error card (the hint to add an OCR language pack is part of
the message); exceptions are reported by type name only.

"Recognize screen text and copy" (`StartScreenOcrCopy`) uses the same capture and `WindowsOcr`, has its own
cancellation token (`_ocrCopyCts`, no popup, no `_visionSeq`), writes the text with `ClipboardFallback.TryWriteText` and
reports through one tray notice. It never touches the translation transport.

## Updates and start-up

**Packaging.** The release workflow publishes a self-contained, non-single-file win-x64 build and runs
`vpk pack` with pack id `BiliIndexTranslate.Desktop`. The installer puts the app in
`%LocalAppData%\BiliIndexTranslate.Desktop` (the exe runs from `current\`); settings and logs stay in
`%LocalAppData%\BiliIndexTranslate` (`Program.DataDirectory`). The two must never be the same folder,
or uninstalling and updating would touch the settings. `Program.Main` begins with
`VelopackApp.Build()....Run()`, before the single-instance check: the installer starts the exe with
special arguments and `Run()` handles them and exits.

**UpdateService** (App) wraps `UpdateManager` with a `GithubSource` for this repository: no access
token, no pre-releases, so only *published* releases are seen (a draft is invisible until published).
It is active only when `IsInstalled` is true; a portable zip or a development run never contacts
GitHub. Like the rest of `AppHost` it works on the UI thread: the check and the download run on the
thread pool, results come back through `Post`, and `Dispose` cancels whatever is running.

* *When.* `UpdateSchedule.Next(isInstalled, enabled, lastCheck, now)` (Core, pure) answers none / now /
  after a delay: 60 s after start, then 24 h after the last check. A `DispatcherTimer` carries the delay;
  the setting `AutoCheckForUpdates` (default on, so an older settings file needs no migration) turns it
  off, and `Configure` ignores an unchanged value so that unrelated settings saves do not restart the timer.
* *What.* A found version is downloaded at once. When the download ends, `UpdateReady` makes `AppHost` show
  the tray menu item "Restart and update" and one tray notice. An update downloaded in an earlier run
  (`UpdatePendingRestart`) shows the menu item without the notice.
* *Applying.* "Restart and update" disposes the host first (settings saved, hooks and tray gone) and then calls
  `ApplyUpdatesAndRestart`, which ends the process. A normal exit from the tray calls
  `WaitExitThenApplyUpdates(asset, silent: true, restart: false)` after the host is disposed: the updater
  installs once the process has gone, and nothing is started.
* *Manual check.* The Settings button calls `CheckNowAsync` and shows the outcome (`UpdateCheckOutcome`) in
  the window; only this path shows a failure to the user. Automatic checks write a diagnostics line only.
  Errors pass through `UpdateErrorText`, which keeps a short reason (an HTTP status or an exception name) and
  replaces every URL, so neither the log nor the window carries a token or a download address.

**Start with Windows** (`App/Startup/AutoStart`, value format in `Core/Startup/AutoStartValue`). The entry is
`HKCU\Software\Microsoft\Windows\CurrentVersion\Run`, value `BiliIndexTranslate`, the quoted
`Environment.ProcessPath`. The registry is the only source of truth: Settings reads it when it opens
(an entry that points to another exe counts as not set) and writes or deletes it on Save; nothing is stored in
`settings.json`. The path `...\current\BiliIndexTranslate.exe` is the same before and after an update. The
Velopack hook `OnBeforeUninstallFastCallback` deletes the value when it points to the exe being uninstalled.
