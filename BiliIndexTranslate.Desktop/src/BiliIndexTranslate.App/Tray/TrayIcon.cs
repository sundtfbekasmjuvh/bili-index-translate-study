using System.Drawing;
using System.Windows.Forms;
using BiliIndexTranslate.App.Localization;

namespace BiliIndexTranslate.App.Tray;

/// <summary>
/// Notification-area icon and its compact menu. Uses WinForms NotifyIcon (WPF has none).
/// The icon switches to a muted variant while automatic translation is paused.
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly ContextMenuStrip _menu;
    private readonly ToolStripMenuItem _autoItem;
    private readonly ToolStripMenuItem _appItem;
    private readonly ToolStripMenuItem _updateItem;
    private readonly ToolStripSeparator _updateSeparator;
    private readonly Icon _normal;
    private readonly Icon _paused;
    private Func<string?>? _currentAppProvider;

    public TrayIcon()
    {
        _normal = LoadIcon("app.ico");
        _paused = LoadIcon("app-paused.ico");

        _menu = new ContextMenuStrip { ShowImageMargin = false, ShowCheckMargin = true };
        _autoItem = new ToolStripMenuItem(Loc.T("Tray_Auto")) { CheckOnClick = true };
        _autoItem.CheckedChanged += (_, _) => AutoTranslateToggled?.Invoke(_autoItem.Checked);
        _appItem = new ToolStripMenuItem(Loc.T("Tray_ExcludeThisApp")) { CheckOnClick = true, Enabled = false };
        _appItem.Click += (_, _) => { if (_appItem.Tag is string app) ExcludeAppToggled?.Invoke(app, _appItem.Checked); };

        var snip = new ToolStripMenuItem(Loc.T("Tray_Snip"));
        snip.Click += (_, _) => SnipRegionRequested?.Invoke();
        var ocrCopy = new ToolStripMenuItem(Loc.T("Tray_OcrCopy"));
        ocrCopy.Click += (_, _) => OcrCopyRequested?.Invoke();
        var clipboard = new ToolStripMenuItem(Loc.T("Tray_Clipboard"));
        clipboard.Click += (_, _) => TranslateClipboardRequested?.Invoke();
        var settings = new ToolStripMenuItem(Loc.T("Tray_Settings"));
        settings.Click += (_, _) => SettingsRequested?.Invoke();
        var diagnostics = new ToolStripMenuItem(Loc.T("Tray_Diagnostics"));
        diagnostics.Click += (_, _) => DiagnosticsRequested?.Invoke();
        var vocabulary = new ToolStripMenuItem(Loc.T("Tray_Vocabulary"));
        vocabulary.Click += (_, _) => VocabularyRequested?.Invoke();
        var welcome = new ToolStripMenuItem(Loc.T("Tray_Welcome"));
        welcome.Click += (_, _) => WelcomeRequested?.Invoke();
        var exit = new ToolStripMenuItem(Loc.T("Tray_Exit"));
        exit.Click += (_, _) => ExitRequested?.Invoke();

        // Shown only while a downloaded update waits to be installed.
        _updateItem = new ToolStripMenuItem(Loc.T("Tray_RestartUpdate")) { Visible = false };
        _updateItem.Click += (_, _) => RestartUpdateRequested?.Invoke();
        _updateSeparator = new ToolStripSeparator { Visible = false };

        _menu.Items.AddRange(new ToolStripItem[]
        {
            _updateItem, _updateSeparator,
            _autoItem, _appItem, new ToolStripSeparator(),
            snip, ocrCopy, clipboard, new ToolStripSeparator(),
            settings, diagnostics, vocabulary, welcome, new ToolStripSeparator(),
            exit,
        });
        _menu.Opening += (_, _) => RefreshAppItem();

        _icon = new NotifyIcon
        {
            Icon = _normal,
            Text = "Bili Index Translate",
            Visible = true,
            ContextMenuStrip = _menu,
        };
        _icon.DoubleClick += (_, _) => SettingsRequested?.Invoke();

        // The menu is built once, so its text is set again when the interface language changes.
        Loc.Instance.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(Loc.Language)) return;
            _autoItem.Text = Loc.T("Tray_Auto");
            snip.Text = Loc.T("Tray_Snip");
            ocrCopy.Text = Loc.T("Tray_OcrCopy");
            clipboard.Text = Loc.T("Tray_Clipboard");
            settings.Text = Loc.T("Tray_Settings");
            diagnostics.Text = Loc.T("Tray_Diagnostics");
            vocabulary.Text = Loc.T("Tray_Vocabulary");
            welcome.Text = Loc.T("Tray_Welcome");
            exit.Text = Loc.T("Tray_Exit");
            _updateItem.Text = Loc.T("Tray_RestartUpdate");
            _icon.Text = _autoItem.Checked ? "Bili Index Translate" : Loc.T("Tray_Paused");
            RefreshAppItem();
        };
    }

    public event Action<bool>? AutoTranslateToggled;
    public event Action<string, bool>? ExcludeAppToggled;
    public event Action? SnipRegionRequested;
    public event Action? OcrCopyRequested;
    public event Action? TranslateClipboardRequested;
    public event Action? SettingsRequested;
    public event Action? DiagnosticsRequested;
    public event Action? VocabularyRequested;
    public event Action? WelcomeRequested;
    public event Action? ExitRequested;
    public event Action? RestartUpdateRequested;

    /// <summary>Shows or hides the "Restart and update" item (a downloaded update is waiting).</summary>
    public void SetUpdateReady(bool ready)
    {
        _updateItem.Visible = ready;
        _updateSeparator.Visible = ready;
    }

    /// <summary>Supplies the name of the application the user is most likely referring to (last probed / foreground).</summary>
    public void SetCurrentAppProvider(Func<string?> provider) => _currentAppProvider = provider;

    public Func<string, bool>? IsAppExcluded { get; set; }

    public void SetAutoTranslate(bool enabled)
    {
        if (_autoItem.Checked != enabled) _autoItem.Checked = enabled;
        _icon.Icon = enabled ? _normal : _paused;
        _icon.Text = enabled ? "Bili Index Translate" : Loc.T("Tray_Paused");
    }

    public void ShowNotice(string title, string message, bool warning = false)
    {
        try
        {
            _icon.BalloonTipTitle = title;
            _icon.BalloonTipText = message;
            _icon.BalloonTipIcon = warning ? ToolTipIcon.Warning : ToolTipIcon.Info;
            _icon.ShowBalloonTip(4000);
        }
        catch (Exception) { /* notifications may be disabled */ }
    }

    private void RefreshAppItem()
    {
        var app = _currentAppProvider?.Invoke();
        if (string.IsNullOrEmpty(app))
        {
            _appItem.Text = Loc.T("Tray_ExcludeThisApp");
            _appItem.Enabled = false;
            _appItem.Checked = false;
            _appItem.Tag = null;
            return;
        }
        _appItem.Text = Loc.T("Tray_ExcludeApp", app);
        _appItem.Enabled = true;
        _appItem.Tag = app;
        _appItem.Checked = IsAppExcluded?.Invoke(app) ?? false;
    }

    private static Icon LoadIcon(string name)
    {
        try
        {
            var uri = new Uri($"pack://application:,,,/BiliIndexTranslate;component/Assets/{name}");
            var info = System.Windows.Application.GetResourceStream(uri);
            if (info is not null)
            {
                using var stream = info.Stream;
                return new Icon(stream);
            }
        }
        catch (Exception) { }
        return SystemIcons.Application;
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _menu.Dispose();
        if (!ReferenceEquals(_normal, SystemIcons.Application)) _normal.Dispose();
        if (!ReferenceEquals(_paused, SystemIcons.Application)) _paused.Dispose();
    }
}
