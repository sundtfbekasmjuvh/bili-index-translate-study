using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Text.Json;
using BiliIndexTranslate.Core.Diagnostics;
using BiliIndexTranslate.Core.Settings;
using Xunit;

namespace BiliIndexTranslate.Core.Tests;

public class PrivacyScrubberTests
{
    private static PrivacyScrubber Make(string? profile = @"C:\Users\alice", string? user = "alice", string? machine = "DESKTOP-ABC123")
        => new(profile, user, machine);

    [Fact]
    public void Profile_path_is_replaced_with_either_separator_and_any_case()
    {
        var s = Make();
        Assert.Equal(@"%USERPROFILE%\AppData\x", s.Scrub(@"C:\Users\alice\AppData\x"));
        Assert.Equal("%USERPROFILE%/AppData/x", s.Scrub("C:/Users/alice/AppData/x"));
        Assert.Equal(@"%USERPROFILE%\Desktop", s.Scrub(@"c:\users\ALICE\Desktop"));
    }

    [Fact]
    public void Users_folder_of_the_user_name_is_replaced_keeping_the_separator()
    {
        var s = Make(profile: null);
        Assert.Equal(@"D:\Users\%USERNAME%\files", s.Scrub(@"D:\Users\alice\files"));
        Assert.Equal("/mnt/Users/%USERNAME%/files", s.Scrub("/mnt/Users/ALICE/files"));
    }

    [Fact]
    public void User_name_is_replaced_as_a_whole_word_only()
    {
        var s = Make(profile: null);
        Assert.Equal("hello %USERNAME%, bye %USERNAME%.", s.Scrub("hello alice, bye Alice."));
        Assert.Equal("alices malice alice_1 alice2", s.Scrub("alices malice alice_1 alice2"));
    }

    [Fact]
    public void A_two_character_user_name_is_not_replaced_as_a_word()
    {
        var s = new PrivacyScrubber(null, "ab", null);
        Assert.Equal("ab is a word", s.Scrub("ab is a word"));
        Assert.Equal(@"C:\Users\%USERNAME%\x", s.Scrub(@"C:\Users\ab\x"));
    }

    [Fact]
    public void Machine_name_is_replaced_as_a_whole_word()
    {
        var s = Make(profile: null, user: null);
        Assert.Equal("host %COMPUTERNAME% ok", s.Scrub("host desktop-abc123 ok"));
        Assert.Equal("DESKTOP-ABC1234", s.Scrub("DESKTOP-ABC1234"));
    }

    [Fact]
    public void Missing_inputs_skip_their_rules()
    {
        var s = new PrivacyScrubber(null, "", null);
        Assert.Equal(@"D:\data\alice alice", s.Scrub(@"D:\data\alice alice"));
        Assert.Equal("", s.Scrub(""));
    }

    [Fact]
    public void Another_users_folder_is_hidden_without_mangling_a_longer_name()
    {
        // The profile rule must not turn C:\Users\alicia into %USERPROFILE%ia; the Users-folder rule then hides it whole.
        var s = Make();
        Assert.Equal(@"C:\Users\%USERNAME%\x", s.Scrub(@"C:\Users\alicia\x"));
        Assert.Equal(@"C:\Users\%USERNAME%\x", s.Scrub(@"C:\Users\alice2\x"));
    }

    [Fact]
    public void Short_8_3_profile_name_is_replaced()
    {
        // %TEMP% on a profile with a long user name, as on the GitHub runner (runneradmin → RUNNER~1).
        var s = new PrivacyScrubber(@"C:\Users\runneradmin", "runneradmin", null);
        Assert.Equal(@"dir C:\Users\%USERNAME%\AppData\Local\Temp\bit-1 ok", s.Scrub(@"dir C:\Users\RUNNER~1\AppData\Local\Temp\bit-1 ok"));
        Assert.Equal("C:/Users/%USERNAME%/AppData", s.Scrub("C:/Users/RUNNER~1/AppData"));
    }

    [Fact]
    public void Users_folder_with_spaces_is_replaced_only_up_to_a_separator()
    {
        var s = new PrivacyScrubber(null, null, null);
        Assert.Equal(@"C:\Users\%USERNAME%\AppData", s.Scrub(@"C:\Users\John Smith\AppData"));
        // No separator after it: only the first word is taken as the folder; the rest of the line stays readable.
        Assert.Equal("open C:\\Users\\%USERNAME% failed\nnext", s.Scrub("open C:\\Users\\bob failed\nnext"));
        Assert.Equal(@"at C:\Users\%USERNAME%", s.Scrub(@"at C:\Users\bob"));
    }

