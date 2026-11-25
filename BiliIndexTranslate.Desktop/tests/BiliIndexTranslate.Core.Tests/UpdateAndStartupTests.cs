using System.Net;
using System.Text.RegularExpressions;
using BiliIndexTranslate.Core.Settings;
using BiliIndexTranslate.Core.Startup;
using BiliIndexTranslate.Core.Updates;
using Xunit;

namespace BiliIndexTranslate.Core.Tests;

public class UpdateScheduleTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 25, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public void NotInstalled_OrSwitchedOff_NeverChecks(bool installed, bool enabled)
    {
        Assert.Equal(UpdateCheckPlan.None, UpdateSchedule.Next(installed, enabled, null, Now));
        Assert.Equal(UpdateCheckPlan.None, UpdateSchedule.Next(installed, enabled, Now.AddDays(-5), Now));
    }

    [Fact]
    public void FirstCheck_WaitsAboutAMinuteAfterStart()
    {
        var plan = UpdateSchedule.Next(true, true, null, Now);
        Assert.Equal(UpdateCheckAction.After, plan.Action);
        Assert.Equal(TimeSpan.FromSeconds(60), plan.Delay);
    }

    [Fact]
    public void AfterACheck_TheNextOneIsDueAfter24Hours()
    {
        var plan = UpdateSchedule.Next(true, true, Now.AddHours(-5), Now);
        Assert.Equal(UpdateCheckAction.After, plan.Action);
        Assert.Equal(TimeSpan.FromHours(19), plan.Delay);

        var justNow = UpdateSchedule.Next(true, true, Now, Now);
        Assert.Equal(TimeSpan.FromHours(24), justNow.Delay);
    }

    [Theory]
    [InlineData(24)]
    [InlineData(25)]
    [InlineData(24 * 30)]
    public void WhenTheIntervalHasPassed_ACheckIsDueNow(int hoursAgo)
        => Assert.Equal(UpdateCheckPlan.Now, UpdateSchedule.Next(true, true, Now.AddHours(-hoursAgo), Now));

    [Fact]
    public void ALastCheckInTheFuture_DoesNotPostponeBeyondOneInterval()
    {
        var plan = UpdateSchedule.Next(true, true, Now.AddDays(10), Now);
        Assert.Equal(UpdateCheckAction.After, plan.Action);
        Assert.Equal(TimeSpan.FromHours(24), plan.Delay);
    }
}

public class AutoStartValueTests
{
    private const string Exe = @"C:\Users\Ann Lee\AppData\Local\BiliIndexTranslate.Desktop\current\BiliIndexTranslate.exe";

    [Fact]
    public void Build_QuotesThePath_AndDoesNotQuoteTwice()
    {
        Assert.Equal("\"" + Exe + "\"", AutoStartValue.Build(Exe));
        Assert.Equal("\"" + Exe + "\"", AutoStartValue.Build("\"" + Exe + "\""));
        Assert.Equal("\"" + Exe + "\"", AutoStartValue.Build("  " + Exe + "  "));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("\"\"")]
    public void Build_RefusesAnEmptyPath(string? path)
        => Assert.Throws<ArgumentException>(() => AutoStartValue.Build(path!));

    [Fact]
    public void PointsTo_IgnoresCaseAndQuotes()
    {
        Assert.True(AutoStartValue.PointsTo(AutoStartValue.Build(Exe), Exe));
        Assert.True(AutoStartValue.PointsTo(Exe, Exe));
        Assert.True(AutoStartValue.PointsTo("\"" + Exe.ToUpperInvariant() + "\"", Exe.ToLowerInvariant()));
        Assert.True(AutoStartValue.PointsTo("  \"" + Exe + "\"  ", "\"" + Exe + "\""));
        Assert.True(AutoStartValue.PointsTo("\"" + Exe.Replace('\\', '/') + "\"", Exe));
    }

    [Fact]
    public void PointsTo_AcceptsArgumentsAfterAQuotedPath()
        => Assert.True(AutoStartValue.PointsTo("\"" + Exe + "\" --welcome", Exe));

    [Theory]
    [InlineData(@"C:\Other\BiliIndexTranslate.exe")]
    [InlineData(@"C:\Users\Ann Lee\AppData\Local\BiliIndexTranslate.Desktop\current\BiliIndexTranslate.exe.bak")]
    [InlineData(@"C:\Users\Ann")]
    [InlineData("")]
    [InlineData(null)]
    public void PointsTo_IsFalseForAnotherPath_OrNothing(string? registered)
        => Assert.False(AutoStartValue.PointsTo(registered, Exe));

