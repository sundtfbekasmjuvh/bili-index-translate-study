using System.Reflection;
using System.Runtime.Versioning;
using System.Security;
using BiliIndexTranslate.Core.Diagnostics;

namespace BiliIndexTranslate.App.Popup;

/// <summary>
/// Lightweight, zero-dependency Windows SAPI5 speech helper using SSML <c>xml:lang</c> selection.
/// Calls are asynchronous (<c>SVSFlagsAsync | SVSFPurgeBeforeSpeak | SVSFIsXML</c>) so the UI thread
/// never blocks, and any failure on stripped OS builds degrades gracefully to a boolean false.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class SpeechPlayer : IDisposable
{
    private const int SvsFlagsAsync = 1;
    private const int SvsfPurgeBeforeSpeak = 2;
    private const int SvsfIsXml = 8;

    private object? _voice;
    private Type? _voiceType;
    private bool _unavailable;
    private bool _disposed;

    /// <summary>Diagnostics sink; failures log exception type and HResult only, never the spoken text.</summary>
    public DiagnosticsLog? Log { get; set; }

    public bool TrySpeak(string? text, string? bcp47Language, bool slow = false)
    {
        if (_disposed || _unavailable || string.IsNullOrWhiteSpace(text)) return false;

        try
        {
            if (_voice is null)
            {
                _voiceType = Type.GetTypeFromProgID("SAPI.SpVoice", throwOnError: false);
                if (_voiceType is null)
                {
                    _unavailable = true;
                    Log?.Warn("speech", "SAPI.SpVoice is not registered; speech disabled");
                    return false;
                }
                _voice = Activator.CreateInstance(_voiceType);
                if (_voice is null)
                {
                    _unavailable = true;
                    Log?.Warn("speech", "SAPI.SpVoice could not be created; speech disabled");
                    return false;
                }
            }

            var lang = NormalizeLanguageTag(bcp47Language);
            var escaped = SecurityElement.Escape(text.Trim()) ?? string.Empty;
            var body = slow ? $"<prosody rate='-30%'>{escaped}</prosody>" : escaped;
            var ssml = $"<speak version='1.0' xml:lang='{lang}'>{body}</speak>";

            _voiceType!.InvokeMember(
                "Speak",
                BindingFlags.InvokeMethod,
                binder: null,
                target: _voice,
                args: new object[] { ssml, SvsFlagsAsync | SvsfPurgeBeforeSpeak | SvsfIsXml },
                culture: System.Globalization.CultureInfo.CurrentCulture); // same as the former implicit null (current culture)
            return true;
        }
        catch (Exception ex)
        {
            Log?.Warn("speech", $"SSML speak failed ({ex.GetType().Name}, HResult 0x{ex.HResult:X8}); trying plain text");
            // Fallback: try plain text without SSML in case a custom SAPI engine rejects xml:lang.
            try
            {
                if (_voice is not null && _voiceType is not null)
                {
                    _voiceType.InvokeMember(
                        "Speak",
                        BindingFlags.InvokeMethod,
                        binder: null,
                        target: _voice,
                        args: new object[] { text.Trim(), SvsFlagsAsync | SvsfPurgeBeforeSpeak },
                        culture: System.Globalization.CultureInfo.CurrentCulture); // same as the former implicit null (current culture)
                    return true;
                }
            }
            catch (Exception ex2)
            {
                // Audio device or SAPI service unavailable.
                Log?.Warn("speech", $"Plain-text speak failed ({ex2.GetType().Name}, HResult 0x{ex2.HResult:X8})");
            }
            return false;
        }
    }

    public void Stop()
    {
        if (_disposed || _voice is null || _voiceType is null) return;
        try
        {
            _voiceType.InvokeMember(
                "Speak",
                BindingFlags.InvokeMethod,
                binder: null,
                target: _voice,
                args: new object[] { string.Empty, SvsfPurgeBeforeSpeak },
                culture: System.Globalization.CultureInfo.CurrentCulture); // same as the former implicit null (current culture)
        }
        catch (Exception)
        {
            // Ignore shutdown/stop errors.
        }
    }

    private static string NormalizeLanguageTag(string? raw)
    {
        var tag = (raw ?? string.Empty).Trim();
        if (tag.Length == 0) return "en-US";
        foreach (var ch in tag)
        {
            if (!(char.IsLetterOrDigit(ch) || ch == '-'))
                return "en-US";
        }
        return tag;
    }

    public void Dispose()
    {
        if (_disposed) return;
        Stop();
        _disposed = true;
        _voice = null;
        _voiceType = null;
    }
}
