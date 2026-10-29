using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Windows.Forms;
using BiliIndexTranslate.Core.Geometry;

namespace BiliIndexTranslate.App.Popup;

internal readonly record struct ScreenSnipCapture(string DataUrl, PxRect ScreenRect);

/// <summary>
/// Full-virtual-screen Per-Monitor-V2 physical-pixel region selector.
/// Freezes the current screen into a RAM bitmap on trigger, lets the user drag a crisp spotlight
/// rectangle across any monitor, encodes the cropped region directly to <c>data:image/png;base64,...</c>
/// in a <see cref="MemoryStream"/>, and immediately disposes all bitmaps (zero disk trace).
/// </summary>
internal static class ScreenSnipOverlay
{
    private const int MinSelectionPx = 14;
    private const int MaxImageEdgePx = 1440;
    private static bool _active;

    public static bool IsActive => _active;

    public static ScreenSnipCapture? CaptureInteractive()
    {
        if (_active) return null;
        _active = true;

        var vs = SystemInformation.VirtualScreen;
        if (vs.Width <= 0 || vs.Height <= 0)
        {
            _active = false;
            return null;
        }

        Bitmap? frozenScreen = null;
        try
        {
            frozenScreen = new Bitmap(vs.Width, vs.Height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(frozenScreen))
            {
                g.CopyFromScreen(vs.Left, vs.Top, 0, 0, vs.Size, CopyPixelOperation.SourceCopy);
            }

            using var dialog = new SnipForm(frozenScreen, vs);
            var dr = dialog.ShowDialog();
            if (dr != DialogResult.OK || dialog.SelectedLocalRect is not { } localRect)
                return null;

            var dataUrl = EncodeCroppedPngDataUrl(frozenScreen, localRect);
            var screenRect = new PxRect(
                vs.Left + localRect.Left,
                vs.Top + localRect.Top,
                vs.Left + localRect.Right,
                vs.Top + localRect.Bottom);

            return new ScreenSnipCapture(dataUrl, screenRect);
        }
        finally
        {
            frozenScreen?.Dispose();
            _active = false;
        }
    }

    public static bool TryReadClipboardImageDataUrl(out string dataUrl)
    {
        dataUrl = string.Empty;
        try
        {
            if (!Clipboard.ContainsImage()) return false;
            using var img = Clipboard.GetImage();
            if (img is null || img.Width < 8 || img.Height < 8) return false;
            using var bmp = new Bitmap(img);
            dataUrl = EncodeCroppedPngDataUrl(bmp, new Rectangle(0, 0, bmp.Width, bmp.Height));
            return dataUrl.Length > 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static string EncodeCroppedPngDataUrl(Bitmap source, Rectangle crop)
    {
        var safeCrop = Rectangle.Intersect(crop, new Rectangle(0, 0, source.Width, source.Height));
        if (safeCrop.Width <= 0 || safeCrop.Height <= 0)
            return string.Empty;

        var targetW = safeCrop.Width;
        var targetH = safeCrop.Height;
        var maxEdge = Math.Max(targetW, targetH);
        if (maxEdge > MaxImageEdgePx)
        {
            var scale = (double)MaxImageEdgePx / maxEdge;
            targetW = Math.Max(1, (int)Math.Round(targetW * scale));
            targetH = Math.Max(1, (int)Math.Round(targetH * scale));
        }

        using var cropped = new Bitmap(targetW, targetH, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(cropped))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.SmoothingMode = SmoothingMode.HighQuality;
            g.DrawImage(source, new Rectangle(0, 0, targetW, targetH), safeCrop, GraphicsUnit.Pixel);
        }

        using var ms = new MemoryStream();
        cropped.Save(ms, ImageFormat.Png);
        return "data:image/png;base64," + Convert.ToBase64String(ms.GetBuffer(), 0, (int)ms.Length);
    }

    private sealed class SnipForm : Form
    {
        private readonly Bitmap _frozen;
        private Point _start;
        private Point _current;
        private bool _dragging;

        public Rectangle? SelectedLocalRect { get; private set; }

        public SnipForm(Bitmap frozen, Rectangle virtualScreen)
        {
            _frozen = frozen;
            AutoScaleMode = AutoScaleMode.None;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            Bounds = virtualScreen;
            TopMost = true;
            ShowInTaskbar = false;
            KeyPreview = true;
            Cursor = Cursors.Cross;
            DoubleBuffered = true;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Activate();
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape)
            {
                DialogResult = DialogResult.Cancel;
                Close();
                return;
            }
            base.OnKeyDown(e);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left)
            {
                DialogResult = DialogResult.Cancel;
                Close();
                return;
            }
            _dragging = true;
            _start = e.Location;
            _current = e.Location;
            Invalidate();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (!_dragging) return;
            _current = e.Location;
            Invalidate();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (!_dragging) return;
            _dragging = false;
            _current = e.Location;

            var rect = BuildRect(_start, _current);
            if (rect.Width < MinSelectionPx || rect.Height < MinSelectionPx)
            {
                DialogResult = DialogResult.Cancel;
                Close();
                return;
            }

            SelectedLocalRect = rect;
            DialogResult = DialogResult.OK;
            Close();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.DrawImageUnscaled(_frozen, 0, 0);

            using var dimBrush = new SolidBrush(Color.FromArgb(92, 15, 23, 42));
            if (_dragging)
            {
                var sel = BuildRect(_start, _current);
                using (var region = new Region(new Rectangle(0, 0, Width, Height)))
                {
                    region.Exclude(sel);
                    g.FillRegion(dimBrush, region);
                }

                if (sel.Width > 1 && sel.Height > 1)
                {
                    using var borderPen = new Pen(Color.FromArgb(255, 59, 130, 246), 2f);
                    g.DrawRectangle(borderPen, sel.X, sel.Y, sel.Width - 1, sel.Height - 1);

                    var label = $"{sel.Width} × {sel.Height} px  ·  松开即识图直译";
                    DrawPill(g, label, sel.X, Math.Max(8, sel.Y - 30));
                }
            }
            else
            {
                g.FillRectangle(dimBrush, 0, 0, Width, Height);
                var primary = Screen.PrimaryScreen?.Bounds ?? new Rectangle(0, 0, Width, Height);
                var localCenterX = primary.Left - Left + primary.Width / 2;
                var localTopY = primary.Top - Top + 28;
                DrawPill(g, "拖拽框选屏幕任意图片 / 视频字幕 / 游戏界面直译 + 词卡  ·  Esc 取消", localCenterX - 210, localTopY);
            }
        }

        private static void DrawPill(Graphics g, string text, int x, int y)
        {
            using var font = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Regular, GraphicsUnit.Point);
            var size = TextRenderer.MeasureText(g, text, font);
            var box = new Rectangle(Math.Max(8, x), Math.Max(8, y), size.Width + 20, size.Height + 10);
            using var bg = new SolidBrush(Color.FromArgb(216, 15, 23, 42));
            g.FillRectangle(bg, box);
            using var border = new Pen(Color.FromArgb(64, 255, 255, 255), 1f);
            g.DrawRectangle(border, box);
            TextRenderer.DrawText(g, text, font, new Point(box.X + 10, box.Y + 5), Color.FromArgb(248, 250, 252));
        }

        private static Rectangle BuildRect(Point a, Point b)
        {
            var x = Math.Min(a.X, b.X);
            var y = Math.Min(a.Y, b.Y);
            return new Rectangle(x, y, Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));
        }
    }
}
