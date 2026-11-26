using System.IO.Compression;
using System.Text;

namespace BiliIndexTranslate.Core.Diagnostics;

/// <summary>Writes the diagnostics ZIP and reads the tail of the log files that go into it.</summary>
public static class DiagnosticBundle
{
    public const string OmittedMarker = "[earlier lines omitted]\n";

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>Writes every entry as a UTF-8 (no BOM) text file in the ZIP; the text goes through the scrubber first.</summary>
    public static void Write(Stream output, IEnumerable<(string Name, string Text)> entries, PrivacyScrubber scrubber)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(scrubber);
        using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
        foreach (var (name, text) in entries)
        {
            var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
            using var stream = entry.Open();
            var bytes = Utf8NoBom.GetBytes(scrubber.Scrub(text));
            stream.Write(bytes, 0, bytes.Length);
        }
    }

    /// <summary>
    /// The last <paramref name="maxBytes"/> bytes of a text file, or null when the file does not exist. The file may be
    /// open for appending in the app, so it is opened with sharing. A truncated result starts at a line boundary and
    /// begins with <see cref="OmittedMarker"/>.
    /// </summary>
    public static string? ReadTail(string path, int maxBytes)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxBytes, 1);
        if (!File.Exists(path)) return null;
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var length = fs.Length;
        var truncated = length > maxBytes;
        var start = truncated ? length - maxBytes : 0;
        fs.Seek(start, SeekOrigin.Begin);
        var buffer = new byte[(int)(length - start)];
        var read = 0;
        while (read < buffer.Length)
        {
            var n = fs.Read(buffer, read, buffer.Length - read);
            if (n == 0) break;
            read += n;
        }
        var text = Utf8NoBom.GetString(buffer, 0, read);
        if (!truncated) return text;
        // Drop the first, probably partial, line (also covers a cut inside a multi-byte character).
        var newline = text.IndexOf('\n', StringComparison.Ordinal);
        text = newline < 0 ? string.Empty : text[(newline + 1)..];
        return OmittedMarker + text;
    }
}
