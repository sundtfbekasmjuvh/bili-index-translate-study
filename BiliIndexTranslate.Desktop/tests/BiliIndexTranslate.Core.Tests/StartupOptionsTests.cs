using BiliIndexTranslate.Core.Startup;
using Xunit;

namespace BiliIndexTranslate.Core.Tests;

public class StartupOptionsTests
{
    private static string Abs(string name) => Path.Combine(Path.GetTempPath(), name);

    [Fact]
    public void No_args_means_defaults()
    {
        var o = StartupOptions.Parse([]);
        Assert.False(o.Welcome);
        Assert.False(o.Diagnostics);
        Assert.Null(o.DataDirectory);
    }

    [Fact]
    public void Diagnostics_flag_is_case_insensitive_and_independent_of_the_others()
    {
        var dir = Abs("bit-d");
        var o = StartupOptions.Parse(["--Diagnostics", "--data-dir", dir]);
        Assert.True(o.Diagnostics);
        Assert.False(o.Welcome);
        Assert.Equal(Path.GetFullPath(dir), o.DataDirectory);
        Assert.False(StartupOptions.Parse(["--welcome"]).Diagnostics);
    }

    [Fact]
    public void Vocabulary_flag_is_case_insensitive_and_independent_of_the_others()
    {
        var dir = Abs("bit-v");
        var o = StartupOptions.Parse(["--Vocabulary", "--data-dir", dir]);
        Assert.True(o.Vocabulary);
        Assert.False(o.Welcome);
        Assert.False(o.Diagnostics);
        Assert.Equal(Path.GetFullPath(dir), o.DataDirectory);
        Assert.False(StartupOptions.Parse(["--diagnostics", "--welcome"]).Vocabulary);
        Assert.False(StartupOptions.Parse([]).Vocabulary);
        var both = StartupOptions.Parse(["--diagnostics", "--vocabulary"]);
        Assert.True(both.Diagnostics);
        Assert.True(both.Vocabulary);
    }

    [Fact]
    public void Welcome_flag_is_case_insensitive()
        => Assert.True(StartupOptions.Parse(["--WELCOME"]).Welcome);

    [Fact]
    public void Data_dir_takes_the_next_argument()
    {
        var dir = Abs("bit-a");
        var o = StartupOptions.Parse(["--welcome", "--data-dir", dir]);
        Assert.True(o.Welcome);
        Assert.Equal(Path.GetFullPath(dir), o.DataDirectory);
    }

    [Fact]
    public void Data_dir_accepts_equals_form()
    {
        var dir = Abs("bit-b");
        Assert.Equal(Path.GetFullPath(dir), StartupOptions.Parse([$"--data-dir={dir}"]).DataDirectory);
    }

    [Fact]
    public void Data_dir_without_value_is_ignored()
    {
        Assert.Null(StartupOptions.Parse(["--data-dir"]).DataDirectory);
        Assert.Null(StartupOptions.Parse(["--data-dir", "  "]).DataDirectory);
        Assert.Null(StartupOptions.Parse(["--data-dir="]).DataDirectory);
    }

    [Fact]
    public void Relative_data_dir_becomes_absolute()
        => Assert.True(Path.IsPathRooted(StartupOptions.Parse(["--data-dir", "rel-dir"]).DataDirectory!));

    [Fact]
    public void Scoped_name_is_unchanged_without_a_data_dir()
    {
        const string name = @"Local\BiliIndexTranslate.SingleInstance";
        Assert.Equal(name, StartupOptions.ScopedName(name, null));
        Assert.Equal(name, StartupOptions.ScopedName(name, ""));
    }

    [Fact]
    public void Scoped_name_differs_per_directory_and_is_stable()
    {
        const string name = @"Local\BiliIndexTranslate.SingleInstance";
        var a = StartupOptions.ScopedName(name, Abs("bit-x"));
        var b = StartupOptions.ScopedName(name, Abs("bit-y"));
        Assert.NotEqual(name, a);
        Assert.NotEqual(a, b);
        Assert.Equal(a, StartupOptions.ScopedName(name, Abs("bit-x") + Path.DirectorySeparatorChar));
        Assert.StartsWith(name + ".", a);
    }
}
