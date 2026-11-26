using System.Drawing;
using System.Runtime.InteropServices;

namespace BiliIndexTranslate.App.UiTests;

/// <summary>
/// Plain Win32 window lookup. Used where the test process must not ask UI Automation anything: finding the app's own
/// card windows, and (cold Chromium test) finding Edge's window and its page area.
/// </summary>
public static class Win32
{
    private delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc proc, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr parent, EnumProc proc, IntPtr lParam);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hwnd, char[] text, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, char[] text, int max);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);

    /// <summary>Top-level windows owned by a process (optionally only the visible ones).</summary>
    public static List<IntPtr> TopLevelWindows(int pid, bool visibleOnly)
    {
        var result = new List<IntPtr>();
        EnumWindows((h, _) =>
        {
            var threadId = GetWindowThreadProcessId(h, out var owner); // the lambda parameter is named _, so the result is kept in a local; thread id unused
            if (owner == (uint)pid && (!visibleOnly || IsWindowVisible(h))) result.Add(h);
            return true;
        }, IntPtr.Zero);
        return result;
    }

    public static string Title(IntPtr hwnd)
    {
        var buffer = new char[512];
        var length = GetWindowText(hwnd, buffer, buffer.Length);
        return length > 0 ? new string(buffer, 0, Math.Min(length, buffer.Length)) : string.Empty;
    }

    public static string ClassName(IntPtr hwnd)
    {
        var buffer = new char[256];
        var length = GetClassName(hwnd, buffer, buffer.Length);
        return length > 0 ? new string(buffer, 0, Math.Min(length, buffer.Length)) : string.Empty;
    }

    /// <summary>First visible top-level window (any process) whose title contains the text, or zero.</summary>
    public static IntPtr FindTopLevelByTitle(string contains)
    {
        var found = IntPtr.Zero;
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h) || !Title(h).Contains(contains, StringComparison.Ordinal)) return true;
            found = h;
            return false;
        }, IntPtr.Zero);
        return found;
    }

    public static IntPtr WaitForTopLevelByTitle(string contains, TimeSpan timeout)
    {
        var end = DateTime.UtcNow + timeout;
        do
        {
            var h = FindTopLevelByTitle(contains);
            if (h != IntPtr.Zero) return h;
            Thread.Sleep(300);
        } while (DateTime.UtcNow < end);
        throw new TimeoutException($"no visible window with a title containing '{contains}' within {timeout.TotalSeconds:F0} s");
    }

    /// <summary>First descendant window of the given class, or zero.</summary>
    public static IntPtr FindChildByClass(IntPtr parent, string className)
    {
        var found = IntPtr.Zero;
        EnumChildWindows(parent, (h, _) =>
        {
            if (ClassName(h) != className) return true;
            found = h;
            return false;
        }, IntPtr.Zero);
        return found;
    }

    /// <summary>Waits for a descendant window of the given class that already has a non-empty rectangle.</summary>
    public static IntPtr WaitForChildByClass(IntPtr parent, string className, TimeSpan timeout)
    {
        var end = DateTime.UtcNow + timeout;
        do
        {
            var h = FindChildByClass(parent, className);
            if (h != IntPtr.Zero && Bounds(h) is { Width: > 0, Height: > 0 }) return h;
            Thread.Sleep(200);
        } while (DateTime.UtcNow < end);
        throw new TimeoutException($"no child window of class '{className}' within {timeout.TotalSeconds:F0} s");
    }

    /// <summary>Window rectangle in screen coordinates.</summary>
    public static Rectangle Bounds(IntPtr hwnd)
    {
        if (!GetWindowRect(hwnd, out var r)) throw new InvalidOperationException("GetWindowRect failed");
        return Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
    }

    /// <summary>DPI scale of the window (1.0 = 96 dpi).</summary>
    public static double Scale(IntPtr hwnd)
    {
        var dpi = GetDpiForWindow(hwnd);
        return dpi == 0 ? 1.0 : dpi / 96.0;
    }

    public static void SetForeground(IntPtr hwnd) => SetForegroundWindow(hwnd);

    /// <summary>
    /// Kills (with child processes) every process that owns a visible top-level window whose title STARTS WITH one of the
    /// prefixes. Only those pids, never programs by name: the tests also run on people's own machines.
    /// </summary>
    public static int KillWindowOwners(IEnumerable<string> titlePrefixes)
    {
        var prefixes = titlePrefixes.ToArray();
        var pids = new HashSet<uint>();
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h)) return true;
            var title = Title(h);
            if (!prefixes.Any(p => title.StartsWith(p, StringComparison.Ordinal))) return true;
            var threadId = GetWindowThreadProcessId(h, out var pid); // the lambda parameter is named _, so the result is kept in a local; thread id unused
            if (pid != (uint)Environment.ProcessId) pids.Add(pid);
            return true;
        }, IntPtr.Zero);
        foreach (var pid in pids)
        {
            try
            {
                using var p = System.Diagnostics.Process.GetProcessById((int)pid);
                p.Kill(entireProcessTree: true);
                p.WaitForExit(3000);
            }
            catch (Exception) { /* already gone */ }
        }
        return pids.Count;
    }
}
