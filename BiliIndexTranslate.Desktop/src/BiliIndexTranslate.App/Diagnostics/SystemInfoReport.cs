using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using BiliIndexTranslate.App.Themes;
using BiliIndexTranslate.Core.Settings;
using Microsoft.Win32;

namespace BiliIndexTranslate.App.Diagnostics;

/// <summary>
/// The system.txt of a diagnostics bundle: versions, display setup and a few settings. Never the machine name,
/// the user name, the domain or environment variables (the data directory is scrubbed later with the rest).
/// </summary>
internal static class SystemInfoReport
{
    public static string Build(string appVersion, bool installed, AppSettings settings, string dataDir, IReadOnlyList<string> monitors)
    {
        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.AppendLine(inv, $"App version: {appVersion}");
        sb.AppendLine(inv, $"Build: {(installed ? "installed (Velopack)" : "portable or development")}");
        sb.AppendLine(inv, $"Windows: {WindowsVersionText()}");
        sb.AppendLine(inv, $"Environment.OSVersion: {Environment.OSVersion}");
        sb.AppendLine(inv, $".NET: {RuntimeInformation.FrameworkDescription}");
        sb.AppendLine(inv, $"Architecture: OS {RuntimeInformation.OSArchitecture}, process {RuntimeInformation.ProcessArchitecture}");
        sb.AppendLine(inv, $"UI culture: {CultureInfo.CurrentUICulture.Name}");
        sb.AppendLine(inv, $"UiLanguage setting: {settings.UiLanguage}");
        sb.AppendLine(inv, $"High contrast: {(Safe(ThemeManager.SystemHighContrast) ? "on" : "off")}");
        sb.AppendLine(inv, $"System theme: {(Safe(ThemeManager.SystemPrefersDark) ? "dark" : "light")}");
        sb.AppendLine(inv, $"Monitors: {(monitors.Count == 0 ? "(unknown)" : string.Join(", ", monitors))}");
        sb.AppendLine(inv, $"Process uptime: {Uptime()}");
        sb.AppendLine(inv, $"Data directory: {dataDir}");
        sb.AppendLine(inv, $"File logging: {(settings.DiagnosticsFileLogging ? "on" : "off")}");
        return sb.ToString();
    }

    private static string Uptime()
    {
        try
        {
            using var p = Process.GetCurrentProcess();
            var up = DateTime.Now - p.StartTime;
            return $"{(int)up.TotalHours}h {up.Minutes}m {up.Seconds}s";
        }
        catch (Exception) { return "(unknown)"; }
    }

    private static string WindowsVersionText()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            if (key is null) return "(unknown)";
            var product = key.GetValue("ProductName") as string ?? "?";
            var display = key.GetValue("DisplayVersion") as string ?? key.GetValue("ReleaseId") as string ?? "?";
            var build = key.GetValue("CurrentBuild") as string ?? "?";
            var ubr = key.GetValue("UBR") is int u ? u.ToString(CultureInfo.InvariantCulture) : "?";
            return $"{product} {display} (build {build}.{ubr})";
        }
        catch (Exception) { return "(unknown)"; }
    }

    private static bool Safe(Func<bool> f)
    {
        try { return f(); }
        catch (Exception) { return false; }
    }
}
