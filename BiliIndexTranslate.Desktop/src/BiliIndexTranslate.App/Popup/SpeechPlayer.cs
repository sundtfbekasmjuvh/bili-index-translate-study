using System.Runtime.Versioning;
using BiliIndexTranslate.Core.Diagnostics;
using BiliIndexTranslate.Core.Speech;
using Windows.Foundation;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Media.SpeechSynthesis;

namespace BiliIndexTranslate.App.Popup;

/// <summary>Outcome of <see cref="SpeechPlayer.TrySpeak"/>. Known synchronously; later synthesis errors are only logged.</summary>
internal enum SpeakResult
{
    /// <summary>A voice for the language was found and speech was started.</summary>
    Started,

    /// <summary>Neither engine has a voice for the language; nothing is spoken.</summary>
    NoVoice,

    /// <summary>Speech could not be started (no usable engine, empty text, disposed, or an error).</summary>
    Failed,
}

/// <summary>
/// Speaks text with the Windows voice that matches the requested language. WinRT
/// (<c>Windows.Media.SpeechSynthesis</c>, which sees the OneCore voices of installed language packs) is tried
/// first, then SAPI5 with a voice whose language matches. A voice of another language is never used.
/// All members are called on the UI thread. A new request or <see cref="Stop"/> bumps a generation counter and
/// cancels playback and any synthesis in flight, so audio never overlaps and never arrives late.
/// </summary>
[SupportedOSPlatform("windows10.0.17763.0")]
internal sealed class SpeechPlayer : IDisposable
{
    private const double SlowRateWinRt = 0.7;
    private const int SlowRateSapi = -3;
    private const int SvsFlagsAsync = 1;
    private const int SvsfPurgeBeforeSpeak = 2;

    private int _generation;
    private bool _disposed;
    private bool _winRtUnavailable;
    private bool _sapiUnavailable;

    private MediaPlayer? _player;
    private IAsyncOperation<SpeechSynthesisStream>? _pending;
    private SpeechSynthesisStream? _stream;
    private MediaSource? _source;
    private object? _sapi;

    /// <summary>Diagnostics sink; messages carry voice names and exception type and HResult, never the spoken text.</summary>
    public DiagnosticsLog? Log { get; set; }

    public SpeakResult TrySpeak(string? text, string? bcp47Language, bool slow = false)
    {
        if (_disposed || string.IsNullOrWhiteSpace(text)) return SpeakResult.Failed;
        try
        {
            StopCore(); // new generation; previous audio and synthesis are gone before anything else happens
            var generation = _generation;
            var lang = NormalizeLanguageTag(bcp47Language);
            var trimmed = text.Trim();

            var winRtFailed = false;
            if (!_winRtUnavailable)
            {
                try
                {
                    var voice = PickWinRtVoice(lang);
                    if (voice is not null)
                    {
                        Log?.Info("speech", $"winrt voice {voice.Language} ({voice.DisplayName})");
                        _ = SynthesizeAndPlayAsync(generation, voice, trimmed, slow);
                        return SpeakResult.Started;
                    }
                }
                catch (Exception ex)
                {
                    _winRtUnavailable = true;
                    winRtFailed = true;
                    Log?.Warn("speech", $"winrt speech unavailable ({ex.GetType().Name}, HResult 0x{ex.HResult:X8}); trying sapi");
                }
            }

            var sapi = TrySpeakSapi(trimmed, lang, slow);
            if (sapi is { } result) return result;

            Log?.Info("speech", $"no voice for {lang}");
            return winRtFailed && _sapiUnavailable ? SpeakResult.Failed : SpeakResult.NoVoice;
        }
        catch (Exception ex)
        {
            Log?.Warn("speech", $"speak failed ({ex.GetType().Name}, HResult 0x{ex.HResult:X8})");
            return SpeakResult.Failed;
        }
    }

