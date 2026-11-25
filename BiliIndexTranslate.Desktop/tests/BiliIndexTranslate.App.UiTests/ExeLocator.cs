namespace BiliIndexTranslate.App.UiTests;

/// <summary>Finds the executables under test: an environment variable wins, otherwise the build output next to the sources.</summary>
public static class ExeLocator
{
    public static string App() => Find("BIT_APP_EXE", Path.Combine("src", "BiliIndexTranslate.App"), "BiliIndexTranslate.exe");

    public static string Target() => Find("BIT_TARGET_EXE", Path.Combine("tests", "BiliIndexTranslate.UiTestTarget"), "BiliIndexTranslate.UiTestTarget.exe");

    /// <summary>The BiliIndexTranslate.Desktop directory (the one that holds BiliIndexTranslate.sln), or null.</summary>
    public static string? DesktopRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "BiliIndexTranslate.sln"))) return dir.FullName;
        return null;
    }

    private static string Find(string envVar, string projectDir, string exeName)
    {
        var fromEnv = Environment.GetEnvironmentVariable(envVar);
        if (!string.IsNullOrWhiteSpace(fromEnv))
        {
            if (!File.Exists(fromEnv)) throw new FileNotFoundException($"{envVar} points to a file that does not exist: {fromEnv}");
            return fromEnv;
        }

        var root = DesktopRoot() ?? throw new FileNotFoundException($"BiliIndexTranslate.sln not found above {AppContext.BaseDirectory}; set {envVar}.");
        // The target framework may later gain a Windows SDK version (net10.0-windows10.0.19041.0), hence the wildcard.
        foreach (var config in new[] { "Release", "Debug" })
        {
            var bin = Path.Combine(root, projectDir, "bin", config);
            if (!Directory.Exists(bin)) continue;
            var hit = Directory.GetDirectories(bin, "net10.0-windows*")
                .OrderByDescending(d => d, StringComparer.Ordinal)
                .Select(d => Path.Combine(d, exeName))
                .FirstOrDefault(File.Exists);
            if (hit is not null) return hit;
        }
        throw new FileNotFoundException($"{exeName} not found under {Path.Combine(root, projectDir, "bin")}; build the solution first or set {envVar}.");
    }
}
