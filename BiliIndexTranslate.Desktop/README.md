# Bili Index Translate — Desktop

A quiet reading companion for Windows. Select text in another program; a small card with the
Chinese translation appears above the selection and goes away when you clear the selection, click
elsewhere, or press Esc. No visible window otherwise, no shortcut needed for normal use.

Built fresh in C# / .NET 8 (WPF for the card, UI Automation for reading selections).

> **Status.** The code compiles cleanly on Windows (0 warnings) and the platform-independent core —
> state machine, placement math, translation client, vision and study-card parsers, cache, settings —
> is covered by 225 passing unit tests. CI builds and tests every push and pull request on
> `windows-latest`. On a Windows 11 desktop the app starts, registers its hotkeys, enforces a single
> instance, opens Settings and exits cleanly from the tray. Selection reading, the popup card, the
> error card, cache hits, foreground and minimize dismissal and the unsupported-control path have
> been walked through against a local mock endpoint; the snip overlay, SAPI TTS and Chromium itself
> have not. `docs/TESTING.md` lists what is verified and what is still open.

## Run

Requirements: Windows 10 1809+ / Windows 11, [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0)
(x64). To build from source you need the .NET 8 SDK.

| What | How |
|---|---|
| Prebuilt (framework-dependent) | `publish\BiliIndexTranslate.exe` |
| Build and run from source | `tools\run.cmd` (add `--welcome` to force the welcome window) |
| Build a release folder | `tools\build.cmd` → `publish\` (runs the tests first) |
| Self-contained single exe (no runtime install) | `tools\build-selfcontained.cmd` → `publish-sc\` (~139 MB) |
| Unit tests | `tools\test.cmd` or `dotnet test` |

If the settings file cannot be written, the Settings window says so and stays open, and a
background save reports it once through a tray notice; [docs/TESTING.md](docs/TESTING.md) describes
one cause (a checkout under a directory with a Low integrity label).

The app lives in the notification area. Right-click the icon for: automatic translation on/off,
"Don't translate in *app*", translate clipboard text, Settings, Diagnostics, Welcome, Exit.
A second instance does not start twice; it opens the Settings window of the running one.

## Using it

1. Select text with the mouse (or Shift+arrows / Ctrl+A) in a browser, editor, or document.
2. After the selection settles (~250 ms), the card appears above the first selected line
   (below if there is no room), translated into Simplified Chinese by default.
3. Clear the selection, click anywhere outside the card, or press Esc to dismiss it.
   The same selection does not reopen the card until you select something else.
4. Hover the card for controls: show original text, copy, keep open (pin), close. Click **`学习卡片`**
   in the card footer to open the ephemeral companion Language Study Card beside the translation
   (progressive SSE streaming, `9B深度` ↔ `⚡ 2B极速` model toggle, normal + `0.7x` slow shadowing
   pronunciation via Windows SAPI, IPA/reading, register badge, synonym/usage nuance,
   etymology/mnemonic, collocations, bilingual example, sentence grammar note, pageable key-vocab
   flashcards, and `自测翻转` active recall).
   Ctrl + mouse wheel over a card changes the text size (remembered).

Optional fallback: `Alt+Q` (configurable, can be disabled) asks for a translation on demand. If
UI Automation cannot read the selection, this — and only this — sends a single Ctrl+C and
translates the copied text. Automatic mode never touches the clipboard.

## Settings (tray → Settings…)

* **Reading** – automatic translation, target language (简体中文 / 繁體中文 / English / 日本語 / 한국어),
  text size, light/dark/system theme, editable-field policy (default: skip single-line inputs such
  as address bars; multi-line editors are translated), show original by default.
* **Service** – API format (OpenAI-compatible by default, DeepLX-compatible, or a custom JSON template, see below),
  endpoint (base URL *or* full `/chat/completions` URL; the suffix is never
  duplicated), API key (stored with DPAPI for your Windows account), model
  (`Index-Translate-2B` / `9B` / `35B-A3B`, default 9B, free text for other OpenAI-compatible
  servers), relay toggle. **Save and test** saves what is on screen and tests exactly that.
  HTTP 412 from the direct endpoint is shown as a rejection with a hint about the relay; the app
  never switches routes by itself.
* **Applications** – per-process exclusions (also reachable from the tray menu).
* **Fallback shortcut** – enable/disable, change the key. Registration conflicts are reported in
  the tray and in Settings; automatic mode is unaffected.
* **Advanced** – settle delay (120–800 ms), UI Automation selection events on/off, diagnostics file
  logging.

**DeepLX format.** The endpoint is the complete translate URL and is used as entered, for example
`https://host/translate?token={apiKey}` or `http://localhost:1188/translate`. `{apiKey}` is replaced
with the API key; without the placeholder the key is sent as a Bearer token, and an empty key sends
no credential. The request body is
`{"text", "source_lang", "target_lang"}` and the translation is read from `data`; an HTTP error or
a non-200 `code` in the body is shown as an error. This format only translates selected text: the
glossary and the model choices have no effect, and Study Card and screen snip report that they need
an OpenAI-compatible endpoint without sending a request. With the relay enabled, the relay URL must
be the complete relayed translate URL.

**Custom format.** Any service that answers with JSON can be described by a template instead of by
code: the endpoint (it may carry placeholders), the method (`POST` or `GET`), request headers (one
`Name: value` per line), a request body, and a result path that says where the translation is in
the reply (`data`, `translations[0].text`); an optional error path names where the service puts its
error message. Placeholders: `{text}`, `{texts}` (the text as a JSON array, body only), `{apiKey}`,
`{target}` (`zh-Hans`, `en`), `{targetLang}` / `{targetLangLower}` (`ZH` / `zh`). Values are
URL-encoded in the endpoint and in form bodies, JSON-escaped in JSON bodies and stripped of line
breaks in headers. Presets for the DeepL API (Free / Pro) and LibreTranslate fill in the form. A
template that cannot work is rejected when saving. Like DeepLX, a custom API only translates
selected text.

Settings file: `%LocalAppData%\BiliIndexTranslate\settings.json`. Malformed files are backed up as
`settings.json.bad` and replaced with defaults; older files with a plain-text key are migrated.

## Privacy

Only the selected text and request metadata (model, target language) are sent to the configured
endpoint. No telemetry, no history, no persisted cache; diagnostics never contain selected text,
translations, or keys (only lengths and a short fingerprint).

## Project layout

```
src/BiliIndexTranslate.Core   platform-independent: session state machine, geometry/placement,
                              translation client, cache, settings, diagnostics (net8.0)
src/BiliIndexTranslate.App    WPF app: UIA observer, input hooks, popup, tray, settings UI (net8.0-windows)
tests/…Core.Tests             xunit tests for Core (run on any OS)
tools/                        run.cmd, build.cmd, build-selfcontained.cmd, test.cmd, make-icon.py
docs/ARCHITECTURE.md          boundaries, threads, state model, race handling
docs/TESTING.md               18 validation scenarios, app compatibility table, measurements
```

## Upgrading

Replace the `publish` folder (or rebuild). Settings and the encrypted key are kept; the schema is
versioned and unknown fields are ignored. Nothing else is written to disk unless file logging is on.