    [Fact]
    public void Public_and_default_users_folders_and_placeholders_stay()
    {
        var s = Make();
        Assert.Equal(@"C:\Users\Public\Desktop", s.Scrub(@"C:\Users\Public\Desktop"));
        Assert.Equal(@"C:\Users\Default\NTUSER", s.Scrub(@"C:\Users\Default\NTUSER"));
        Assert.Equal(@"C:\Users\%USERNAME%\x", s.Scrub(@"C:\Users\%USERNAME%\x"));
        Assert.Equal(@"%USERPROFILE%\x", s.Scrub(@"c:\users\ALICE\x"));
    }
}

public class SettingsRedactorTests
{
    private static JsonElement Redact(AppSettings s) => JsonElement.Parse(SettingsRedactor.ToJson(s));

    [Fact]
    public void Every_settable_property_is_exported_somehow_or_excluded_on_purpose()
    {
        var known = SettingsRedactor.VerbatimProperties.Concat(SettingsRedactor.TransformedProperties).Concat(SettingsRedactor.ExcludedProperties).ToHashSet();
        var settable = typeof(AppSettings).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.SetMethod is { IsPublic: true })
            .Select(p => p.Name)
            .ToList();
        var unknown = settable.Where(n => !known.Contains(n)).ToList();
        Assert.True(unknown.Count == 0, "decide how the diagnostics bundle exports: " + string.Join(", ", unknown));
        // and the lists do not name properties that no longer exist
        var stale = known.Where(n => !settable.Contains(n)).ToList();
        Assert.True(stale.Count == 0, "no such settings property any more: " + string.Join(", ", stale));
    }

    [Fact]
    public void Verbatim_fields_are_copied_with_camel_case_names_and_enum_names()
    {
        var s = new AppSettings { Theme = ThemePreference.Dark, VisionMode = VisionMode.Local, Hotkey = "Alt+W", StabilizationDelayMs = 300 };
        s.ExcludedProcesses.Add("notepad");
        var j = Redact(s);
        Assert.Equal("Dark", j.GetProperty("theme").GetString());
        Assert.Equal("Local", j.GetProperty("visionMode").GetString());
        Assert.Equal("Alt+W", j.GetProperty("hotkey").GetString());
        Assert.Equal(300, j.GetProperty("stabilizationDelayMs").GetInt32());
        Assert.Equal("notepad", j.GetProperty("excludedProcesses")[0].GetString());
        Assert.Equal(AppSettings.CurrentSchemaVersion, j.GetProperty("schemaVersion").GetInt32());
    }

    [Fact]
    public void Urls_are_reduced_to_their_origin()
    {
        var j = Redact(new AppSettings
        {
            BaseUrl = "https://user:pw@diag-host.example:8443/v1/SENTINEL1-path?token=SENTINEL2#frag",
            RelayPrefix = "http://relay.example/https://upstream.example/v1",
        });
        Assert.Equal("https://diag-host.example:8443", j.GetProperty("baseUrl").GetString());
        Assert.Equal("http://relay.example", j.GetProperty("relayPrefix").GetString());
    }

    [Fact]
    public void A_value_that_is_not_a_url_is_replaced_by_its_length_and_empty_stays_empty()
    {
        var j = Redact(new AppSettings { BaseUrl = "ftp://x.example/a", RelayPrefix = "not a url" });
        Assert.Equal("(not a URL, 17 chars)", j.GetProperty("baseUrl").GetString());
        Assert.Equal("(not a URL, 9 chars)", j.GetProperty("relayPrefix").GetString());
        Assert.Equal("", SettingsRedactor.OriginOnly(""));
    }

    [Fact]
    public void Header_values_are_masked_and_lines_without_a_colon_are_hidden()
    {
        var j = Redact(new AppSettings { CustomHeaders = "X-Test-Secret: SENTINEL3\n\n  Authorization :Bearer abc\nrawtoken" });
        Assert.Equal("X-Test-Secret: ***\nAuthorization: ***\n***", j.GetProperty("customHeaders").GetString());
    }

    [Fact]
    public void Body_and_glossary_become_lengths()
    {
        var j = Redact(new AppSettings { CustomBody = "{\"q\": SENTINEL}", GlossaryText = "SENTINEL4 => x\n\nfoo => bar\n" });
        Assert.Equal(15, j.GetProperty("customBodyLength").GetInt32());
        Assert.Equal(27, j.GetProperty("glossaryLength").GetInt32());
        Assert.Equal(2, j.GetProperty("glossaryLines").GetInt32());
        Assert.False(j.TryGetProperty("customBody", out _));
        Assert.False(j.TryGetProperty("glossaryText", out _));
    }

    [Fact]
    public void Custom_name_is_kept_only_for_a_built_in_preset()
    {
        Assert.Equal("DeepL", Redact(new AppSettings { CustomName = "DeepL" }).GetProperty("customName").GetString());
        Assert.Equal("(11 chars)", Redact(new AppSettings { CustomName = "My own tool" }).GetProperty("customName").GetString());
        Assert.Equal("", Redact(new AppSettings { CustomName = "" }).GetProperty("customName").GetString());
    }

    [Fact]
    public void Sentinels_never_appear_and_the_api_key_has_no_trace()
    {
        var s = new AppSettings
        {
            BaseUrl = "https://diag-host.example/v1/SENTINEL1-path?token=SENTINEL2",
            CustomHeaders = "X-Test-Secret: SENTINEL3",
            GlossaryText = "SENTINEL4 => x",
            CustomBody = "SENTINEL6",
            ApiKey = "SENTINEL5",
        };
        var json = SettingsRedactor.ToJson(s);
        Assert.DoesNotContain("SENTINEL", json, StringComparison.Ordinal);
        Assert.DoesNotContain("apikey", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("https://diag-host.example", json, StringComparison.Ordinal);
    }
}

