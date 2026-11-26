using BiliIndexTranslate.Core.Geometry;
using BiliIndexTranslate.Core.Localization;
using BiliIndexTranslate.Core.Selection;
using BiliIndexTranslate.Core.Translation;
using Xunit;

namespace BiliIndexTranslate.Core.Tests;

public class CardCaptionTests
{
    private static SelectionSnapshot Snap(SelectionOrigin origin = SelectionOrigin.UiAutomation, bool atPointer = false)
    {
        var r = new PxRect(0, 0, 10, 10);
        return new SelectionSnapshot("text", new AnchorRect(r, r, 1, 10), 1, 1, "app", EditableKind.NotEditable, origin, 0) { AnchoredAtPointer = atPointer };
    }

    [Fact]
    public void Plain_final_card_has_no_caption()
        => Assert.Null(CardCaption.Build("en", Snap(), RequestRoute.Direct, fromCache: false, useRelay: false));

    [Fact]
    public void Streaming_adds_the_streaming_part()
        => Assert.Equal("streaming…", CardCaption.Build("en", Snap(), null, false, false, streaming: true));

    [Fact]
    public void Cached_adds_the_cached_part()
        => Assert.Equal("cached", CardCaption.Build("en", Snap(), RequestRoute.Direct, fromCache: true, useRelay: false));

    [Fact]
    public void Relay_route_adds_the_relay_part()
        => Assert.Equal("relay", CardCaption.Build("en", Snap(), RequestRoute.Relay, false, useRelay: false));

    [Fact]
    public void Without_a_route_the_relay_setting_decides()
    {
        Assert.Equal("relay", CardCaption.Build("en", Snap(), null, false, useRelay: true));
        Assert.Null(CardCaption.Build("en", Snap(), null, false, useRelay: false));
    }

    [Fact]
    public void A_direct_route_ignores_the_relay_setting()
        => Assert.Null(CardCaption.Build("en", Snap(), RequestRoute.Direct, false, useRelay: true));

    [Fact]
    public void Clipboard_origin_adds_the_clipboard_part()
        => Assert.Equal("clipboard", CardCaption.Build("en", Snap(SelectionOrigin.Clipboard, atPointer: false), RequestRoute.Direct, false, false));

    [Fact]
    public void Anchored_at_the_pointer_adds_the_near_pointer_part()
        => Assert.Equal("near pointer", CardCaption.Build("en", Snap(atPointer: true), RequestRoute.Direct, false, false));

    [Fact]
    public void Clipboard_origin_wins_over_near_pointer()
        => Assert.Equal("clipboard", CardCaption.Build("en", Snap(SelectionOrigin.Clipboard, atPointer: true), RequestRoute.Direct, false, false));

    [Fact]
    public void Not_anchored_at_the_pointer_has_no_near_pointer_part()
        => Assert.Null(CardCaption.Build("en", Snap(atPointer: false), RequestRoute.Direct, false, false));

    [Fact]
    public void Parts_come_in_a_fixed_order_joined_with_a_middle_dot()
        => Assert.Equal("streaming… · cached · relay · near pointer", CardCaption.Build("en", Snap(atPointer: true), null, true, true, streaming: true));

    [Fact]
    public void The_interface_language_picks_the_words()
    {
        Assert.Equal("生成中… · 缓存 · 中继 · 指针附近", CardCaption.Build("zh-CN", Snap(atPointer: true), null, true, true, streaming: true));
        Assert.Equal("剪贴板", CardCaption.Build("zh-CN", Snap(SelectionOrigin.Clipboard), RequestRoute.Direct, false, false));
    }
}
