using System.Globalization;
using System.Text;
using FlaUI.Core.AutomationElements;
using FlaUI.UIA3;

namespace BiliIndexTranslate.App.UiTests;

/// <summary>Failure evidence read through UI Automation. Never used by the cold Chromium test (it must not query Edge).</summary>
public static class UiaDiagnostics
{
    /// <summary>
    /// What UI Automation reports from the test process, walking up from the focused element the way the app's
    /// probe does (raw view, up to 6 parents, stop at the first element with a text pattern).
    /// </summary>
    public static string DescribeFocus(UIA3Automation automation)
    {
        static string Safe(Func<string> read)
        {
            try { return read(); }
            catch (Exception ex) { return "?" + ex.GetType().Name; }
        }

        var sb = new StringBuilder();
        try
        {
            var walker = automation.TreeWalkerFactory.GetRawViewWalker();
            var e = automation.FocusedElement();
            for (var hop = 0; hop <= 6 && e is not null; hop++)
            {
                var current = e;
                var name = Safe(() => current.Properties.Name.ValueOrDefault ?? string.Empty);
                if (name.Length > 40) name = name[..40] + "...";
                var hasText = Safe(() => current.Patterns.Text.IsSupported.ToString());
                sb.Append(CultureInfo.InvariantCulture, $"  hop {hop}: {Safe(() => current.Properties.ControlType.ValueOrDefault.ToString())}")
                  .Append(CultureInfo.InvariantCulture, $" class='{Safe(() => current.Properties.ClassName.ValueOrDefault ?? string.Empty)}'")
                  .Append(CultureInfo.InvariantCulture, $" framework='{Safe(() => current.Properties.FrameworkId.ValueOrDefault ?? string.Empty)}'")
                  .Append(CultureInfo.InvariantCulture, $" name='{name}' textPattern={hasText}");
                if (hasText == bool.TrueString)
                {
                    sb.Append(" selection=").Append(Safe(() =>
                    {
                        var ranges = current.Patterns.Text.Pattern.GetSelection();
                        if (ranges.Length == 0) return "(no ranges)";
                        var text = ranges[0].GetText(200) ?? string.Empty;
                        return $"{ranges.Length} range(s), '{text.Replace("\r", "\\r").Replace("\n", "\\n")}'";
                    })).AppendLine();
                    break;
                }
                sb.AppendLine();
                e = walker.GetParent(current);
            }
        }
        catch (Exception ex)
        {
            sb.Append("  focus query failed: ").Append(ex.GetType().Name).Append(' ').Append(ex.Message).AppendLine();
        }
        return sb.ToString();
    }

    /// <summary>The first levels of the UIA tree of the target window (or the desktop's top-level windows when none was found).</summary>
    public static string DumpTree(UIA3Automation automation, Window? window)
    {
        var sb = new StringBuilder();
        var count = 0;
        void Walk(AutomationElement e, int depth)
        {
            if (count++ > 600 || depth > 7) return;
            string line;
            try
            {
                var p = e.Properties;
                var name = p.Name.ValueOrDefault ?? string.Empty;
                if (name.Length > 80) name = name[..80] + "...";
                line = $"{new string(' ', depth * 2)}{e.ControlType} class='{p.ClassName.ValueOrDefault}' id='{p.AutomationId.ValueOrDefault}' name='{name}' rect={e.BoundingRectangle}";
            }
            catch (Exception ex) { line = $"{new string(' ', depth * 2)}(unreadable: {ex.Message})"; }
            sb.AppendLine(line);
            try { foreach (var child in e.FindAllChildren()) Walk(child, depth + 1); }
            catch (Exception) { }
        }

        try
        {
            if (window is not null) Walk(window, 0);
            else
            {
                sb.AppendLine("target window was not found; top-level windows:");
                foreach (var w in automation.GetDesktop().FindAllChildren()) Walk(w, 0 /* each with its own limit */ + 7);
            }
        }
        catch (Exception ex) { sb.AppendLine("dump failed: " + ex.Message); }
        return sb.ToString();
    }
}
