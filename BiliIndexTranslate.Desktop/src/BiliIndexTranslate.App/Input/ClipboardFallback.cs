using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using BiliIndexTranslate.App.Native;

namespace BiliIndexTranslate.App.Input;

/// <summary>
/// Explicit-only clipboard capture (fallback shortcut or tray command). Sends one Ctrl+C to the
/// foreground application, waits briefly for the clipboard sequence number to change, and reads the
/// text. Never runs automatically and never polls the clipboard. If the clipboard held text before the
/// Ctrl+C, that text is written back afterwards (excluded from clipboard history and cloud clipboard);
/// non-text content is left as is, and nothing is restored when Ctrl+C changed nothing.
/// </summary>
internal static class ClipboardFallback
{
    public static async Task<string?> CaptureSelectionAsync(int timeoutMs = 350)
    {
        var previous = ReadText();
        var before = NativeMethods.GetClipboardSequenceNumber();
        var sent = SendCtrlC();
        if (sent == 0) return null;

        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            await Task.Delay(25).ConfigureAwait(true);
            if (NativeMethods.GetClipboardSequenceNumber() != before) break;
        }
        if (NativeMethods.GetClipboardSequenceNumber() == before) return null;
        var text = ReadText();
        if (previous is not null) TryRestoreText(previous);
        return text;
    }

    /// <summary>
    /// Writes text back to the clipboard without it entering Windows clipboard history or the cloud
    /// clipboard (registered formats with a DWORD 0); false when the clipboard stays busy.
    /// </summary>
    public static bool TryRestoreText(string text)
    {
        for (var attempt = 0; attempt < 4; attempt++)
        {
            try
            {
                var data = new DataObject();
                data.SetText(text);
                data.SetData("CanIncludeInClipboardHistory", new MemoryStream(new byte[4]));
                data.SetData("CanUploadToCloudClipboard", new MemoryStream(new byte[4]));
                Clipboard.SetDataObject(data, true);
                return true;
            }
            catch (COMException)
            {
                Thread.Sleep(40); // clipboard busy
            }
        }
        return false;
    }

    public static string? ReadText()
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                return Clipboard.ContainsText() ? Clipboard.GetText() : null;
            }
            catch (COMException)
            {
                Thread.Sleep(30); // clipboard busy
            }
        }
        return null;
    }

    /// <summary>Puts text on the clipboard; false when the clipboard stays busy.</summary>
    public static bool TryWriteText(string text)
    {
        for (var attempt = 0; attempt < 4; attempt++)
        {
            try
            {
                Clipboard.SetDataObject(text, true);
                return true;
            }
            catch (COMException)
            {
                Thread.Sleep(40); // clipboard busy
            }
        }
        return false;
    }

    private static uint SendCtrlC()
    {
        // Release Alt/Shift/Win if the hotkey still holds them so the target sees a clean Ctrl+C.
        var inputs = new List<INPUT>();
        foreach (var mod in new ushort[] { NativeMethods.VK_MENU, NativeMethods.VK_SHIFT, NativeMethods.VK_LWIN, NativeMethods.VK_RWIN })
            if (NativeMethods.IsKeyDown(mod)) inputs.Add(Key(mod, up: true));
        inputs.Add(Key(NativeMethods.VK_CONTROL, up: false));
        inputs.Add(Key(NativeMethods.VK_C, up: false));
        inputs.Add(Key(NativeMethods.VK_C, up: true));
        inputs.Add(Key(NativeMethods.VK_CONTROL, up: true));
        var arr = inputs.ToArray();
        return NativeMethods.SendInput((uint)arr.Length, arr, Marshal.SizeOf<INPUT>());
    }

    private static INPUT Key(ushort vk, bool up) => new()
    {
        type = NativeMethods.INPUT_KEYBOARD,
        u = new INPUTUNION { ki = new KEYBDINPUT { wVk = vk, dwFlags = up ? NativeMethods.KEYEVENTF_KEYUP : 0, dwExtraInfo = OwnInput.Marker } },
    };
}
