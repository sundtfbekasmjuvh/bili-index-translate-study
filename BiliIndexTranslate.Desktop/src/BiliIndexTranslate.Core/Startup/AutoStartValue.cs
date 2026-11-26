namespace BiliIndexTranslate.Core.Startup;

/// <summary>
/// The value of the per-user start-up entry (<c>HKCU\Software\Microsoft\Windows\CurrentVersion\Run</c>).
/// Pure string rules; the registry itself is handled by the app.
/// </summary>
public static class AutoStartValue
{
    public const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string ValueName = "BiliIndexTranslate";

    /// <summary>The exe path in double quotes, as the Run key expects it (a path with spaces would otherwise be split).</summary>
    public static string Build(string exePath)
    {
        var path = Unquote(exePath);
        if (path.Length == 0) throw new ArgumentException("The exe path is empty.", nameof(exePath));
        return "\"" + path + "\"";
    }

    /// <summary>
    /// True when the registered value starts the given exe: case is ignored, the quotes are removed, and
    /// arguments after a quoted path are ignored. Null or empty on either side is false.
    /// </summary>
    public static bool PointsTo(string? registeredValue, string? exePath)
    {
        if (string.IsNullOrWhiteSpace(registeredValue) || string.IsNullOrWhiteSpace(exePath)) return false;
        var registered = PathOf(registeredValue);
        var current = Normalize(Unquote(exePath));
        return registered.Length > 0 && string.Equals(registered, current, StringComparison.OrdinalIgnoreCase);
    }

    private static string PathOf(string value)
    {
        var v = value.Trim();
        if (v.StartsWith('"'))
        {
            var end = v.IndexOf('"', 1);
            return Normalize(end < 0 ? v[1..] : v[1..end]);
        }
        return Normalize(v);
    }

    private static string Unquote(string? path) => (path ?? string.Empty).Trim().Trim('"').Trim();

    private static string Normalize(string path) => path.Trim().Replace('/', '\\');
}
