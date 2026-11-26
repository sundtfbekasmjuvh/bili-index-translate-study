using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using BiliIndexTranslate.Core.Settings;
using Microsoft.Win32;

namespace BiliIndexTranslate.App.Themes;

/// <summary>
/// Swaps the palette dictionary; every brush in the UI is a DynamicResource so windows update live.
/// Windows high contrast wins over the Light/Dark preference and is followed while the app runs.
/// </summary>
internal sealed class ThemeManager : IDisposable
{
    private static readonly Uri LightUri = new("pack://application:,,,/BiliIndexTranslate;component/Themes/Light.xaml");
    private static readonly Uri DarkUri = new("pack://application:,,,/BiliIndexTranslate;component/Themes/Dark.xaml");
    private static readonly Uri HighContrastUri = new("pack://application:,,,/BiliIndexTranslate;component/Themes/HighContrast.xaml");

    private readonly Application _app;
    private ResourceDictionary? _palette;
    private ThemeVariant? _variant;
    private ThemePreference _preference = ThemePreference.System;
    private bool _listening;

    public ThemeManager(Application app)
    {
        _app = app;
    }

    /// <summary>Receives one line (without the category) each time the applied theme changes.</summary>
    public Action<string>? Log { get; set; }

    public bool IsDark => _variant == ThemeVariant.Dark;
    public bool IsHighContrast => _variant == ThemeVariant.HighContrast;

    public void Apply(ThemePreference preference)
    {
        _preference = preference;
        var variant = ThemeChoice.Resolve(preference, SystemPrefersDark(), SystemHighContrast());
        if (_palette is null || variant != _variant)
        {
            var next = new ResourceDictionary
            {
                Source = variant switch
                {
                    ThemeVariant.HighContrast => HighContrastUri,
                    ThemeVariant.Dark => DarkUri,
                    _ => LightUri,
                },
            };
            if (_palette is not null) _app.Resources.MergedDictionaries.Remove(_palette);
            _app.Resources.MergedDictionaries.Add(next);
            _palette = next;
            _variant = variant;
            Log?.Invoke(variant switch
            {
                ThemeVariant.HighContrast => "high contrast",
                ThemeVariant.Dark => "dark",
                _ => "light",
            });
        }

        if (!_listening)
        {
            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
            SystemParameters.StaticPropertyChanged += OnSystemParameterChanged;
            _listening = true;
        }
    }

    private void Reapply() => _app.Dispatcher.BeginInvoke(() => Apply(_preference));

    // Accessibility: the high-contrast switch. Color / General / VisualStyle: the Light/Dark choice and high-contrast color changes.
    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category == UserPreferenceCategory.Accessibility)
            _app.Dispatcher.BeginInvoke(() => Log?.Invoke($"accessibility setting changed (high contrast {(SystemHighContrast() ? "on" : "off")})"));
        if (e.Category is UserPreferenceCategory.Accessibility or UserPreferenceCategory.General
            or UserPreferenceCategory.Color or UserPreferenceCategory.VisualStyle)
            Reapply();
    }

    /// <summary>
    /// Windows high contrast, read from the system each time: <see cref="SystemParameters.HighContrast"/> is cached by
    /// WPF and can still hold the old value when the change notification arrives.
    /// </summary>
    public static bool SystemHighContrast()
    {
        var hc = new HIGHCONTRAST { cbSize = (uint)Marshal.SizeOf<HIGHCONTRAST>() };
        return SystemParametersInfo(SPI_GETHIGHCONTRAST, hc.cbSize, ref hc, 0)
            ? (hc.dwFlags & HCF_HIGHCONTRASTON) != 0
            : SystemParameters.HighContrast;
    }

    private const uint SPI_GETHIGHCONTRAST = 0x0042;
    private const uint HCF_HIGHCONTRASTON = 0x00000001;

    [StructLayout(LayoutKind.Sequential)]
    private struct HIGHCONTRAST
    {
        public uint cbSize;
        public uint dwFlags;
        public IntPtr lpszDefaultScheme;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(uint uiAction, uint uiParam, ref HIGHCONTRAST pvParam, uint fWinIni);

    // SystemParameters caches HighContrast; this event fires after WPF has refreshed the cache.
    private void OnSystemParameterChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SystemParameters.HighContrast)) Reapply();
    }

    public static bool SystemPrefersDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch (Exception) { return false; }
    }

    public void Dispose()
    {
        if (_listening)
        {
            SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
            SystemParameters.StaticPropertyChanged -= OnSystemParameterChanged;
        }
        _listening = false;
    }
}
