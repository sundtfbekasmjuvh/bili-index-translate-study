namespace BiliIndexTranslate.App.Input;

/// <summary>
/// Marker carried in <c>dwExtraInfo</c> by every key this app injects itself (the fallback Ctrl+C), so the
/// low-level keyboard hook can tell its own input apart from real keys and from keys injected by others.
/// </summary>
internal static class OwnInput
{
    public static readonly IntPtr Marker = new(0x42495431); // "BIT1"
}
