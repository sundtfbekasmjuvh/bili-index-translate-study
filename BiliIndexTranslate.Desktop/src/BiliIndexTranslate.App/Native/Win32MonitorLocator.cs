using BiliIndexTranslate.Core.Diagnostics;
using BiliIndexTranslate.Core.Geometry;

namespace BiliIndexTranslate.App.Native;

/// <summary>Monitor lookup through Win32, with effective per-monitor DPI.</summary>
internal sealed class Win32MonitorLocator : IMonitorLocator
{
    private static int _dpiFallbackLogged;

    /// <summary>Diagnostics sink for the DPI fallback (set once by the host; optional).</summary>
    public DiagnosticsLog? Log { get; set; }

    public MonitorArea FromRect(PxRect anchor)
    {
        var rc = new RECT { Left = anchor.Left, Top = anchor.Top, Right = anchor.Right, Bottom = anchor.Bottom };
        var h = NativeMethods.MonitorFromRect(ref rc, NativeMethods.MONITOR_DEFAULTTONEAREST);
        return FromHandle(h, Log);
    }

    public MonitorArea FromPoint(PxPoint point)
    {
        var h = NativeMethods.MonitorFromPoint(new POINT { X = point.X, Y = point.Y }, NativeMethods.MONITOR_DEFAULTTONEAREST);
        return FromHandle(h, Log);
    }

    public MonitorArea FromWindow(IntPtr hwnd)
        => FromHandle(NativeMethods.MonitorFromWindow(hwnd, NativeMethods.MONITOR_DEFAULTTONEAREST), Log);

    public static MonitorArea FromHandle(IntPtr hMonitor, DiagnosticsLog? log = null)
    {
        var info = new MONITORINFOEX { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFOEX>() };
        if (hMonitor == IntPtr.Zero || !NativeMethods.GetMonitorInfoW(hMonitor, ref info))
            return MonitorArea.Default();

        double scale = 1.0;
        try
        {
            if (NativeMethods.GetDpiForMonitor(hMonitor, NativeMethods.MDT_EFFECTIVE_DPI, out var dpiX, out _) == 0 && dpiX > 0)
                scale = dpiX / 96.0;
        }
        catch (Exception ex)
        {
            // shcore missing: assume 100 %; logged once per process (the first call that has a log).
            if (log is not null && Interlocked.Exchange(ref _dpiFallbackLogged, 1) == 0)
                log.Warn("dpi", $"GetDpiForMonitor failed ({ex.GetType().Name}, HResult 0x{ex.HResult:X8}); assuming 100 % scale");
        }

        return new MonitorArea(
            new PxRect(info.rcMonitor.Left, info.rcMonitor.Top, info.rcMonitor.Right, info.rcMonitor.Bottom),
            new PxRect(info.rcWork.Left, info.rcWork.Top, info.rcWork.Right, info.rcWork.Bottom),
            scale,
            (info.dwFlags & NativeMethods.MONITORINFOF_PRIMARY) != 0,
            info.szDevice ?? "monitor");
    }
}
