using System.Text.RegularExpressions;

namespace BiliIndexTranslate.Core.Diagnostics;

/// <summary>
/// Removes the Windows user's identity from diagnostic text: the profile path, the user name and the machine name.
/// A rule whose input is null or empty is skipped. The user name and the machine name are only replaced as whole
/// words and only when they have at least 3 characters, so a short name does not damage unrelated text.
/// </summary>
public sealed class PrivacyScrubber
{
    private const int MinWholeWordLength = 3;
    private const string WordChar = @"[\p{L}\p{N}_]";
    private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    /// <summary>
    /// Any other folder right under a drive's Users folder: the 8.3 short name of the profile (C:\Users\JOHNSM~1, which
    /// %TEMP% uses for long or spaced user names), a spaced name, or another account. Public and Default stay.
    /// A name with spaces counts only when a separator follows it, so the rest of a log line is not swallowed.
    /// </summary>
    private static readonly Regex OtherUsersFolder = new(
        @"(?<prefix>\b[A-Za-z]:[\\/]Users[\\/])(?!(?:%USERNAME%|Public|Default)(?:[\\/]|\s|$))(?:[^\\/\r\n""'<>|:*?]+(?=[\\/])|[^\\/\s""'<>|:*?]+)",
        Options);

    private readonly Regex? _profile;
    private readonly Regex? _usersFolder;
    private readonly Regex? _userWord;
    private readonly Regex? _machineWord;

    public PrivacyScrubber(string? userProfilePath, string? userName, string? machineName)
    {
        var profile = userProfilePath?.Trim().TrimEnd('\\', '/');
        if (!string.IsNullOrEmpty(profile))
        {
            var pattern = string.Join(@"[\\/]", profile.Split('\\', '/').Select(Regex.Escape));
            _profile = new Regex(pattern + "(?!" + WordChar + ")", Options);
        }

        var user = userName?.Trim();
        if (!string.IsNullOrEmpty(user))
        {
            _usersFolder = new Regex(@"Users(?<sep>[\\/])" + Regex.Escape(user) + "(?!" + WordChar + ")", Options);
            if (user.Length >= MinWholeWordLength) _userWord = WholeWord(user);
        }

        var machine = machineName?.Trim();
        if (!string.IsNullOrEmpty(machine) && machine.Length >= MinWholeWordLength) _machineWord = WholeWord(machine);
    }

    public string Scrub(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        if (_profile is not null) text = _profile.Replace(text, "%USERPROFILE%");
        if (_usersFolder is not null) text = _usersFolder.Replace(text, m => "Users" + m.Groups["sep"].Value + "%USERNAME%");
        if (_userWord is not null) text = _userWord.Replace(text, "%USERNAME%");
        if (_machineWord is not null) text = _machineWord.Replace(text, "%COMPUTERNAME%");
        text = OtherUsersFolder.Replace(text, m => m.Groups["prefix"].Value + "%USERNAME%");
        return text;
    }

    private static Regex WholeWord(string word)
        => new("(?<!" + WordChar + ")" + Regex.Escape(word) + "(?!" + WordChar + ")", Options);
}
