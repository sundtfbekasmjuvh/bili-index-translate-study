using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using BiliIndexTranslate.Core.Diagnostics;

namespace BiliIndexTranslate.Core.Vocabulary;

public enum VocabularyToggleResult
{
    Added,
    Removed,

    /// <summary>The file was written by a newer version (or could not be read); nothing is changed.</summary>
    ReadOnly,

    /// <summary>The file could not be written; the change was undone.</summary>
    SaveFailed,
}

/// <summary>
/// The vocabulary book: a plain JSON file <c>{ "schemaVersion": 1, "entries": [ ... ] }</c>, saved after every change
/// (temp file in the same folder, flushed, then replaced). Not encrypted and never sent anywhere. A malformed file is
/// moved to <c>*.bad-&lt;time&gt;</c> and the book starts empty. A file with a newer schema version is loaded as far as it
/// can be read but is read-only: writing it back would drop the fields a newer version added. Logs carry counts only.
/// </summary>
/// <summary>Why the store refuses changes: a newer schema, or a file it could not read or set aside.</summary>
public enum VocabularyReadOnlyReason
{
    None,
    NewerVersion,
    Unreadable,
}

public sealed class VocabularyStore
{
    public const int CurrentSchemaVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly object _gate = new();
    private readonly DiagnosticsLog? _log;
    private readonly List<VocabularyEntry> _entries = []; // newest first
    private bool _readOnly;
    private VocabularyReadOnlyReason _readOnlyReason;
    private string? _setAsideFileName;

    public VocabularyStore(string filePath, DiagnosticsLog? log)
    {
        FilePath = filePath;
        _log = log;
        Load();
    }

    public string FilePath { get; }

    /// <summary>Raised (on the thread that changed the book, outside the lock) after every change that was saved.</summary>
    public event Action? Changed;

    /// <summary>True when the file came from a newer version (or could not be read or set aside): every change is refused and nothing is written.</summary>
    /// <summary>Why <see cref="IsReadOnly"/> is true; <see cref="VocabularyReadOnlyReason.None"/> when it is not.</summary>
    public VocabularyReadOnlyReason ReadOnlyReason
    {
        get { lock (_gate) return _readOnlyReason; }
    }

    /// <summary>File name a malformed vocabulary file was moved to when this store loaded, or null.</summary>
    public string? SetAsideFileName
    {
        get { lock (_gate) return _setAsideFileName; }
    }

    public bool IsReadOnly
    {
        get { lock (_gate) return _readOnly; }
    }

    public int Count
    {
        get { lock (_gate) return _entries.Count; }
    }

    /// <summary>A copy of the entries, newest first.</summary>
    public IReadOnlyList<VocabularyEntry> Entries
    {
        get { lock (_gate) return _entries.ToArray(); }
    }

    public VocabularyEntry? Find(VocabularyKind kind, string? text, string? targetLanguage)
    {
        var key = VocabularyEntry.MakeIdentityKey(kind, text, targetLanguage);
        lock (_gate) return FindByKey(key);
    }

    public bool Contains(VocabularyKind kind, string? text, string? targetLanguage) => Find(kind, text, targetLanguage) is not null;

    /// <summary>
    /// Adds the entry. When an entry with the same identity exists it is returned and nothing changes. Returns null when the
    /// book is read-only or could not be saved (the entry is then not added).
    /// </summary>
    public VocabularyEntry? Add(VocabularyEntry entry)
    {
        VocabularyEntry? stored;
        lock (_gate)
        {
            if (_readOnly) return null;
            if (string.IsNullOrWhiteSpace(entry.Text)) return null;
            if (FindByKey(entry.IdentityKey) is { } existing) return existing;
            _entries.Insert(0, entry);
            if (!SaveLocked()) { _entries.Remove(entry); return null; }
            stored = entry;
        }
        Changed?.Invoke();
        return stored;
    }

    /// <summary>Removes the entry with this id. False when it does not exist, the book is read-only or the save failed.</summary>
    public bool Remove(string id) => RemoveMany([id]);

    /// <summary>Removes the entries with these ids in one save. False when none existed, the book is read-only or the save failed.</summary>
    public bool RemoveMany(IEnumerable<string> ids)
    {
        var set = new HashSet<string>(ids, StringComparer.Ordinal);
        lock (_gate)
        {
            if (_readOnly || set.Count == 0) return false;
            var before = _entries.ToArray();
            if (_entries.RemoveAll(e => set.Contains(e.Id)) == 0) return false;
            if (!SaveLocked()) { Restore(before); return false; }
        }
        Changed?.Invoke();
        return true;
    }

