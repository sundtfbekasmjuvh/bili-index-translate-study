using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BiliIndexTranslate.App.Input;
using BiliIndexTranslate.App.Startup;
using BiliIndexTranslate.Core.Settings;
using BiliIndexTranslate.Core.Updates;
using BiliIndexTranslate.Core.Translation;
using BiliIndexTranslate.App.Localization;
using BiliIndexTranslate.Core.Localization;

namespace BiliIndexTranslate.App.Settings;

/// <summary>What the Settings window needs for its update section: whether this build can update, its version, and the manual check.</summary>
public sealed record SettingsUpdateInfo(bool Supported, string VersionText, Func<Task<UpdateCheckOutcome>> CheckNow);

[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable",
    Justification = "WPF Window is not IDisposable; _testCts is only cancelled on close (existing lifetime, kept unchanged) and its 20 s timer expires on its own.")]
public partial class SettingsWindow : Window
{
    private readonly SettingsUpdateInfo _updates;
    /// <summary>Whether the Run entry started this exe when the window opened (or after the last successful change).</summary>
    private bool _autoStartInitial;
    private readonly AppSettings _original;
    private readonly ObservableCollection<string> _excluded;
    private readonly Func<AppSettings, CancellationToken, Task<(bool Ok, string Message)>> _tester;
    /// <summary>Applies and stores the settings; returns why they could not be written to disk, or null.</summary>
    private readonly Func<AppSettings, string?> _save;
    private readonly string _settingsPath;
    private CancellationTokenSource? _testCts;
    private bool _keyRevealed;

    public SettingsWindow(AppSettings current, string settingsPath,
        Func<AppSettings, CancellationToken, Task<(bool Ok, string Message)>> tester,
        Func<AppSettings, string?> save,
        SettingsUpdateInfo updates)
    {
        InitializeComponent();
        _updates = updates;
        _original = current.Clone();
        _excluded = new ObservableCollection<string>(current.ExcludedProcesses);
        _tester = tester;
        _save = save;
        _settingsPath = settingsPath;
        Populate(current);
        Closed += (_, _) => _testCts?.Cancel();
    }

    /// <summary>Shows a transient hotkey registration status from the host.</summary>
    public void SetHotkeyStatus(string? error)
    {
        if (error is null) Loc.Bind(HotkeyStatus, TextBlock.TextProperty, "Settings_HotkeyHelp");
        else HotkeyStatus.Text = error;
        HotkeyStatus.Foreground = error is null ? (Brush)FindResource("Ui.TextSecondary") : (Brush)FindResource("Ui.Error");
    }

    public void SetSnipHotkeyStatus(string? error)
    {
        if (error is null) Loc.Bind(SnipHotkeyStatus, TextBlock.TextProperty, "Settings_SnipHelp");
        else SnipHotkeyStatus.Text = error;
        SnipHotkeyStatus.Foreground = error is null ? (Brush)FindResource("Ui.TextSecondary") : (Brush)FindResource("Ui.Error");
    }

