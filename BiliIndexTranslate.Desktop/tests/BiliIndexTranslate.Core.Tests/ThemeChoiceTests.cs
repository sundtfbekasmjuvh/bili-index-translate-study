using BiliIndexTranslate.Core.Settings;
using Xunit;

namespace BiliIndexTranslate.Core.Tests;

public class ThemeChoiceTests
{
    [Theory]
    [InlineData(ThemePreference.System, false, ThemeVariant.Light)]
    [InlineData(ThemePreference.System, true, ThemeVariant.Dark)]
    [InlineData(ThemePreference.Light, false, ThemeVariant.Light)]
    [InlineData(ThemePreference.Light, true, ThemeVariant.Light)]
    [InlineData(ThemePreference.Dark, false, ThemeVariant.Dark)]
    [InlineData(ThemePreference.Dark, true, ThemeVariant.Dark)]
    public void Without_high_contrast_the_preference_decides(ThemePreference preference, bool systemDark, ThemeVariant expected)
        => Assert.Equal(expected, ThemeChoice.Resolve(preference, systemDark, highContrast: false));

    [Theory]
    [InlineData(ThemePreference.System, false)]
    [InlineData(ThemePreference.System, true)]
    [InlineData(ThemePreference.Light, false)]
    [InlineData(ThemePreference.Light, true)]
    [InlineData(ThemePreference.Dark, false)]
    [InlineData(ThemePreference.Dark, true)]
    public void High_contrast_wins_over_every_preference(ThemePreference preference, bool systemDark)
        => Assert.Equal(ThemeVariant.HighContrast, ThemeChoice.Resolve(preference, systemDark, highContrast: true));
}
