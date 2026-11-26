using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using FlaUI.Core.Tools;
using Xunit;

namespace BiliIndexTranslate.App.UiTests;

/// <summary>
/// High contrast. WARNING: this test switches Windows high contrast on for the WHOLE DESKTOP of the machine running it
/// (every window, including the test host and the screen of anyone using that machine) and switches it back in a finally
/// block. It is in its own class so that nothing else runs in that state (xUnit runs the tests of the assembly one at a
/// time, see AssemblyInfo). The change is not persisted (no SPIF_UPDATEINIFILE), so a killed test run is undone by a
/// sign-out or restart. If the SPI call fails, the test fails with the Win32 error; it never skips silently.
/// </summary>
public class HighContrastTests
{
    private static readonly string Full = MockUpstream.FullText;
    private static readonly TimeSpan ThemeTimeout = TimeSpan.FromSeconds(10);

    [Fact]
    public void App_follows_windows_high_contrast_and_still_shows_the_card()
    {
        using var ui = new UiSession(Name());
        ui.Guard(() =>
        {
            var before = HighContrastNative.Get();
            try
            {
                HighContrastNative.Set(on: true, scheme: "High Contrast Black");
                WaitForLog(ui, "theme: high contrast");

                // The card still works with the high-contrast palette.
                ui.SelectAll();
                ui.WaitForCardText(Full);
                ui.PressEscape();
                Assert.True(ui.WaitForCardGone(), "card still visible after Esc");
            }
            finally
            {
                HighContrastNative.Restore(before);
            }

            // After restoring, the app goes back to a normal theme: the log gets a light or dark line after the high-contrast one.
            var restored = Retry.WhileFalse(() =>
            {
                var log = ui.DiagnosticsLog();
                var hc = log.LastIndexOf("theme: high contrast", StringComparison.Ordinal);
                return hc >= 0 && (log.IndexOf("theme: light", hc, StringComparison.Ordinal) >= 0 || log.IndexOf("theme: dark", hc, StringComparison.Ordinal) >= 0);
            }, ThemeTimeout, TimeSpan.FromMilliseconds(200));
            Assert.True(restored.Success, "no 'theme: light' / 'theme: dark' line after high contrast was switched off; log:\n" + ui.DiagnosticsLog());
        });
    }

    private static void WaitForLog(UiSession ui, string line)
    {
        var found = Retry.WhileFalse(() => ui.DiagnosticsLog().Contains(line, StringComparison.Ordinal), ThemeTimeout, TimeSpan.FromMilliseconds(200));
        Assert.True(found.Success, $"app did not log '{line}' within {ThemeTimeout.TotalSeconds:F0} s; log:\n{ui.DiagnosticsLog()}");
    }

    private static string Name([CallerMemberName] string name = "") => name;
}

/// <summary>SystemParametersInfo for SPI_GETHIGHCONTRAST / SPI_SETHIGHCONTRAST.</summary>
internal static class HighContrastNative
{
    private const uint SPI_GETHIGHCONTRAST = 0x0042;
    private const uint SPI_SETHIGHCONTRAST = 0x0043;
    private const uint HCF_HIGHCONTRASTON = 0x00000001;
    private const uint SPIF_SENDCHANGE = 0x0002;

    [StructLayout(LayoutKind.Sequential)]
    private struct HIGHCONTRAST
    {
        public uint cbSize;
        public uint dwFlags;
        public IntPtr lpszDefaultScheme;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SystemParametersInfo(uint uiAction, uint uiParam, ref HIGHCONTRAST pvParam, uint fWinIni);

    /// <summary>Flags and default scheme name as they were before the test.</summary>
    public readonly record struct State(uint Flags, string? Scheme);

    public static State Get()
    {
        var buffer = Marshal.AllocHGlobal(512 * sizeof(char));
        try
        {
            Marshal.WriteInt16(buffer, 0);
            var hc = new HIGHCONTRAST { cbSize = (uint)Marshal.SizeOf<HIGHCONTRAST>(), lpszDefaultScheme = buffer };
            if (!SystemParametersInfo(SPI_GETHIGHCONTRAST, hc.cbSize, ref hc, 0))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "SystemParametersInfo(SPI_GETHIGHCONTRAST) failed");
            var scheme = hc.lpszDefaultScheme == IntPtr.Zero ? null : Marshal.PtrToStringUni(hc.lpszDefaultScheme);
            return new State(hc.dwFlags, string.IsNullOrEmpty(scheme) ? null : scheme);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    public static void Set(bool on, string? scheme)
    {
        var name = scheme is null ? IntPtr.Zero : Marshal.StringToHGlobalUni(scheme);
        try
        {
            var hc = new HIGHCONTRAST
            {
                cbSize = (uint)Marshal.SizeOf<HIGHCONTRAST>(),
                dwFlags = on ? HCF_HIGHCONTRASTON : 0,
                lpszDefaultScheme = name,
            };
            if (!SystemParametersInfo(SPI_SETHIGHCONTRAST, hc.cbSize, ref hc, SPIF_SENDCHANGE))
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"SystemParametersInfo(SPI_SETHIGHCONTRAST, on={on}) failed");
        }
        finally { if (name != IntPtr.Zero) Marshal.FreeHGlobal(name); }
    }

    /// <summary>Puts the earlier state back (on with its scheme, or off).</summary>
    public static void Restore(State before)
        => Set((before.Flags & HCF_HIGHCONTRASTON) != 0, before.Scheme);
}
