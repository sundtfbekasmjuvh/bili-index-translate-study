using Microsoft.Win32;
using BiliIndexTranslate.Core.Startup;

namespace BiliIndexTranslate.App.Startup;

/// <summary>
/// Start with Windows, through the current user's Run key. The registry is the only place the state lives:
/// nothing about it is kept in settings.json. The value is the quoted path of the running exe; in an
/// installed build that is <c>...\current\BiliIndexTranslate.exe</c>, which stays the same across updates.
/// </summary>
internal static class AutoStart
{
    private static string? CurrentExe => Environment.ProcessPath;

    /// <summary>True when the Run entry exists and starts this exe. An entry that points elsewhere counts as not set.</summary>
    public static bool IsRegisteredForCurrentExe()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(AutoStartValue.RunKeyPath, writable: false);
            return AutoStartValue.PointsTo(key?.GetValue(AutoStartValue.ValueName) as string, CurrentExe);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Writes the entry, replacing one that points to another path. Throws when the registry cannot be written.</summary>
    public static void Register()
    {
        var exe = CurrentExe;
        if (string.IsNullOrWhiteSpace(exe)) throw new InvalidOperationException("The path of the running exe is unknown.");
        using var key = Registry.CurrentUser.CreateSubKey(AutoStartValue.RunKeyPath, writable: true)
                        ?? throw new InvalidOperationException("The Run key could not be opened.");
        key.SetValue(AutoStartValue.ValueName, AutoStartValue.Build(exe), RegistryValueKind.String);
    }

    /// <summary>Deletes the entry. Throws when the registry cannot be written.</summary>
    public static void Unregister()
    {
        using var key = Registry.CurrentUser.OpenSubKey(AutoStartValue.RunKeyPath, writable: true);
        key?.DeleteValue(AutoStartValue.ValueName, throwOnMissingValue: false);
    }

    /// <summary>
    /// Uninstall hook: removes the entry when it starts the exe that is being uninstalled. An entry that
    /// points to another copy (a portable zip, say) is left alone. Never throws.
    /// </summary>
    public static void RemoveOnUninstall()
    {
        try
        {
            if (IsRegisteredForCurrentExe()) Unregister();
        }
        catch (Exception) { /* the uninstaller must not fail because of a start-up entry */ }
    }
}
