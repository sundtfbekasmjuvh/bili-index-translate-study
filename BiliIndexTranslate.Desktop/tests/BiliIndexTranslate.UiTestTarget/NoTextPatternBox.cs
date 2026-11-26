using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Input;
using System.Windows.Media;

namespace BiliIndexTranslate.UiTestTarget;

/// <summary>
/// Custom-drawn, focusable control that exposes no UI Automation text pattern, so the app's probe finds no
/// selection and falls back to the explicit Ctrl+C clipboard capture. On Ctrl+C it puts <see cref="CopyText"/>
/// on the clipboard.
/// </summary>
public sealed class NoTextPatternBox : FrameworkElement
{
    public const string CopyText = "Custom control text from Ctrl+C";

    public NoTextPatternBox()
    {
        Focusable = true;
        FocusVisualStyle = null;
    }

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Brushes.White, new Pen(IsKeyboardFocused ? Brushes.SteelBlue : Brushes.Gray, 1), new Rect(0, 0, ActualWidth, ActualHeight));
        var text = new FormattedText(CopyText, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), 18, Brushes.Black, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        dc.DrawText(text, new Point(8, 8));
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        Focus();
        Keyboard.Focus(this);
        e.Handled = true;
    }

    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e) { base.OnGotKeyboardFocus(e); InvalidateVisual(); }
    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e) { base.OnLostKeyboardFocus(e); InvalidateVisual(); }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control)
        {
            for (var attempt = 0; attempt < 10; attempt++)
            {
                try { Clipboard.SetDataObject(CopyText, true); break; }
                catch (COMException) { Thread.Sleep(40); }
            }
            e.Handled = true;
        }
        base.OnKeyDown(e);
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new BoxPeer(this);

    /// <summary>Visible to UI Automation as a focusable control, but without any pattern (in particular no Text pattern).</summary>
    private sealed class BoxPeer : FrameworkElementAutomationPeer
    {
        public BoxPeer(NoTextPatternBox owner) : base(owner) { }
        protected override string GetClassNameCore() => nameof(NoTextPatternBox);
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Custom;
        protected override bool IsControlElementCore() => true;
        protected override bool IsKeyboardFocusableCore() => true;
        public override object? GetPattern(PatternInterface patternInterface) => null;
    }
}
