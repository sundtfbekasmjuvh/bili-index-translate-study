using BiliIndexTranslate.Core.Geometry;
using BiliIndexTranslate.Core.Settings;

namespace BiliIndexTranslate.Core.Selection;

/// <summary>What <see cref="SelectionFinish.Decide"/> decided about a selection read through UI Automation.</summary>
/// <param name="Outcome">Selected (with the snapshot to use, possibly anchored at the pointer) or Skipped (with the reason).</param>
/// <param name="HadGeometry">What to record for the app in the support registry: the selection came with its own on-screen bounds.</param>
/// <param name="AnchoredAtPointer">The selection had no bounds and the anchor is a box around the pointer (the app logs that).</param>
public sealed record SelectionFinishResult(ProbeOutcome Outcome, bool HadGeometry, bool AnchoredAtPointer);

/// <summary>The pure decisions that turn a probe's selection (or captured text) into a probe outcome.</summary>
public static class SelectionFinish
{
    /// <summary>A selection without bounds is anchored at the pointer only when the last pointer event is at most this old.</summary>
    public const long PointerAnchorMaxAgeMs = 1500;

    public const string NoBoundsReason = "selection reported without on-screen bounds";

    /// <summary>
    /// Applies the selection policy, then handles a selection without on-screen bounds: anchored at the pointer only right
    /// after a mouse release (<paramref name="msSinceLastPointer"/> at most <see cref="PointerAnchorMaxAgeMs"/>), never guessed otherwise.
    /// </summary>
    public static SelectionFinishResult Decide(SelectionSnapshot snapshot, AppSettings settings, bool explicitRequest, PxPoint lastPointer, long msSinceLastPointer)
    {
        var reason = SelectionPolicy.ReasonToSkip(snapshot, settings, explicitRequest);
        if (reason is not null)
            return new SelectionFinishResult(new ProbeOutcome.Skipped(reason), snapshot.HasGeometry, false);

        if (snapshot.HasGeometry)
            return new SelectionFinishResult(new ProbeOutcome.Selected(snapshot), true, false);

        if (msSinceLastPointer <= PointerAnchorMaxAgeMs)
        {
            var anchored = snapshot with { Anchor = AnchorRect.AroundPoint(lastPointer), AnchoredAtPointer = true };
            return new SelectionFinishResult(new ProbeOutcome.Selected(anchored), false, true);
        }

        return new SelectionFinishResult(new ProbeOutcome.Skipped(NoBoundsReason), false, false);
    }

    /// <summary>Outcome for text captured by an explicit action (clipboard): anchored at the pointer, then the selection policy.</summary>
    public static ProbeOutcome ForText(string text, SelectionOrigin origin, long foregroundWindow, int processId, string processName, PxPoint point, AppSettings settings, bool explicitRequest)
    {
        var snapshot = new SelectionSnapshot(text, AnchorRect.AroundPoint(point), foregroundWindow, processId, processName, EditableKind.NotEditable, origin, 0)
        {
            AnchoredAtPointer = true,
        };
        var reason = SelectionPolicy.ReasonToSkip(snapshot, settings, explicitRequest);
        return reason is null ? new ProbeOutcome.Selected(snapshot) : new ProbeOutcome.Skipped(reason);
    }
}