    [Fact]
    public void PointsTo_IsFalseWhenTheCurrentExeIsUnknown()
    {
        Assert.False(AutoStartValue.PointsTo(AutoStartValue.Build(Exe), null));
        Assert.False(AutoStartValue.PointsTo(AutoStartValue.Build(Exe), ""));
    }

    [Fact]
    public void ValueName_AndKey_AreTheOnesTheInstallerHookDeletes()
    {
        Assert.Equal("BiliIndexTranslate", AutoStartValue.ValueName);
        Assert.Equal(@"Software\Microsoft\Windows\CurrentVersion\Run", AutoStartValue.RunKeyPath);
    }
}

public class UpdateErrorTextTests
{
    [Fact]
    public void Describe_NamesTheHttpStatus_WithoutTheUrl()
    {
        var ex = new HttpRequestException("Response status code does not indicate success: 403 (rate limit) for https://api.github.com/repos/x/y/releases?access_token=SECRET", null, HttpStatusCode.Forbidden);
        Assert.Equal("HTTP 403", UpdateErrorText.Describe(ex));
    }

    [Fact]
    public void Describe_KeepsNetworkAndTimeoutErrorsShort()
    {
        Assert.Equal("network error", UpdateErrorText.Describe(new HttpRequestException("No such host is known: api.github.com")));
        Assert.Equal("timed out", UpdateErrorText.Describe(new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout")));
        Assert.Equal("timed out", UpdateErrorText.Describe(new TimeoutException()));
        Assert.Equal("network error", UpdateErrorText.Describe(new AggregateException(new HttpRequestException("x"))));
    }

    [Fact]
    public void Describe_RemovesUrlsAndTokens_FromOtherErrors()
    {
        var ex = new InvalidOperationException("Failed to fetch https://github.com/a/b/releases/download/v1/x.nupkg?token=abc123&sig=zzz after 3 tries");
        var text = UpdateErrorText.Describe(ex);
        Assert.Equal("InvalidOperationException: Failed to fetch <url> after 3 tries", text);
        Assert.DoesNotContain("abc123", text);
        Assert.DoesNotContain("github.com", text);
    }

    [Fact]
    public void Describe_UsesTheTypeName_WhenThereIsNoMessage_AndShortensLongText()
    {
        Assert.Equal("IOException", UpdateErrorText.Describe(new IOException("")));
        var long_ = UpdateErrorText.Scrub(new string('x', 500));
        Assert.True(long_.Length <= 121, long_.Length.ToString());
        Assert.EndsWith("…", long_);
        Assert.Equal("a b c", UpdateErrorText.Scrub(" a\r\n b\tc "));
        Assert.Equal(string.Empty, UpdateErrorText.Scrub(null));
    }
}

public class AutoCheckForUpdatesSettingTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bit-tests-" + Guid.NewGuid().ToString("N"));
    private string File_ => Path.Combine(_dir, "settings.json");

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    [Fact]
    public void DefaultsToOn()
    {
        Assert.True(new AppSettings().AutoCheckForUpdates);
        Assert.True(new AppSettings().Normalize().AutoCheckForUpdates);
        Assert.True(new SettingsStore(File_, new ReversibleProtector()).Load().Settings.AutoCheckForUpdates);
    }

    [Fact]
    public void FileWrittenBeforeTheOption_LoadsAsOn_WithoutBeingTreatedAsMigrated()
    {
        var store = new SettingsStore(File_, new ReversibleProtector());
        store.Save(new AppSettings());
        var withoutOption = Regex.Replace(File.ReadAllText(File_), "\\s*\"autoCheckForUpdates\": (true|false),", string.Empty);
        Assert.DoesNotContain("autoCheckForUpdates", withoutOption);
        File.WriteAllText(File_, withoutOption);

        var loaded = store.Load();
        Assert.False(loaded.WasMalformed);
        Assert.False(loaded.WasMigrated);
        Assert.True(loaded.Settings.AutoCheckForUpdates);
    }

    [Fact]
    public void TurnedOff_RoundTrips_AndSurvivesNormalizeAndClone()
    {
        var store = new SettingsStore(File_, new ReversibleProtector());
        store.Save(new AppSettings { AutoCheckForUpdates = false });
        Assert.Contains("\"autoCheckForUpdates\": false", File.ReadAllText(File_));

        var loaded = store.Load().Settings;
        Assert.False(loaded.AutoCheckForUpdates);
        Assert.False(loaded.Clone().Normalize().AutoCheckForUpdates);
    }

    [Fact]
    public void AVersion1File_GetsTheDefault_WhenMigrated()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(File_, """{ "schemaVersion": 1, "apiKey": "old" }""");
        var r = new SettingsStore(File_, new ReversibleProtector()).Load();
        Assert.True(r.WasMigrated);
        Assert.True(r.Settings.AutoCheckForUpdates);
    }
}
