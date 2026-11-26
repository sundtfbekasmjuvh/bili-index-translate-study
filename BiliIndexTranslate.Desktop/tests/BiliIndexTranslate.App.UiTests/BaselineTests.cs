using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using Xunit;
using Xunit.Abstractions;

namespace BiliIndexTranslate.App.UiTests;

/// <summary>
/// Measurement harness: records baseline numbers (selection-to-card latency, cold start, resource use over repeated
/// selections) and writes them to TestResults/baseline/*.json. Latency and cold start have no thresholds; the resource
/// test has gross-leak limits. Numbers from shared CI runners are noisy: read them as orders of magnitude.
/// </summary>
public class BaselineTests
{
    private static readonly string Full = MockUpstream.FullText;
    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(10);

    private readonly ITestOutputHelper _out;

    public BaselineTests(ITestOutputHelper output) => _out = output;

    // ---- 6: latency ----------------------------------------------------------------------------

    [Fact]
    public void Latency_from_selection_to_card()
    {
        using var ui = new UiSession(Name());
        ui.Guard(() =>
        {
            // Cache miss: the first selection goes to the mock.
            ui.SelectAll();
            var sw = Stopwatch.StartNew(); // t0: Ctrl+A has just been sent
            double? firstCard = null, firstPartial = null, final = null;
            while (sw.Elapsed < UiSession.CardTimeout)
            {
                var text = ui.CardText();
                var now = sw.Elapsed.TotalMilliseconds;
                if (firstCard is null && (text is not null || LoadingVisible(ui) || ui.ErrorText() is not null)) firstCard = now;
                if (firstPartial is null && !string.IsNullOrEmpty(text) && text != Full) firstPartial = now;
                if (text == Full) { final = now; break; }
                Thread.Sleep(Poll);
            }
            Assert.True(final is not null, "card text never became the full text on the cache miss");
            firstCard ??= final; // the poller can miss a card that showed up and filled in between two polls

            // Cache hits: same text again, served from the app's translation cache.
            var hits = new List<double>();
            for (var i = 0; i < 10; i++)
            {
                CloseCard(ui);
                ui.ClearSelection();
                ui.SelectAll();
                var t = Stopwatch.StartNew();
                var ok = false;
                while (t.Elapsed < UiSession.CardTimeout)
                {
                    if (ui.CardText() == Full) { ok = true; break; }
                    Thread.Sleep(Poll);
                }
                Assert.True(ok, $"cache-hit cycle {i + 1}: card text never became the full text");
                hits.Add(t.Elapsed.TotalMilliseconds);
            }

            Assert.True(ui.Mock.ChatRequests.Count >= 1, "the mock got no chat request");

            var min = hits.Min();
            var max = hits.Max();
            var median = Median(hits);
            _out.WriteLine("Latency (ms) from Ctrl+A sent to card state, polled every ~10 ms plus UIA lookup time");
            _out.WriteLine($"  miss: first card visible  {F(firstCard)}");
            _out.WriteLine($"  miss: first partial text  {F(firstPartial)}" + (firstPartial is null ? " (none seen)" : ""));
            _out.WriteLine($"  miss: final text          {F(final)}");
            _out.WriteLine($"  hit (n={hits.Count}): min {F(min)}  median {F(median)}  max {F(max)}");
            _out.WriteLine("  Note: Ctrl+A triggers through the keyboard hook (key-up, 280 ms) and the UIA selection event (320 ms), whichever comes last,");
            _out.WriteLine("  and the mock's segment delay (3 segments, 400 ms apart) dominates the final time on a miss.");
            _out.WriteLine("  App log (probe, card window, first text, translation):");
            foreach (var line in ui.DiagnosticsLog().Split('\n').Select(l => l.TrimEnd('\r'))
                         .Where(l => l.Contains(" probe ", StringComparison.Ordinal) || l.Contains(" render ", StringComparison.Ordinal) || l.Contains(" translate ", StringComparison.Ordinal)))
                _out.WriteLine("    " + line);

            WriteJson("latency", new
            {
                test = Name(),
                missFirstCardVisibleMs = firstCard,
                missFirstPartialTextMs = firstPartial,
                missFinalTextMs = final,
                hitCount = hits.Count,
                hitMinMs = min,
                hitMedianMs = median,
                hitMaxMs = max,
                hitAllMs = hits,
                mockSegmentDelayMs = ui.Mock.SegmentDelay.TotalMilliseconds,
                chatRequests = ui.Mock.ChatRequests.Count,
                trigger = "UIA text-selection-changed event",
                note = "mock segment delay dominates the final time on a miss",
            });
        });
    }

