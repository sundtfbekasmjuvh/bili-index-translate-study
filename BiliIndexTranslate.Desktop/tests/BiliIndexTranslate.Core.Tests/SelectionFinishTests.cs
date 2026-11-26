using BiliIndexTranslate.Core.Geometry;
using BiliIndexTranslate.Core.Selection;
using BiliIndexTranslate.Core.Settings;
using Xunit;

namespace BiliIndexTranslate.Core.Tests;

public class SelectionFinishTests
{
    private static readonly PxPoint Pointer = new(300, 200);

    private static SelectionSnapshot Snap(string text, bool geometry = true, string process = "app")
    {
        var r = new PxRect(10, 10, 110, 30);
        return new SelectionSnapshot(text, geometry ? new AnchorRect(r, r, 1, 20) : null, 100, 7, process, EditableKind.NotEditable, SelectionOrigin.UiAutomation, 5);
    }

    [Fact]
    public void Policy_skip_wins_and_records_the_snapshots_own_geometry()
    {
        var withBounds = SelectionFinish.Decide(Snap(" "), new AppSettings(), explicitRequest: false, Pointer, 0);
        Assert.Equal("empty selection", Assert.IsType<ProbeOutcome.Skipped>(withBounds.Outcome).Reason);
        Assert.True(withBounds.HadGeometry);
        Assert.False(withBounds.AnchoredAtPointer);

        // Without bounds and recent pointer: still the policy reason, not an anchor.
        var noBounds = SelectionFinish.Decide(Snap(" ", geometry: false), new AppSettings(), explicitRequest: false, Pointer, 0);
        Assert.IsType<ProbeOutcome.Skipped>(noBounds.Outcome);
        Assert.False(noBounds.HadGeometry);
        Assert.False(noBounds.AnchoredAtPointer);
    }

    [Fact]
    public void Excluded_app_is_skipped_with_the_policy_reason()
    {
        var settings = new AppSettings();
        settings.SetProcessExcluded("app", true);
        var result = SelectionFinish.Decide(Snap("some text"), settings, explicitRequest: false, Pointer, 0);
        Assert.Equal("translation paused for app", Assert.IsType<ProbeOutcome.Skipped>(result.Outcome).Reason);
    }

    [Fact]
    public void Selection_with_bounds_is_selected_unchanged()
    {
        var snap = Snap("some text");
        var result = SelectionFinish.Decide(snap, new AppSettings(), explicitRequest: false, Pointer, 99999);
        Assert.Same(snap, Assert.IsType<ProbeOutcome.Selected>(result.Outcome).Snapshot);
        Assert.True(result.HadGeometry);
        Assert.False(result.AnchoredAtPointer);
        Assert.False(snap.AnchoredAtPointer);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1499)]
    [InlineData(1500)]
    public void No_bounds_right_after_a_pointer_event_anchors_at_the_pointer(long ageMs)
    {
        var result = SelectionFinish.Decide(Snap("some text", geometry: false), new AppSettings(), explicitRequest: false, Pointer, ageMs);
        var selected = Assert.IsType<ProbeOutcome.Selected>(result.Outcome);
        Assert.True(selected.Snapshot.AnchoredAtPointer);
        Assert.Equal(AnchorRect.AroundPoint(Pointer), selected.Snapshot.Anchor);
        Assert.Equal("some text", selected.Snapshot.Text);
        Assert.False(result.HadGeometry);
        Assert.True(result.AnchoredAtPointer);
    }

    [Theory]
    [InlineData(1501)]
    [InlineData(60000)]
    public void No_bounds_long_after_a_pointer_event_is_skipped(long ageMs)
    {
        var result = SelectionFinish.Decide(Snap("some text", geometry: false), new AppSettings(), explicitRequest: false, Pointer, ageMs);
        Assert.Equal("selection reported without on-screen bounds", Assert.IsType<ProbeOutcome.Skipped>(result.Outcome).Reason);
        Assert.False(result.HadGeometry);
        Assert.False(result.AnchoredAtPointer);
    }

    [Fact]
    public void ForText_anchors_at_the_pointer_and_keeps_origin_and_source()
    {
        var outcome = SelectionFinish.ForText("copied text", SelectionOrigin.Clipboard, 4242, 9, "notepad", Pointer, new AppSettings(), explicitRequest: true);
        var snap = Assert.IsType<ProbeOutcome.Selected>(outcome).Snapshot;
        Assert.Equal("copied text", snap.Text);
        Assert.Equal(SelectionOrigin.Clipboard, snap.Origin);
        Assert.True(snap.AnchoredAtPointer);
        Assert.Equal(AnchorRect.AroundPoint(Pointer), snap.Anchor);
        Assert.Equal(4242, snap.SourceWindow);
        Assert.Equal(9, snap.ProcessId);
        Assert.Equal("notepad", snap.ProcessName);
        Assert.Equal(EditableKind.NotEditable, snap.Editable);
    }

    [Fact]
    public void ForText_skips_text_the_policy_rejects()
    {
        var empty = SelectionFinish.ForText("  ", SelectionOrigin.Clipboard, 1, 1, "app", Pointer, new AppSettings(), explicitRequest: true);
        Assert.Equal("empty selection", Assert.IsType<ProbeOutcome.Skipped>(empty).Reason);

        var settings = new AppSettings { MaxChars = 5 };
        var tooLong = SelectionFinish.ForText("123456789", SelectionOrigin.Clipboard, 1, 1, "app", Pointer, settings, explicitRequest: true);
        Assert.Equal("selection longer than 5 characters", Assert.IsType<ProbeOutcome.Skipped>(tooLong).Reason);
    }

    [Fact]
    public void ForText_explicit_request_ignores_the_automatic_mode_rules()
    {
        // One letter is below MinChars (2) and digits alone have nothing to translate; both pass for an explicit request.
        Assert.IsType<ProbeOutcome.Selected>(SelectionFinish.ForText("a", SelectionOrigin.Clipboard, 1, 1, "app", Pointer, new AppSettings(), explicitRequest: true));
        Assert.IsType<ProbeOutcome.Selected>(SelectionFinish.ForText("12345", SelectionOrigin.Clipboard, 1, 1, "app", Pointer, new AppSettings(), explicitRequest: true));
        Assert.IsType<ProbeOutcome.Skipped>(SelectionFinish.ForText("a", SelectionOrigin.Clipboard, 1, 1, "app", Pointer, new AppSettings(), explicitRequest: false));
    }
}
