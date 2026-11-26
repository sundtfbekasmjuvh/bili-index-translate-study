using BiliIndexTranslate.Core.Selection;
using BiliIndexTranslate.Core.Translation;

namespace BiliIndexTranslate.Core.Localization;

/// <summary>The small caption line of the translation card ("streaming… · cached · relay · near pointer").</summary>
public static class CardCaption
{
    /// <param name="language">Interface language ("en" or "zh-CN"), as <c>Loc.Instance.Language</c>.</param>
    /// <param name="route">The route that answered; null while the answer is not final (loading, streaming), then the relay setting decides.</param>
    /// <param name="useRelay">The relay setting, used when <paramref name="route"/> is null.</param>
    /// <returns>The caption parts joined with " · ", or null when there are none.</returns>
    public static string? Build(string? language, SelectionSnapshot snapshot, RequestRoute? route, bool fromCache, bool useRelay, bool streaming = false)
    {
        var parts = new List<string>(4);
        if (streaming) parts.Add(UiText.Translate(language, "Caption_Streaming"));
        if (fromCache) parts.Add(UiText.Translate(language, "Caption_Cached"));
        if (route == RequestRoute.Relay || (route is null && useRelay)) parts.Add(UiText.Translate(language, "Caption_Relay"));
        // The snapshot says where this card's anchor came from. (AppHost used to keep a "last probe was anchored at the
        // pointer" flag for this; it was set in the same places as AnchoredAtPointer, but the snapshot is the right source
        // for the card it belongs to, even when a newer probe has run since.)
        if (snapshot.Origin == SelectionOrigin.Clipboard) parts.Add(UiText.Translate(language, "Caption_Clipboard"));
        else if (snapshot.AnchoredAtPointer) parts.Add(UiText.Translate(language, "Caption_NearPointer"));
        return parts.Count == 0 ? null : string.Join(" · ", parts);
    }
}