    // ---- 7: cold start -------------------------------------------------------------------------

    [Fact]
    public void Cold_start_time()
    {
        var runs = new List<object>();
        var startups = new List<double>();
        for (var i = 0; i < 3; i++)
        {
            double? appLogMs = null;
            double startupMs;
            using (var ui = new UiSession(Name() + "-" + (i + 1)))
            {
                startupMs = ui.StartupTime.TotalMilliseconds;
                appLogMs = AppLogSpanMs(ui.DiagnosticsLog());
            }
            Assert.True(startupMs > 0, $"run {i + 1}: no startup time recorded");
            startups.Add(startupMs);
            runs.Add(new { run = i + 1, startupMs, appLogFirstLineToReadyMs = appLogMs });
            _out.WriteLine($"run {i + 1}: Process.Start -> 'ready' {F(startupMs)} ms (poll step 200 ms); app log first line -> ready " +
                           (appLogMs is { } v ? F(v) + " ms" : "n/a"));
        }
        _out.WriteLine($"startup min {F(startups.Min())}  median {F(Median(startups))}  max {F(startups.Max())} ms");
        _out.WriteLine("Note: the process-start figure is polled every 200 ms, so it reads 0..200 ms high; the log-based figure is exact to 1 ms but starts after the runtime has loaded.");

        WriteJson("cold-start", new
        {
            test = Name(),
            runs,
            startupMinMs = startups.Min(),
            startupMedianMs = Median(startups),
            startupMaxMs = startups.Max(),
            pollStepMs = 200,
        });
    }

    // ---- 8: resources --------------------------------------------------------------------------

    [Fact]
    public void Resource_use_over_repeated_selections()
    {
        const int cycles = 100;
        using var ui = new UiSession(Name());
        ui.Guard(() =>
        {
            // Warm-up: first selection pays for lazy initialisation and the cache miss.
            ui.SelectAll();
            ui.WaitForCardText(Full);

            var samples = new List<ResourceSample> { Sample(ui.AppProcess, 0) };
            for (var i = 1; i <= cycles; i++)
            {
                CloseCard(ui);
                ui.ClearSelection();
                ui.SelectAll();
                ui.WaitForCardText(Full);
                if (i % 10 == 0) samples.Add(Sample(ui.AppProcess, i));
            }

            Assert.True(ui.Mock.ChatRequests.Count >= 1, "the mock got no chat request");

            _out.WriteLine("cycle | handles | GDI | USER | private bytes | working set");
            foreach (var s in samples)
                _out.WriteLine($"{s.Cycle,5} | {s.Handles,7} | {s.Gdi,3} | {s.User,4} | {s.PrivateBytes,13:N0} | {s.WorkingSet,11:N0}");
            var first = samples[0];
            var last = samples[^1];
            _out.WriteLine($"delta first->last: handles {last.Handles - first.Handles:+#;-#;0}, GDI {(long)last.Gdi - first.Gdi:+#;-#;0}, " +
                           $"USER {(long)last.User - first.User:+#;-#;0}, private {last.PrivateBytes - first.PrivateBytes:+#,0;-#,0;0} B, " +
                           $"working set {last.WorkingSet - first.WorkingSet:+#,0;-#,0;0} B");

            WriteJson("resources", new
            {
                test = Name(),
                cycles,
                samples,
                delta = new
                {
                    handles = last.Handles - first.Handles,
                    gdiObjects = (long)last.Gdi - first.Gdi,
                    userObjects = (long)last.User - first.User,
                    privateBytes = last.PrivateBytes - first.PrivateBytes,
                    workingSetBytes = last.WorkingSet - first.WorkingSet,
                },
                note = "cycle 0 is taken after one warm-up selection; each cycle: close card, ClearSelection, SelectAll, wait for the full text (cache hit)",
            });

            // Limits, first sample vs last sample. They catch a real leak; they are not a tuning target.
            // Private bytes is only a gross-leak check (+80 MB): GC timing made one CI run read +45.7 MB that did not reproduce.
            Assert.True(last.Handles - first.Handles <= 50, $"handles grew by {last.Handles - first.Handles} (limit +50)");
            Assert.True((long)last.Gdi - first.Gdi <= 5, $"GDI objects grew by {(long)last.Gdi - first.Gdi} (limit +5)");
            Assert.True((long)last.User - first.User <= 5, $"USER objects grew by {(long)last.User - first.User} (limit +5)");
            Assert.True(last.PrivateBytes - first.PrivateBytes <= 80L * 1024 * 1024,
                $"private bytes grew by {(last.PrivateBytes - first.PrivateBytes) / 1048576.0:F1} MB (limit +80 MB)");
        });
    }

