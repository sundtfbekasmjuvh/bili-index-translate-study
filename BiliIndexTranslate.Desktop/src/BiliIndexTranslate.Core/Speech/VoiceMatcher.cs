using System.Globalization;

namespace BiliIndexTranslate.Core.Speech;

/// <summary>A text-to-speech voice from any engine: an engine-specific id, a BCP-47 language tag and a display name.</summary>
public sealed record VoiceCandidate(string Id, string Language, string DisplayName);

/// <summary>
/// Chooses a voice for a language: exact language match, then the same primary subtag, otherwise none.
/// A voice of another language is never returned (it would read the text as garbage).
/// </summary>
public static class VoiceMatcher
{
    /// <summary>Index of the best voice in <paramref name="voices"/>, or -1 when none speaks the language.</summary>
    public static int Pick(IReadOnlyList<VoiceCandidate> voices, string? bcp47, string? defaultVoiceId)
    {
        var wanted = (bcp47 ?? string.Empty).Trim();
        if (wanted.Length == 0 || voices.Count == 0) return -1;

        var exact = Find(voices, defaultVoiceId, v => string.Equals(v.Language, wanted, StringComparison.OrdinalIgnoreCase));
        if (exact >= 0) return exact;

        var primary = Primary(wanted);
        return Find(voices, defaultVoiceId, v => string.Equals(Primary(v.Language), primary, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Maps a SAPI <c>Language</c> attribute (semicolon-separated hex LCIDs such as "411;409") to BCP-47 tags in order.
    /// Entries that are not hex or not a known culture are ignored.
    /// </summary>
    public static IReadOnlyList<string> LcidListToTags(string? lcidList)
    {
        var tags = new List<string>();
        if (string.IsNullOrWhiteSpace(lcidList)) return tags;
        foreach (var part in lcidList.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!int.TryParse(part, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var lcid) || lcid <= 0) continue;
            try
            {
                var name = CultureInfo.GetCultureInfo(lcid).Name;
                if (name.Length > 0 && !tags.Contains(name, StringComparer.OrdinalIgnoreCase)) tags.Add(name);
            }
            catch (CultureNotFoundException)
            {
                // Unknown LCID: skip.
            }
        }
        return tags;
    }

    private static int Find(IReadOnlyList<VoiceCandidate> voices, string? defaultVoiceId, Func<VoiceCandidate, bool> match)
    {
        var first = -1;
        for (var i = 0; i < voices.Count; i++)
        {
            if (!match(voices[i])) continue;
            if (defaultVoiceId is not null && string.Equals(voices[i].Id, defaultVoiceId, StringComparison.Ordinal)) return i;
            if (first < 0) first = i;
        }
        return first;
    }

    private static string Primary(string tag)
    {
        var dash = tag.IndexOf('-', StringComparison.Ordinal);
        return dash < 0 ? tag : tag[..dash];
    }
}
