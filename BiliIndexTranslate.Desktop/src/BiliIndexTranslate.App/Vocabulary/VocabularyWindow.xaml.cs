using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Input;
using BiliIndexTranslate.App.Localization;
using BiliIndexTranslate.Core.Diagnostics;
using BiliIndexTranslate.Core.Vocabulary;

namespace BiliIndexTranslate.App.Vocabulary;

/// <summary>One line of the list: the entry and the texts shown for it (single line each).</summary>
internal sealed class VocabularyRow
{
    public VocabularyRow(VocabularyEntry entry)
    {
        Entry = entry;
        Text = VocabularyEntry.NormalizeText(entry.Text);
        Preview = VocabularyEntry.NormalizeText(entry.Kind == VocabularyKind.Word ? entry.Meaning : entry.Translation);
        KindLabel = Loc.T(entry.Kind == VocabularyKind.Word ? "Vocab_KindWord" : "Vocab_KindSentence");
        AddedLabel = entry.AddedAt.ToLocalTime().ToString("d", CultureInfo.CurrentCulture);
    }

    public VocabularyEntry Entry { get; }
    public string Text { get; }
    public string Preview { get; }
    public string KindLabel { get; }
    public string AddedLabel { get; }

    public override string ToString() => Text;
}

/// <summary>
/// The vocabulary book: search, delete, clear and export to Anki. Follows the store, so a star clicked on a card shows up
/// here at once. Opened from the tray menu or with <c>--vocabulary</c>.
/// </summary>
public partial class VocabularyWindow : Window
{
    private readonly VocabularyStore _store;
    private readonly DiagnosticsLog? _log;
    private readonly ObservableCollection<VocabularyRow> _rows = [];
    private bool _exporting;

    public VocabularyWindow(VocabularyStore store, DiagnosticsLog? log = null)
    {
        InitializeComponent();
        _store = store;
        _log = log;
        EntryList.ItemsSource = _rows;
        EntryList.SelectionChanged += (_, _) => UpdateButtons();
        // Why the book cannot be changed, and whether a damaged file was set aside when it loaded: an empty book
        // must not look like lost words without saying where the old file is.
        if (_store.ReadOnlyReason == VocabularyReadOnlyReason.Unreadable)
            Loc.Bind(ReadOnlyNotice, System.Windows.Controls.TextBlock.TextProperty, "Vocab_UnreadableNotice");
        if (_store.SetAsideFileName is { } setAside)
        {
            SetAsideNotice.Text = Loc.T("Vocab_SetAsideNotice", setAside);
            SetAsideNotice.Visibility = Visibility.Visible;
        }
        Refresh();
        Loaded += (_, _) => _store.Changed += OnStoreChanged;
        Closed += (_, _) => _store.Changed -= OnStoreChanged;
    }

    private void OnStoreChanged()
    {
        if (Dispatcher.CheckAccess()) Refresh();
        else Dispatcher.BeginInvoke(Refresh);
    }

    private void Refresh()
    {
        var selected = EntryList.SelectedItems.OfType<VocabularyRow>().Select(r => r.Entry.Id).ToHashSet(StringComparer.Ordinal);
        var all = _store.Entries;
        var query = SearchBox.Text;
        var shown = all.Where(e => e.Matches(query)).ToList();

        _rows.Clear();
        foreach (var entry in shown) _rows.Add(new VocabularyRow(entry));
        foreach (var row in _rows.Where(r => selected.Contains(r.Entry.Id))) EntryList.SelectedItems.Add(row);

        CountText.Text = string.IsNullOrWhiteSpace(query)
            ? Loc.T("Vocab_Count", all.Count)
            : Loc.T("Vocab_CountFiltered", shown.Count, all.Count);
        EmptyText.Visibility = all.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ReadOnlyNotice.Visibility = _store.IsReadOnly ? Visibility.Visible : Visibility.Collapsed;
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        var readOnly = _store.IsReadOnly;
        DeleteButton.IsEnabled = !readOnly && EntryList.SelectedItems.Count > 0;
        ClearButton.IsEnabled = !readOnly && _store.Count > 0;
        ExportButton.IsEnabled = !_exporting && _store.Count > 0;
    }

    private void OnSearchChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) => Refresh();

    private void OnListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Delete) return;
        DeleteSelected();
        e.Handled = true;
    }

    private void OnDelete(object sender, RoutedEventArgs e) => DeleteSelected();

    private void DeleteSelected()
    {
        if (_store.IsReadOnly) return;
        var ids = EntryList.SelectedItems.OfType<VocabularyRow>().Select(r => r.Entry.Id).ToList();
        if (ids.Count == 0) return;
        StatusText.Text = string.Empty;
        if (!_store.RemoveMany(ids)) StatusText.Text = Loc.T("Vocab_SaveFailed");
    }

    private void OnClear(object sender, RoutedEventArgs e)
    {
        if (_store.IsReadOnly || _store.Count == 0) return;
        var answer = MessageBox.Show(this, Loc.T("Vocab_ClearConfirm", _store.Count), Loc.T("Vocab_Title"),
            MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes) return;
        StatusText.Text = string.Empty;
        if (!_store.Clear()) StatusText.Text = Loc.T("Vocab_SaveFailed");
    }

    private async void OnExport(object sender, RoutedEventArgs e)
    {
        if (_exporting) return;
        try
        {
            var entries = _store.Entries; // the whole book, newest first, taken here on the UI thread
            if (entries.Count == 0) return;
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = Loc.T("Vocab_ExportTitle"),
                Filter = "Text (*.txt)|*.txt",
                DefaultExt = ".txt",
                AddExtension = true,
                OverwritePrompt = true,
                FileName = "BiliIndexTranslate-anki-" + DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + ".txt",
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            };
            if (dialog.ShowDialog(this) != true) return;

            _exporting = true;
            UpdateButtons();
            var target = dialog.FileName;
            await Task.Run(() => AnkiExport.WriteFile(target, entries));
            _log?.Info("vocab", $"exported {entries.Count} entries to Anki text");
            StatusText.Text = Loc.T("Vocab_ExportSaved", entries.Count, Path.GetFileName(target));
        }
        catch (Exception ex)
        {
            _log?.Warn("vocab", $"export failed ({ex.GetType().Name})");
            StatusText.Text = Loc.T("Vocab_ExportFailed", ex.Message);
        }
        finally
        {
            _exporting = false;
            UpdateButtons();
        }
    }
}