    // ---- helpers -------------------------------------------------------------------------------

    private sealed record ResourceSample(int Cycle, int Handles, uint Gdi, uint User, long PrivateBytes, long WorkingSet);

    private static ResourceSample Sample(Process p, int cycle)
    {
        p.Refresh();
        return new ResourceSample(cycle, p.HandleCount, GetGuiResources(p.Handle, 0), GetGuiResources(p.Handle, 1),
            p.PrivateMemorySize64, p.WorkingSet64);
    }

    // uiFlags: 0 = GR_GDIOBJECTS, 1 = GR_USEROBJECTS
    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetGuiResources(IntPtr hProcess, uint uiFlags);

    /// <summary>Escape and wait until the card is gone, so the next cycle starts from a hidden card (outside the timed part).</summary>
    private static void CloseCard(UiSession ui)
    {
        ui.PressEscape();
        Assert.True(ui.WaitForCardGone(), "card still visible after Esc");
    }

    // The app's own windows are found with Win32 and read through Automation.FromHandle (see UiSession.FindInApp).
    private static bool LoadingVisible(UiSession ui) => ui.FindInApp("LoadingText") is not null;

    /// <summary>Milliseconds between the first log line and the line containing "ready" (line format "HH:mm:ss.fff ..."), or null.</summary>
    private static double? AppLogSpanMs(string log)
    {
        TimeSpan? first = null, ready = null;
        foreach (var line in log.Split('\n'))
        {
            if (line.Length < 12 ||
                !TimeSpan.TryParseExact(line[..12], @"hh\:mm\:ss\.fff", CultureInfo.InvariantCulture, out var t)) continue;
            first ??= t;
            if (line.Contains("ready", StringComparison.Ordinal)) { ready = t; break; }
        }
        if (first is null || ready is null) return null;
        var span = ready.Value - first.Value;
        if (span < TimeSpan.Zero) span += TimeSpan.FromDays(1); // crossed midnight
        return span.TotalMilliseconds;
    }

    private static double Median(List<double> values)
    {
        var s = values.OrderBy(v => v).ToList();
        return s.Count % 2 == 1 ? s[s.Count / 2] : (s[s.Count / 2 - 1] + s[s.Count / 2]) / 2;
    }

    private static string F(double? v) => v is { } d ? d.ToString("F0", CultureInfo.InvariantCulture) : "n/a";

    private static void WriteJson(string file, object metrics)
    {
        var root = ExeLocator.DesktopRoot() ?? AppContext.BaseDirectory;
        var dir = Path.Combine(root, "TestResults", "baseline");
        Directory.CreateDirectory(dir);
        var doc = new
        {
            metrics,
            environment = new { os = Environment.OSVersion.ToString(), processorCount = Environment.ProcessorCount },
        };
        File.WriteAllText(Path.Combine(dir, file + ".json"),
            JsonSerializer.Serialize(doc, IndentedJson));
    }

    private static readonly JsonSerializerOptions IndentedJson = new() { WriteIndented = true };

    private static string Name([System.Runtime.CompilerServices.CallerMemberName] string? name = null) => name!;
}
