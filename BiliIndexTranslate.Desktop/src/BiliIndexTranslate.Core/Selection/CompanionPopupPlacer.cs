using BiliIndexTranslate.Core.Geometry;

namespace BiliIndexTranslate.Core.Selection;

public enum CompanionSide
{
    Right,
    Left,
    Below,
    Above,
}

/// <summary>Result of companion Study Card placement relative to the primary translation card.</summary>
public sealed record CompanionPlacement(PxRect WindowRect, PxRect CardRect, CompanionSide Side, MonitorArea Monitor);

/// <summary>
/// Pure physical-pixel placement math for the companion Study Card floating window.
/// Prefers placing side-by-side (Right, then Left) beside the primary translation card, and falls
/// back to Below or Above when the monitor work area is narrow.
/// </summary>
public static class CompanionPopupPlacer
{
    public const double GapLogical = 10;
    public const double EdgeMarginLogical = 8;

    public static CompanionPlacement Place(
        PxRect primaryCardRect,
        int cardWidth,
        int cardHeight,
        PxInsets insets,
        MonitorArea monitor)
    {
        var work = monitor.WorkArea;
        var gap = monitor.Px(GapLogical);
        var margin = monitor.Px(EdgeMarginLogical);

        cardWidth = Math.Max(1, Math.Min(cardWidth, Math.Max(1, work.Width - 2 * margin)));
        cardHeight = Math.Max(1, Math.Min(cardHeight, Math.Max(1, work.Height - 2 * margin)));

        var rightLeft = primaryCardRect.Right + gap;
        var leftLeft = primaryCardRect.Left - gap - cardWidth;
        var belowTop = primaryCardRect.Bottom + gap;
        var aboveTop = primaryCardRect.Top - gap - cardHeight;

        var fitsRight = rightLeft + cardWidth <= work.Right - margin;
        var fitsLeft = leftLeft >= work.Left + margin;
        var fitsBelow = belowTop + cardHeight <= work.Bottom - margin;
        var fitsAbove = aboveTop >= work.Top + margin;

        CompanionSide side;
        int left;
        int top;

        if (fitsRight)
        {
            side = CompanionSide.Right;
            left = rightLeft;
            top = primaryCardRect.Top;
        }
        else if (fitsLeft)
        {
            side = CompanionSide.Left;
            left = leftLeft;
            top = primaryCardRect.Top;
        }
        else if (fitsBelow || (!fitsAbove && (work.Bottom - primaryCardRect.Bottom) >= (primaryCardRect.Top - work.Top)))
        {
            side = CompanionSide.Below;
            left = primaryCardRect.Left;
            top = belowTop;
        }
        else
        {
            side = CompanionSide.Above;
            left = primaryCardRect.Left;
            top = aboveTop;
        }

        var minLeft = work.Left + margin;
        var maxLeft = work.Right - margin - cardWidth;
        left = maxLeft < minLeft ? minLeft : Math.Clamp(left, minLeft, maxLeft);

        var minTop = work.Top + margin;
        var maxTop = work.Bottom - margin - cardHeight;
        top = maxTop < minTop ? minTop : Math.Clamp(top, minTop, maxTop);

        var card = PxRect.FromSize(left, top, cardWidth, cardHeight);
        var window = new PxRect(card.Left - insets.Left, card.Top - insets.Top, card.Right + insets.Right, card.Bottom + insets.Bottom);
        return new CompanionPlacement(window, card, side, monitor);
    }

    public static (int MaxWidthPx, int MaxHeightPx) MaxCardSize(MonitorArea monitor, double maxWidthLogical = 408, double maxHeightFraction = 0.62)
    {
        var margin = monitor.Px(EdgeMarginLogical);
        var maxW = Math.Min(monitor.Px(maxWidthLogical), Math.Max(160, monitor.WorkArea.Width - 2 * margin));
        var maxH = Math.Max(monitor.Px(120), (int)(monitor.WorkArea.Height * maxHeightFraction));
        return (maxW, maxH);
    }
}