public class DiagnosticBundleTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bit-bundle-" + Guid.NewGuid().ToString("N")[..8]);

    public DiagnosticBundleTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        Directory.Delete(_dir, recursive: true);
        GC.SuppressFinalize(this);
    }

    private string P(string name) => Path.Combine(_dir, name);

    [Fact]
    public void ReadTail_of_a_missing_file_is_null()
        => Assert.Null(DiagnosticBundle.ReadTail(P("none.log"), 100));

    [Fact]
    public void ReadTail_returns_a_small_file_whole()
    {
        File.WriteAllText(P("a.log"), "one\ntwo\n");
        Assert.Equal("one\ntwo\n", DiagnosticBundle.ReadTail(P("a.log"), 1000));
    }

    [Fact]
    public void ReadTail_of_a_big_file_keeps_the_end_without_a_partial_first_line()
    {
        var lines = Enumerable.Range(0, 200).Select(i => $"line-{i:D4}").ToArray();
        File.WriteAllText(P("b.log"), string.Join("\n", lines) + "\n");
        var tail = DiagnosticBundle.ReadTail(P("b.log"), 100)!;
        Assert.StartsWith(DiagnosticBundle.OmittedMarker, tail, StringComparison.Ordinal);
        var body = tail[DiagnosticBundle.OmittedMarker.Length..];
        Assert.EndsWith("line-0199\n", body, StringComparison.Ordinal);
        Assert.True(body.Length <= 100);
        foreach (var l in body.Split('\n', StringSplitOptions.RemoveEmptyEntries)) Assert.Matches(@"^line-\d{4}$", l);
    }

    [Fact]
    public void ReadTail_works_while_another_stream_holds_the_file_open_for_writing()
    {
        using var writer = new FileStream(P("c.log"), FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
        var bytes = Encoding.UTF8.GetBytes("first\nsecond\n");
        writer.Write(bytes, 0, bytes.Length);
        writer.Flush();
        Assert.Equal("first\nsecond\n", DiagnosticBundle.ReadTail(P("c.log"), 1000));
    }

    [Fact]
    public void Write_makes_a_zip_with_the_named_entries_utf8_without_bom_and_scrubbed()
    {
        var scrubber = new PrivacyScrubber(@"C:\Users\alice", "alice", "BOX-1234");
        using var ms = new MemoryStream();
        DiagnosticBundle.Write(ms, [("report.txt", @"see C:\Users\alice\x on BOX-1234 — ok"), ("system.txt", "plain")], scrubber);

        using var zip = new ZipArchive(new MemoryStream(ms.ToArray()), ZipArchiveMode.Read);
        Assert.Equal(["report.txt", "system.txt"], zip.Entries.Select(e => e.FullName).ToArray());
        using var s = zip.GetEntry("report.txt")!.Open();
        using var buf = new MemoryStream();
        s.CopyTo(buf);
        var raw = buf.ToArray();
        Assert.False(raw.Length >= 3 && raw[0] == 0xEF && raw[1] == 0xBB && raw[2] == 0xBF, "BOM found");
        Assert.Equal(@"see %USERPROFILE%\x on %COMPUTERNAME% — ok", Encoding.UTF8.GetString(raw));
    }
}
