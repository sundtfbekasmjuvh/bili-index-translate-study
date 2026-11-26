using System.Text.Json;
using System.Text.Json.Nodes;
using BiliIndexTranslate.Core.Settings;
using BiliIndexTranslate.Core.Translation;

namespace BiliIndexTranslate.Core.Diagnostics;

/// <summary>
/// The settings as they go into a diagnostics bundle. An allowlist: a property that is not named here is not
/// exported, so a new setting stays out until someone decides how to show it (a unit test enforces that).
/// Secrets, URL paths and queries, header values, the request body and the glossary text never leave.
/// </summary>
public static class SettingsRedactor
{
    /// <summary>Properties copied as they are.</summary>
    public static readonly IReadOnlyList<string> VerbatimProperties =
    [
        nameof(AppSettings.SchemaVersion), nameof(AppSettings.AutoTranslate), nameof(AppSettings.TargetLanguage),
        nameof(AppSettings.SourceLanguage), nameof(AppSettings.TextScale), nameof(AppSettings.Theme), nameof(AppSettings.UiLanguage),
        nameof(AppSettings.ShowOriginalByDefault), nameof(AppSettings.EditableFieldPolicy), nameof(AppSettings.ExcludedProcesses),
        nameof(AppSettings.ApiFormat), nameof(AppSettings.CustomMethod), nameof(AppSettings.CustomResultPath),
        nameof(AppSettings.CustomErrorPath), nameof(AppSettings.Model), nameof(AppSettings.VisionModel), nameof(AppSettings.VisionMode),
        nameof(AppSettings.UseRelay), nameof(AppSettings.HotkeyEnabled), nameof(AppSettings.Hotkey), nameof(AppSettings.SnipHotkeyEnabled),
        nameof(AppSettings.SnipHotkey), nameof(AppSettings.StabilizationDelayMs), nameof(AppSettings.MinChars), nameof(AppSettings.MaxChars),
        nameof(AppSettings.UseUiaSelectionEvents), nameof(AppSettings.DiagnosticsFileLogging), nameof(AppSettings.OnboardingCompleted),
        nameof(AppSettings.AutoCheckForUpdates),
    ];

    /// <summary>Properties exported in a reduced form (an origin, a masked list, a length).</summary>
    public static readonly IReadOnlyList<string> TransformedProperties =
    [
        nameof(AppSettings.BaseUrl), nameof(AppSettings.RelayPrefix), nameof(AppSettings.CustomHeaders),
        nameof(AppSettings.CustomBody), nameof(AppSettings.GlossaryText), nameof(AppSettings.CustomName),
    ];

    /// <summary>Properties that never appear in any form.</summary>
    public static readonly IReadOnlyList<string> ExcludedProperties = [nameof(AppSettings.ApiKey)];

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    public static string ToJson(AppSettings s)
    {
        ArgumentNullException.ThrowIfNull(s);
        var o = new JsonObject();
        Put(o, nameof(s.SchemaVersion), s.SchemaVersion);
        Put(o, nameof(s.AutoTranslate), s.AutoTranslate);
        Put(o, nameof(s.TargetLanguage), s.TargetLanguage);
        Put(o, nameof(s.SourceLanguage), s.SourceLanguage);
        Put(o, nameof(s.TextScale), s.TextScale);
        Put(o, nameof(s.Theme), s.Theme.ToString());
        Put(o, nameof(s.UiLanguage), s.UiLanguage);
        Put(o, nameof(s.ShowOriginalByDefault), s.ShowOriginalByDefault);
        Put(o, nameof(s.EditableFieldPolicy), s.EditableFieldPolicy.ToString());
        o[Name(nameof(s.ExcludedProcesses))] = new JsonArray((s.ExcludedProcesses ?? []).Select(p => (JsonNode?)JsonValue.Create(p)).ToArray());
        Put(o, nameof(s.ApiFormat), s.ApiFormat.ToString());
        Put(o, nameof(s.CustomName), CustomNameText(s.CustomName));
        Put(o, nameof(s.CustomMethod), s.CustomMethod);
        Put(o, nameof(s.CustomHeaders), MaskHeaders(s.CustomHeaders));
        Put(o, "CustomBodyLength", (s.CustomBody ?? string.Empty).Length);
        Put(o, nameof(s.CustomResultPath), s.CustomResultPath);
        Put(o, nameof(s.CustomErrorPath), s.CustomErrorPath);
        Put(o, nameof(s.BaseUrl), OriginOnly(s.BaseUrl));
        Put(o, nameof(s.Model), s.Model);
        Put(o, nameof(s.VisionModel), s.VisionModel);
        Put(o, nameof(s.VisionMode), s.VisionMode.ToString());
        Put(o, nameof(s.UseRelay), s.UseRelay);
        Put(o, nameof(s.RelayPrefix), OriginOnly(s.RelayPrefix));
        var glossary = s.GlossaryText ?? string.Empty;
        Put(o, "GlossaryLength", glossary.Length);
        Put(o, "GlossaryLines", glossary.Split('\n').Count(l => !string.IsNullOrWhiteSpace(l)));
        Put(o, nameof(s.HotkeyEnabled), s.HotkeyEnabled);
        Put(o, nameof(s.Hotkey), s.Hotkey);
        Put(o, nameof(s.SnipHotkeyEnabled), s.SnipHotkeyEnabled);
        Put(o, nameof(s.SnipHotkey), s.SnipHotkey);
        Put(o, nameof(s.StabilizationDelayMs), s.StabilizationDelayMs);
        Put(o, nameof(s.MinChars), s.MinChars);
        Put(o, nameof(s.MaxChars), s.MaxChars);
        Put(o, nameof(s.UseUiaSelectionEvents), s.UseUiaSelectionEvents);
        Put(o, nameof(s.DiagnosticsFileLogging), s.DiagnosticsFileLogging);
        Put(o, nameof(s.OnboardingCompleted), s.OnboardingCompleted);
        Put(o, nameof(s.AutoCheckForUpdates), s.AutoCheckForUpdates);
        return o.ToJsonString(WriteOptions);
    }

    /// <summary><c>scheme://host[:port]</c> of an http(s) URL; anything else is reduced to its length.</summary>
    internal static string OriginOnly(string? url)
    {
        if (string.IsNullOrEmpty(url)) return string.Empty;
        if (Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            return uri.Scheme + "://" + uri.Host + (uri.IsDefaultPort ? string.Empty : ":" + uri.Port.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return $"(not a URL, {url.Length} chars)";
    }

    internal static string MaskHeaders(string? headers)
    {
        if (string.IsNullOrWhiteSpace(headers)) return string.Empty;
        var lines = headers.Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .Select(l =>
            {
                var colon = l.IndexOf(':', StringComparison.Ordinal);
                return colon < 0 ? "***" : l[..colon].Trim() + ": ***";
            });
        return string.Join('\n', lines);
    }

    private static string CustomNameText(string? name)
    {
        if (string.IsNullOrEmpty(name)) return string.Empty;
        return CustomApiPreset.All.Any(p => p.Template.Name == name) ? name : $"({name.Length} chars)";
    }

    private static string Name(string property) => JsonNamingPolicy.CamelCase.ConvertName(property);

    private static void Put<T>(JsonObject o, string property, T value) => o[Name(property)] = JsonValue.Create(value);
}