    /// <summary>Stops playback and discards any synthesis still in flight.</summary>
    public void Stop()
    {
        if (_disposed) return;
        try
        {
            StopCore();
        }
        catch (Exception ex)
        {
            Log?.Warn("speech", $"stop failed ({ex.GetType().Name}, HResult 0x{ex.HResult:X8})");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        Stop();
        _disposed = true;
        try
        {
            _player?.Dispose();
        }
        catch (Exception)
        {
            // Shutdown: ignore.
        }
        _player = null;
        _sapi = null;
    }

    private void StopCore()
    {
        _generation++;

        var pending = _pending;
        _pending = null;
        try { pending?.Cancel(); } catch (Exception) { /* already completed or closed */ }

        if (_player is { } player)
        {
            try
            {
                player.Pause();
                player.Source = null;
            }
            catch (Exception) { /* player in a bad state: ignore */ }
        }
        ReleaseCurrent();

        if (_sapi is not null)
        {
            try { SapiPurge(_sapi); } catch (Exception) { /* ignore */ }
        }
    }

    private void ReleaseCurrent()
    {
        var source = _source;
        var stream = _stream;
        _source = null;
        _stream = null;
        try { source?.Dispose(); } catch (Exception) { /* ignore */ }
        try { stream?.Dispose(); } catch (Exception) { /* ignore */ }
    }

    // ---- WinRT -----------------------------------------------------------------------------------

    private static VoiceInformation? PickWinRtVoice(string lang)
    {
        var all = SpeechSynthesizer.AllVoices;
        var candidates = new List<VoiceCandidate>(all.Count);
        foreach (var v in all) candidates.Add(new VoiceCandidate(v.Id, v.Language, v.DisplayName));
        string? defaultId = null;
        try { defaultId = SpeechSynthesizer.DefaultVoice.Id; } catch (Exception) { /* no default voice */ }
        var index = VoiceMatcher.Pick(candidates, lang, defaultId);
        return index < 0 ? null : all[index];
    }

    private async Task SynthesizeAndPlayAsync(int generation, VoiceInformation voice, string text, bool slow)
    {
        SpeechSynthesizer? synth = null;
        SpeechSynthesisStream? stream = null;
        try
        {
            // One synthesizer per request: a cancelled request still in flight never shares Voice/Options with the next one.
            synth = new SpeechSynthesizer { Voice = voice };
            synth.Options.SpeakingRate = slow ? SlowRateWinRt : 1.0;

            var operation = synth.SynthesizeTextToStreamAsync(text);
            _pending = operation;
            stream = await operation; // resumes on the UI thread

            if (generation != _generation || _disposed)
            {
                return; // stale: Stop() or a newer request came first; the finally block disposes the stream
            }
            _pending = null;

            var source = MediaSource.CreateFromStream(stream, stream.ContentType);
            _stream = stream;
            _source = source;
            stream = null; // owned by the player state now

            var player = EnsurePlayer();
            player.Source = source;
            player.Play();
        }
        catch (OperationCanceledException)
        {
            // Cancelled by Stop() or a newer request.
        }
        catch (Exception ex)
        {
            Log?.Warn("speech", $"winrt synthesis failed ({ex.GetType().Name}, HResult 0x{ex.HResult:X8})");
        }
        finally
        {
            try { stream?.Dispose(); } catch (Exception) { /* ignore */ }
            try { synth?.Dispose(); } catch (Exception) { /* ignore */ }
        }
    }

    private MediaPlayer EnsurePlayer()
    {
        if (_player is { } existing) return existing;
        var player = new MediaPlayer { AutoPlay = false };
        player.MediaFailed += OnMediaFailed;
        _player = player;
        return player;
    }

    // Raised on a worker thread: only writes a diagnostics line, never touches WPF or the player state.
    private void OnMediaFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args)
    {
        try
        {
            Log?.Warn("speech", $"winrt playback failed ({args.Error}, HResult 0x{args.ExtendedErrorCode.HResult:X8})");
        }
        catch (Exception)
        {
            // Never throw from an event handler.
        }
    }

    // ---- SAPI ------------------------------------------------------------------------------------

    private static void SapiPurge(object sapi)
    {
        dynamic voice = sapi;
        voice.Speak(string.Empty, SvsfPurgeBeforeSpeak);
    }

    /// <summary>Speaks with a language-matched SAPI voice. Null when SAPI has no voice for the language or is unusable.</summary>
    private SpeakResult? TrySpeakSapi(string text, string lang, bool slow)
    {
        if (_sapiUnavailable) return null;
        try
        {
            if (_sapi is null)
            {
                var type = Type.GetTypeFromProgID("SAPI.SpVoice", throwOnError: false);
                if (type is null)
                {
                    _sapiUnavailable = true;
                    Log?.Warn("speech", "SAPI.SpVoice is not registered");
                    return null;
                }
                _sapi = Activator.CreateInstance(type);
                if (_sapi is null)
                {
                    _sapiUnavailable = true;
                    Log?.Warn("speech", "SAPI.SpVoice could not be created");
                    return null;
                }
            }

            dynamic sapi = _sapi;
            dynamic tokens = sapi.GetVoices();
            int count = tokens.Count;
            var candidates = new List<VoiceCandidate>();
            var tokenIndex = new List<int>();
            for (var i = 0; i < count; i++)
            {
                try
                {
                    dynamic token = tokens.Item(i);
                    string id = token.Id;
                    string name = string.Empty;
                    try { name = token.GetAttribute("Name") ?? string.Empty; } catch (Exception) { /* no name */ }
                    string? languages = null;
                    try { languages = token.GetAttribute("Language"); } catch (Exception) { /* no language */ }
                    foreach (var tag in VoiceMatcher.LcidListToTags(languages))
                    {
                        candidates.Add(new VoiceCandidate(id, tag, name));
                        tokenIndex.Add(i);
                    }
                }
                catch (Exception)
                {
                    // Unreadable token: skip it.
                }
            }

            string? defaultId = null;
            try { defaultId = sapi.Voice.Id; } catch (Exception) { /* no default voice */ }

            var picked = VoiceMatcher.Pick(candidates, lang, defaultId);
            if (picked < 0) return null;

            Log?.Info("speech", $"sapi voice {candidates[picked].Language} ({candidates[picked].DisplayName})");
            sapi.Speak(string.Empty, SvsfPurgeBeforeSpeak);
            sapi.Voice = tokens.Item(tokenIndex[picked]);
            sapi.Rate = slow ? SlowRateSapi : 0;
            sapi.Speak(text, SvsFlagsAsync | SvsfPurgeBeforeSpeak);
            return SpeakResult.Started;
        }
        catch (Exception ex)
        {
            Log?.Warn("speech", $"sapi speak failed ({ex.GetType().Name}, HResult 0x{ex.HResult:X8})");
            return SpeakResult.Failed;
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
}