    /// <summary>Removes everything. False when the book is empty, read-only or could not be saved.</summary>
    public bool Clear()
    {
        lock (_gate)
        {
            if (_readOnly || _entries.Count == 0) return false;
            var before = _entries.ToArray();
            _entries.Clear();
            if (!SaveLocked()) { Restore(before); return false; }
        }
        Changed?.Invoke();
        return true;
    }

    /// <summary>The star: removes the entry when one with the same identity is saved, adds <paramref name="candidate"/> otherwise.</summary>
    public VocabularyToggleResult Toggle(VocabularyEntry candidate)
    {
        bool removed;
        lock (_gate)
        {
            if (_readOnly) return VocabularyToggleResult.ReadOnly;
            var existing = FindByKey(candidate.IdentityKey);
            removed = existing is not null;
            if (existing is not null)
            {
                var index = _entries.IndexOf(existing);
                _entries.RemoveAt(index);
                if (!SaveLocked()) { _entries.Insert(index, existing); return VocabularyToggleResult.SaveFailed; }
            }
            else
            {
                if (string.IsNullOrWhiteSpace(candidate.Text)) return VocabularyToggleResult.SaveFailed;
                _entries.Insert(0, candidate);
                if (!SaveLocked()) { _entries.Remove(candidate); return VocabularyToggleResult.SaveFailed; }
            }
        }
        Changed?.Invoke();
        return removed ? VocabularyToggleResult.Removed : VocabularyToggleResult.Added;
    }

    // ---- internals ----------------------------------------------------------------------------

    private VocabularyEntry? FindByKey(string key)
        => _entries.FirstOrDefault(e => string.Equals(e.IdentityKey, key, StringComparison.OrdinalIgnoreCase));

    private void Restore(VocabularyEntry[] snapshot)
    {
        _entries.Clear();
        _entries.AddRange(snapshot);
    }

    private void Load()
    {
        lock (_gate)
        {
            _entries.Clear();
            _readOnly = false;
            _readOnlyReason = VocabularyReadOnlyReason.None;
            _setAsideFileName = null;
            if (!File.Exists(FilePath)) return;

            byte[] bytes;
            try { bytes = File.ReadAllBytes(FilePath); }
            catch (Exception ex)
            {
                // Not being able to read it says nothing about its content: never overwrite it.
                _readOnly = true;
                _readOnlyReason = VocabularyReadOnlyReason.Unreadable;
                _log?.Warn("vocab", $"vocabulary file could not be read ({ex.GetType().Name}); it is left alone and the vocabulary is read-only");
                return;
            }

            var parsed = TryParse(bytes, out var entries, out var schemaVersion);
            if (!parsed)
            {
                SetAsideMalformed();
                return;
            }

            if (schemaVersion > CurrentSchemaVersion)
            {
                _readOnly = true;
                _readOnlyReason = VocabularyReadOnlyReason.NewerVersion;
                _log?.Warn("vocab", $"vocabulary file has schema version {schemaVersion}, newer than {CurrentSchemaVersion}; read-only ({entries.Count} entries read)");
            }
            else
            {
                _log?.Info("vocab", $"vocabulary loaded: {entries.Count} entries");
            }

            var seenIds = new HashSet<string>(StringComparer.Ordinal);
            var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var dropped = 0;
            foreach (var raw in entries.OrderByDescending(e => e.AddedAt))
            {
                var e = Sanitize(raw);
                if (string.IsNullOrWhiteSpace(e.Text) || !seenIds.Add(e.Id) || !seenKeys.Add(e.IdentityKey)) { dropped++; continue; }
                _entries.Add(e);
            }
            if (dropped > 0) _log?.Warn("vocab", $"{dropped} unusable or duplicate entries left out");
        }
    }

