using System.Net.Http;
using System.Text.RegularExpressions;

namespace BiliIndexTranslate.Core.Updates;

public enum UpdateCheckStatus
{
    /// <summary>Portable build or development run: updates are not supported.</summary>
    NotInstalled,
    UpToDate,

    /// <summary>A newer version exists and is being downloaded in the background.</summary>
    Downloading,

    /// <summary>A newer version has been downloaded and is installed when the app exits.</summary>
    Downloaded,
    Failed,

    /// <summary>Another check or download is already running.</summary>
    Busy,
    Cancelled,
}

public sealed record UpdateCheckOutcome(UpdateCheckStatus Status, string? Version = null, string? Reason = null);

/// <summary>Turns update errors into short text that is safe to log and show: no URLs, no tokens.</summary>
public static partial class UpdateErrorText
{
    private const int MaxLength = 120;

    public static string Describe(Exception ex)
    {
        if (ex is AggregateException { InnerException: { } inner }) return Describe(inner);
        switch (ex)
        {
            case HttpRequestException { StatusCode: { } status }:
                return $"HTTP {(int)status}";
            case HttpRequestException:
                return "network error";
            case TimeoutException or TaskCanceledException:
                return "timed out";
            case OperationCanceledException:
                return "cancelled";
        }
        var message = Scrub(ex.Message);
        return message.Length == 0 ? ex.GetType().Name : $"{ex.GetType().Name}: {message}";
    }

    /// <summary>Replaces every URL by &lt;url&gt;, collapses white space and shortens the text.</summary>
    public static string Scrub(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        var clean = Whitespace().Replace(Url().Replace(text, "<url>"), " ").Trim();
        return clean.Length <= MaxLength ? clean : clean[..MaxLength].TrimEnd() + "…";
    }

    [GeneratedRegex(@"[A-Za-z][A-Za-z0-9+.\-]*://\S+")]
    private static partial Regex Url();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
