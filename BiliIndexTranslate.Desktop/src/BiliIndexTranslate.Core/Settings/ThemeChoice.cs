namespace BiliIndexTranslate.Core.Settings;

/// <summary>The palette a window set is drawn with.</summary>
public enum ThemeVariant
{
    Light,
    Dark,
    HighContrast,
}

/// <summary>Decides which palette to use. Pure, so the rule can be tested without Windows.</summary>
public static class ThemeChoice
{
    /// <summary>
    /// Windows high contrast always wins: its colors are the user's accessibility setting and override the app's own
    /// Light/Dark preference. Otherwise an explicit Light/Dark preference is used, and System follows the system app theme.
    /// </summary>
    public static ThemeVariant Resolve(ThemePreference preference, bool systemPrefersDark, bool highContrast)
    {
        if (highContrast) return ThemeVariant.HighContrast;
        return preference switch
        {
            ThemePreference.Dark => ThemeVariant.Dark,
            ThemePreference.Light => ThemeVariant.Light,
            _ => systemPrefersDark ? ThemeVariant.Dark : ThemeVariant.Light,
        };
    }
}