    /// <summary>
    /// Reads the file. False when it is malformed. For the current schema every entry must be readable; for a newer one,
    /// entries that cannot be read are skipped (the file is never written back).
    /// </summary>
    private static bool TryParse(byte[] bytes, out List<VocabularyEntry> entries, out int schemaVersion)
    {
        entries = [];
        schemaVersion = 1;
        JsonObject root;
        try
        {
            ReadOnlySpan<byte> utf8 = bytes;
            ReadOnlySpan<byte> bom = [0xEF, 0xBB, 0xBF];
            if (utf8.StartsWith(bom)) utf8 = utf8[3..];
            root = JsonNode.Parse(utf8, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject
                   ?? throw new JsonException("root is not an object");
            if (root["schemaVersion"] is { } v) schemaVersion = v.GetValue<int>();
            if (root["entries"] is { } list && list is not JsonArray) throw new JsonException("entries is not an array");
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            return false;
        }

        var lenient = schemaVersion > CurrentSchemaVersion;
        if (root["entries"] is not JsonArray array) return true;
        foreach (var node in array)
        {
            try
            {
                var entry = node.Deserialize<VocabularyEntry>(JsonOptions) ?? throw new JsonException("null entry");
                entries.Add(entry);
            }
            catch (JsonException)
            {
                if (!lenient) return false;
            }
        }
        return true;
    }

    private static VocabularyEntry Sanitize(VocabularyEntry e) => e with
    {
        Id = string.IsNullOrWhiteSpace(e.Id) ? Guid.NewGuid().ToString("N") : e.Id,
        Text = e.Text ?? string.Empty,
        Translation = e.Translation ?? string.Empty,
        Reading = e.Reading ?? string.Empty,
        PartOfSpeech = e.PartOfSpeech ?? string.Empty,
        Register = e.Register ?? string.Empty,
        Meaning = e.Meaning ?? string.Empty,
        Nuance = e.Nuance ?? string.Empty,
        MemoryAid = e.MemoryAid ?? string.Empty,
        Collocations = (e.Collocations ?? []).Where(c => c is not null).ToArray(),
        ExampleSource = e.ExampleSource ?? string.Empty,
        ExampleTarget = e.ExampleTarget ?? string.Empty,
        SourceLanguage = e.SourceLanguage ?? string.Empty,
        TargetLanguage = e.TargetLanguage ?? string.Empty,
    };

    /// <summary>Moves a file that cannot be parsed to <c>vocabulary.json.bad-yyyyMMddHHmmss</c> (a number is added when that name exists). Must hold the lock.</summary>
    private void SetAsideMalformed()
    {
        try
        {
            var stamp = DateTime.Now.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
            var target = FilePath + ".bad-" + stamp;
            for (var i = 1; File.Exists(target); i++) target = FilePath + ".bad-" + stamp + "-" + i.ToString(CultureInfo.InvariantCulture);
            File.Move(FilePath, target);
            _setAsideFileName = Path.GetFileName(target);
            _log?.Warn("vocab", "vocabulary file is malformed; it was moved aside and the vocabulary starts empty");
        }
        catch (Exception ex)
        {
            _readOnly = true; // the original could not be kept, so it must not be replaced
            _readOnlyReason = VocabularyReadOnlyReason.Unreadable;
            _log?.Warn("vocab", $"vocabulary file is malformed and could not be moved aside ({ex.GetType().Name}); the vocabulary is read-only");
        }
    }

    /// <summary>Writes the file atomically. False (and a log line) when it fails; the previous file is then intact. Must hold the lock.</summary>
    private bool SaveLocked()
    {
        var tmp = FilePath + ".tmp";
        try
        {
            var dir = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(new FileModel { SchemaVersion = CurrentSchemaVersion, Entries = _entries }, JsonOptions);
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                var bytes = new UTF8Encoding(false).GetBytes(json);
                fs.Write(bytes, 0, bytes.Length);
                fs.Flush(flushToDisk: true);
            }
            if (File.Exists(FilePath)) File.Replace(tmp, FilePath, null);
            else File.Move(tmp, FilePath);
            _log?.Info("vocab", $"vocabulary saved: {_entries.Count} entries");
            return true;
        }
        catch (Exception ex)
        {
            try { File.Delete(tmp); } catch (Exception) { /* best effort */ }
            _log?.Warn("vocab", $"vocabulary could not be saved ({ex.GetType().Name}); the file is unchanged");
            return false;
        }
    }

    private sealed class FileModel
    {
        public int SchemaVersion { get; init; }
        public IReadOnlyList<VocabularyEntry> Entries { get; init; } = [];
    }
}
