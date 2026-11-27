using BiliIndexTranslate.Core.Speech;
using Xunit;

namespace BiliIndexTranslate.Core.Tests;

public class VoiceMatcherTests
{
    private static VoiceCandidate V(string id, string lang) => new(id, lang, id);

    [Fact]
    public void Exact_language_match_wins_over_primary_subtag()
    {
        var voices = new[] { V("a", "es-MX"), V("b", "es-ES") };
        Assert.Equal(1, VoiceMatcher.Pick(voices, "es-ES", null));
    }

    [Fact]
    public void Default_voice_is_preferred_among_exact_matches()
    {
        var voices = new[] { V("a", "en-US"), V("b", "en-US"), V("c", "en-GB") };
        Assert.Equal(1, VoiceMatcher.Pick(voices, "en-US", "b"));
        Assert.Equal(0, VoiceMatcher.Pick(voices, "en-US", "c"));
        Assert.Equal(0, VoiceMatcher.Pick(voices, "en-US", null));
    }

    [Fact]
    public void Primary_subtag_fallback_picks_another_region()
    {
        Assert.Equal(1, VoiceMatcher.Pick(new[] { V("a", "en-US"), V("b", "es-MX") }, "es-ES", null));
        Assert.Equal(1, VoiceMatcher.Pick(new[] { V("a", "ja-JP"), V("b", "zh-TW") }, "zh-CN", null));
    }

    [Fact]
    public void Primary_subtag_fallback_prefers_default_then_first()
    {
        var voices = new[] { V("a", "es-MX"), V("b", "es-US") };
        Assert.Equal(1, VoiceMatcher.Pick(voices, "es-ES", "b"));
        Assert.Equal(0, VoiceMatcher.Pick(voices, "es-ES", null));
        Assert.Equal(0, VoiceMatcher.Pick(voices, "es-ES", "zzz"));
    }

    [Fact]
    public void Matching_ignores_case()
    {
        Assert.Equal(0, VoiceMatcher.Pick(new[] { V("a", "JA-jp") }, "ja-JP", null));
        Assert.Equal(0, VoiceMatcher.Pick(new[] { V("a", "ES-mx") }, "es-es", null));
    }

    [Fact]
    public void No_match_returns_minus_one()
    {
        Assert.Equal(-1, VoiceMatcher.Pick(new[] { V("a", "en-US"), V("b", "ja-JP") }, "th-TH", null));
        Assert.Equal(-1, VoiceMatcher.Pick(new[] { V("a", "en-US") }, "", null));
        Assert.Equal(-1, VoiceMatcher.Pick(new[] { V("a", "en-US") }, null, null));
    }

    [Fact]
    public void Empty_list_returns_minus_one()
    {
        Assert.Equal(-1, VoiceMatcher.Pick(Array.Empty<VoiceCandidate>(), "en-US", "x"));
    }

    [Fact]
    public void Lcid_list_maps_to_tags()
    {
        Assert.Equal(new[] { "ja-JP" }, VoiceMatcher.LcidListToTags("411"));
        Assert.Equal("en-US", VoiceMatcher.LcidListToTags("409;9")[0]);
    }

    [Fact]
    public void Lcid_list_ignores_garbage()
    {
        Assert.Equal(new[] { "ja-JP" }, VoiceMatcher.LcidListToTags("zz;;411;-5;0"));
        Assert.Empty(VoiceMatcher.LcidListToTags(null));
        Assert.Empty(VoiceMatcher.LcidListToTags("  "));
        Assert.Empty(VoiceMatcher.LcidListToTags("xyz"));
    }
}
