using System.Security.Cryptography;
using System.Text;

namespace BiliIndexTranslate.Core.Startup;

/// <summary>
/// Command-line options of the app. <c>--data-dir &lt;path&gt;</c> moves settings, logs and crash.log to
/// another directory (used by the UI tests so they never touch the real settings) and gives that
/// instance its own single-instance names.
/// </summary>
public sealed record StartupOptions(bool Welcome, string? DataDirectory)
{
    public const string DataDirArg = "--data-dir";
    public const string WelcomeArg = "--welcome";

    public static StartupOptions Parse(IReadOnlyList<string> args)
    {
        var welcome = false;
        string? dataDir = null;
        for (var i = 0; i < args.Count; i++)
        {
            var a = args[i];
            if (string.Equals(a, WelcomeArg, StringComparison.OrdinalIgnoreCase))
            {
                welcome = true;
            }
            else if (string.Equals(a, DataDirArg, StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 < args.Count && !string.IsNullOrWhiteSpace(args[i + 1]))
                    dataDir = args[++i].Trim();
            }
            else if (a.StartsWith(DataDirArg + "=", StringComparison.OrdinalIgnoreCase))
            {
                var v = a[(DataDirArg.Length + 1)..].Trim();
                if (v.Length > 0) dataDir = v;
            }
        }
        return new StartupOptions(welcome, dataDir is null ? null : Path.GetFullPath(dataDir));
    }

    /// <summary>
    /// Appends a suffix derived from the data directory to a kernel object name. Without a custom
    /// directory the name is returned unchanged, so existing installs keep their single-instance behaviour.
    /// </summary>
    public static string ScopedName(string baseName, string? dataDirectory)
    {
        if (string.IsNullOrEmpty(dataDirectory)) return baseName;
        var normalized = Path.GetFullPath(dataDirectory).TrimEnd('\\', '/').ToUpperInvariant();
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return baseName + "." + Convert.ToHexString(hash, 0, 6);
    }
}