    private void Populate(AppSettings s)
    {
        AutoTranslateBox.IsChecked = s.AutoTranslate;
        foreach (var lang in TargetLanguage.All)
            TargetLanguageBox.Items.Add(new ComboBoxItem { Content = $"{lang.DisplayName}  ·  {lang.PromptName}", Tag = lang.Code });
        TargetLanguageBox.SelectedIndex = Math.Max(0, TargetLanguage.All.ToList().FindIndex(l => l.Code == s.TargetLanguage));
        TextScaleSlider.Value = s.TextScale;
        TextScaleLabel.Text = $"{s.TextScale * 100:0}%";
        ThemeBox.SelectedIndex = (int)s.Theme;
        UiLanguageBox.SelectedIndex = s.UiLanguage switch { InterfaceLanguage.English => 1, InterfaceLanguage.SimplifiedChinese => 2, _ => 0 };
        EditablePolicyBox.SelectedIndex = (int)s.EditableFieldPolicy;
        ShowOriginalBox.IsChecked = s.ShowOriginalByDefault;

        ApiFormatBox.SelectedIndex = (int)s.ApiFormat;
        var noPreset = new ComboBoxItem();
        Loc.Bind(noPreset, ContentControl.ContentProperty, "Settings_LoadPreset");
        CustomPresetBox.Items.Add(noPreset);
        foreach (var preset in CustomApiPreset.All)
            CustomPresetBox.Items.Add(new ComboBoxItem { Content = preset.DisplayName, Tag = preset.Id });
        CustomPresetBox.SelectedIndex = 0;
        ShowTemplate(s.CustomTemplate);
        SyncCustomPanel();
        EndpointBox.Text = s.BaseUrl;
        ApiKeyBox.Password = s.ApiKey;
        foreach (var m in AppSettings.KnownModels) ModelBox.Items.Add(m);
        ModelBox.Text = s.Model;
        foreach (var vm in AppSettings.KnownVisionModels) VisionModelBox.Items.Add(vm);
        VisionModelBox.Text = s.VisionModel;
        VisionModeBox.SelectedIndex = (int)s.VisionMode;
        RelayBox.IsChecked = s.UseRelay;
        RelayBox2.Text = s.RelayPrefix;
        RelayBox2.IsEnabled = s.UseRelay;
        GlossaryBox.Text = s.GlossaryText;

        ExcludedList.ItemsSource = _excluded;

        SnipHotkeyEnabledBox.IsChecked = s.SnipHotkeyEnabled;
        SnipHotkeyBox.Text = s.SnipHotkey;
        HotkeyEnabledBox.IsChecked = s.HotkeyEnabled;
        HotkeyBox.Text = s.Hotkey;

        DelaySlider.Value = s.StabilizationDelayMs;
        DelayLabel.Text = $"{s.StabilizationDelayMs} ms";
        UiaEventsBox.IsChecked = s.UseUiaSelectionEvents;
        FileLoggingBox.IsChecked = s.DiagnosticsFileLogging;
        SettingsPathRun.Text = _settingsPath;

        // The Run entry decides the start-up box; an entry that points to another exe shows as not set.
        _autoStartInitial = AutoStart.IsRegisteredForCurrentExe();
        RunAtStartupBox.IsChecked = _autoStartInitial;
        AutoCheckUpdatesBox.IsChecked = s.AutoCheckForUpdates;
        VersionRun.Text = _updates.VersionText;
        AutoCheckUpdatesBox.IsEnabled = CheckUpdatesButton.IsEnabled = _updates.Supported;
        UpdatesHelpText.Visibility = _updates.Supported ? Visibility.Visible : Visibility.Collapsed;
        UpdatesPortableText.Visibility = _updates.Supported ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>Writes or deletes the Run entry when the box differs from what the registry had. Returns the error text, or null.</summary>
    private string? ApplyStartupChoice()
    {
        var want = RunAtStartupBox.IsChecked == true;
        if (want == _autoStartInitial) return null;
        try
        {
            if (want) AutoStart.Register();
            else AutoStart.Unregister();
            _autoStartInitial = want;
            return null;
        }
        catch (Exception ex)
        {
            return Loc.T("Settings_StartupFailed", ex.GetType().Name);
        }
    }

    private async void OnCheckUpdates(object sender, RoutedEventArgs e)
    {
        CheckUpdatesButton.IsEnabled = false;
        ShowUpdateResult(Loc.T("Settings_Checking"), UpdateTone.Neutral);
        try
        {
            ShowUpdateOutcome(await _updates.CheckNow());
        }
        catch (Exception ex)
        {
            ShowUpdateResult(Loc.T("Update_Failed", UpdateErrorText.Describe(ex)), UpdateTone.Error);
        }
        finally
        {
            CheckUpdatesButton.IsEnabled = _updates.Supported;
        }
    }

    /// <summary>The result of a manual check, as text in the window.</summary>
    public void ShowUpdateOutcome(UpdateCheckOutcome outcome)
    {
        switch (outcome.Status)
        {
            case UpdateCheckStatus.UpToDate: ShowUpdateResult(Loc.T("Update_UpToDate"), UpdateTone.Good); break;
            case UpdateCheckStatus.Downloading: ShowUpdateResult(Loc.T("Update_Found", outcome.Version), UpdateTone.Neutral); break;
            case UpdateCheckStatus.Downloaded: ShowUpdateResult(Loc.T("Update_Ready", outcome.Version), UpdateTone.Good); break;
            case UpdateCheckStatus.Failed: ShowUpdateResult(Loc.T("Update_Failed", outcome.Reason), UpdateTone.Error); break;
            case UpdateCheckStatus.Busy: ShowUpdateResult(Loc.T("Update_Busy"), UpdateTone.Neutral); break;
            case UpdateCheckStatus.NotInstalled: ShowUpdateResult(Loc.T("Settings_UpdatesPortable"), UpdateTone.Neutral); break;
            default: UpdateResultText.Visibility = Visibility.Collapsed; break;
        }
    }

    /// <summary>The background download that followed a manual check has finished.</summary>
    public void ShowUpdateDownloaded(string version) => ShowUpdateResult(Loc.T("Update_Ready", version), UpdateTone.Good);

    public void ShowUpdateDownloadFailed(string reason) => ShowUpdateResult(Loc.T("Update_DownloadFailed", reason), UpdateTone.Error);

    private enum UpdateTone { Neutral, Good, Error }

    private void ShowUpdateResult(string text, UpdateTone tone)
    {
        UpdateResultText.Text = text;
        UpdateResultText.Foreground = (Brush)FindResource(tone switch { UpdateTone.Good => "Ui.Success", UpdateTone.Error => "Ui.Error", _ => "Ui.TextSecondary" });
        UpdateResultText.Visibility = Visibility.Visible;
    }

    /// <summary>Builds a settings object from what is currently on screen (not what was saved).</summary>
    private AppSettings? ReadForm(out string? error)
    {
        error = null;
        var s = _original.Clone();
        s.AutoTranslate = AutoTranslateBox.IsChecked == true;
        s.TargetLanguage = (TargetLanguageBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "zh-Hans";
        s.TextScale = TextScaleSlider.Value;
        s.Theme = (ThemePreference)Math.Max(0, ThemeBox.SelectedIndex);
        s.UiLanguage = UiLanguageBox.SelectedIndex switch { 1 => InterfaceLanguage.English, 2 => InterfaceLanguage.SimplifiedChinese, _ => InterfaceLanguage.Auto };
        s.EditableFieldPolicy = (EditableFieldPolicy)Math.Max(0, EditablePolicyBox.SelectedIndex);
        s.ShowOriginalByDefault = ShowOriginalBox.IsChecked == true;

        s.ApiFormat = (ApiFormat)Math.Max(0, ApiFormatBox.SelectedIndex);
        s.CustomName = CustomNameBox.Text.Trim();
        s.CustomMethod = CustomMethodBox.SelectedIndex == 1 ? "GET" : "POST";
        s.CustomHeaders = CustomHeadersBox.Text;
        s.CustomBody = CustomBodyBox.Text;
        s.CustomResultPath = CustomResultPathBox.Text.Trim();
        s.CustomErrorPath = CustomErrorPathBox.Text.Trim();
        s.BaseUrl = EndpointBox.Text.Trim();
        s.ApiKey = _keyRevealed ? ApiKeyPlainBox.Text : ApiKeyBox.Password;
        s.Model = ModelBox.Text.Trim();
        s.VisionModel = VisionModelBox.Text.Trim();
        s.VisionMode = (VisionMode)Math.Max(0, VisionModeBox.SelectedIndex);
        s.UseRelay = RelayBox.IsChecked == true;
        s.RelayPrefix = RelayBox2.Text.Trim();
        s.GlossaryText = GlossaryBox.Text;
        s.ExcludedProcesses = _excluded.ToList();

        s.SnipHotkeyEnabled = SnipHotkeyEnabledBox.IsChecked == true;
        s.SnipHotkey = SnipHotkeyBox.Text.Trim();
        s.HotkeyEnabled = HotkeyEnabledBox.IsChecked == true;
        s.Hotkey = HotkeyBox.Text.Trim();
        s.StabilizationDelayMs = (int)DelaySlider.Value;
        s.UseUiaSelectionEvents = UiaEventsBox.IsChecked == true;
        s.DiagnosticsFileLogging = FileLoggingBox.IsChecked == true;
        s.AutoCheckForUpdates = AutoCheckUpdatesBox.IsChecked == true;

        if (!EndpointResolver.TryResolve(s.ApiFormat, s.BaseUrl, s.UseRelay, s.RelayPrefix, s.ApiKey, out var endpoint, out var epError))
        {
            error = epError;
            return null;
        }
        // A template that cannot work is reported here instead of on the first translation.
        if (s.ApiFormat == ApiFormat.Custom
            && (!CustomApiProtocol.TryParseTemplate(s.CustomTemplate, endpoint!.NormalizedBase, out var template, out var templateError)
                || !CustomApiProtocol.TryBuildRequest(template!, endpoint.NormalizedBase, s.ApiKey, ["sample"], s.TargetLanguage, out _, out templateError)))
        {
            error = templateError;
            return null;
        }
        if (string.IsNullOrWhiteSpace(s.Model)) { error = Loc.T("Settings_ChooseModel"); return null; }
        if (string.IsNullOrWhiteSpace(s.VisionModel)) { error = Loc.T("Settings_ChooseVisionModel"); return null; }
        if (s.SnipHotkeyEnabled && !HotkeyParser.TryParse(s.SnipHotkey, out _)) { error = Loc.T("Settings_SnipShortcutInvalid"); return null; }
        if (s.HotkeyEnabled && !HotkeyParser.TryParse(s.Hotkey, out _)) { error = Loc.T("Settings_ShortcutInvalid"); return null; }
        s.Normalize();
        return s;
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        var s = ReadForm(out var error);
        if (s is null) { ShowStatus(error!, isError: true); return; }
        if (ApplyStartupChoice() is { } startupError) { ShowStatus(startupError, isError: true); return; }
        // The window stays open when the file could not be written, so that the reason is seen.
        if (_save(s) is { } saveError) { ShowStatus(saveError, isError: true); return; }
        Close();
    }

    private async void OnSaveAndTest(object sender, RoutedEventArgs e)
    {
        var s = ReadForm(out var error);
        if (s is null) { ShowStatus(error!, isError: true); return; }
        if (ApplyStartupChoice() is { } startupError) { ShowStatus(startupError, isError: true); return; }
        var saveError = _save(s);

        _testCts?.Cancel();
        _testCts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        SaveTestButton.IsEnabled = false;
        ShowStatus(Loc.T(saveError is null ? "Settings_SavedTesting" : "Settings_NotSavedTesting"), isError: saveError is not null);
        try
        {
            var (ok, message) = await _tester(s, _testCts.Token);
            if (saveError is null) ShowStatus(message, isError: !ok);
            else ShowStatus(Loc.T("Settings_SaveErrorWithTest", saveError, message), isError: true);
        }
        catch (OperationCanceledException) { ShowStatus(Loc.T("Settings_TestCancelled"), isError: true); }
        catch (Exception ex) { ShowStatus(Loc.T("Settings_TestFailed", ex.GetType().Name), isError: true); }
        finally { SaveTestButton.IsEnabled = true; }
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Close();

    private void ShowStatus(string text, bool isError)
    {
        StatusText.Text = text;
        StatusText.Foreground = isError ? (Brush)FindResource("Ui.Error") : (Brush)FindResource("Ui.Success");
    }

    private void OnTextScaleChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (TextScaleLabel is not null) TextScaleLabel.Text = $"{e.NewValue * 100:0}%";
    }

    private void OnResetTextScale(object sender, RoutedEventArgs e) => TextScaleSlider.Value = 1.0;

    private void OnDelayChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (DelayLabel is not null) DelayLabel.Text = $"{(int)e.NewValue} ms";
    }

    private void OnApiFormatChanged(object sender, SelectionChangedEventArgs e) => SyncCustomPanel();

    /// <summary>The template fields only apply to the custom format.</summary>
    private void SyncCustomPanel()
    {
        if (CustomPanel is null) return;
        CustomPanel.Visibility = ApiFormatBox.SelectedIndex == (int)ApiFormat.Custom ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowTemplate(CustomApiTemplate template)
    {
        CustomNameBox.Text = template.Name;
        CustomMethodBox.SelectedIndex = string.Equals(template.Method, "GET", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        CustomHeadersBox.Text = template.Headers;
        CustomBodyBox.Text = template.Body;
        CustomResultPathBox.Text = template.ResultPath;
        CustomErrorPathBox.Text = template.ErrorPath;
    }

    /// <summary>A preset fills in the form only; nothing is saved until the user saves.</summary>
    private void OnCustomPresetChanged(object sender, SelectionChangedEventArgs e)
    {
        if ((CustomPresetBox.SelectedItem as ComboBoxItem)?.Tag is not string id) return;
        var preset = CustomApiPreset.All.FirstOrDefault(p => p.Id == id);
        if (preset is null) return;
        EndpointBox.Text = preset.BaseUrl;
        ShowTemplate(preset.Template);
        CustomPresetBox.SelectedIndex = 0;
        ShowStatus(Loc.T("Settings_PresetLoaded", preset.Template.Label), isError: false);
    }

    private void OnRelayToggled(object sender, RoutedEventArgs e)
    {
        if (RelayBox2 is not null) RelayBox2.IsEnabled = RelayBox.IsChecked == true;
    }

    private void OnRevealKey(object sender, RoutedEventArgs e)
    {
        _keyRevealed = !_keyRevealed;
        if (_keyRevealed)
        {
            ApiKeyPlainBox.Text = ApiKeyBox.Password;
            ApiKeyPlainBox.Visibility = Visibility.Visible;
            ApiKeyBox.Visibility = Visibility.Collapsed;
            Loc.Bind(RevealKeyButton, ContentControl.ContentProperty, "Settings_Hide");
        }
        else
        {
            ApiKeyBox.Password = ApiKeyPlainBox.Text;
            ApiKeyBox.Visibility = Visibility.Visible;
            ApiKeyPlainBox.Visibility = Visibility.Collapsed;
            Loc.Bind(RevealKeyButton, ContentControl.ContentProperty, "Settings_Show");
        }
    }

    private void OnAddExcluded(object sender, RoutedEventArgs e)
    {
        var name = NewExcludedBox.Text.Trim();
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        if (name.Length == 0) return;
        if (!_excluded.Any(x => string.Equals(x, name, StringComparison.OrdinalIgnoreCase))) _excluded.Add(name);
        NewExcludedBox.Clear();
    }

    private void OnRemoveExcluded(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is string name) _excluded.Remove(name);
    }
}
