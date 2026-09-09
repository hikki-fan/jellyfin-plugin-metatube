using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.MetaTube.Helpers;

public sealed class BadgeStateDocument
{
    [JsonPropertyName("version")]
    public int Version { get; set; } = 1;

    [JsonPropertyName("items")]
    public Dictionary<string, ItemBadgeState> Items { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
}

public sealed class BadgeStateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly string _filePath;
    private readonly object _lock = new();
    private BadgeStateDocument _document;
    private bool _isDirty;

    public BadgeStateStore(string filePath = null)
    {
        _filePath = filePath ?? DefaultStateFilePath;
        Load();
    }

    public Exception LastLoadError { get; private set; }

    public string LastRecoveryBackupPath { get; private set; }

    public static string DefaultStateFilePath
    {
        get => GetDefaultStateFilePath("subtitle-badge-state.json");
    }

    public static string DefaultThumbStateFilePath
    {
        // v1 could mark an existing local thumbnail as Applied without proving that
        // Emby/Jellyfin had downloaded the requested remote badge image. Keep the
        // old file for recovery/auditing and rebuild thumbnail state through the
        // explicit localization path introduced in v2.
        get => GetDefaultStateFilePath("subtitle-thumb-badge-state-v2.json");
    }

    public ItemBadgeState GetState(string itemId)
    {
        if (string.IsNullOrWhiteSpace(itemId))
            return null;

        lock (_lock)
        {
            return _document.Items.TryGetValue(itemId, out var state) ? state : null;
        }
    }

    public void SetState(string itemId, ItemBadgeState state)
    {
        if (string.IsNullOrWhiteSpace(itemId) || state == null)
            return;

        lock (_lock)
        {
            _document.Items[itemId] = state;
            _isDirty = true;
        }
    }

    public void RemoveState(string itemId)
    {
        if (string.IsNullOrWhiteSpace(itemId))
            return;

        lock (_lock)
        {
            if (_document.Items.Remove(itemId))
                _isDirty = true;
        }
    }

    public void Prune(ISet<string> validItemIds)
    {
        if (validItemIds == null)
            return;

        lock (_lock)
        {
            foreach (var itemId in _document.Items.Keys
                         .Where(itemId => !validItemIds.Contains(itemId)).ToList())
            {
                _document.Items.Remove(itemId);
                _isDirty = true;
            }
        }
    }

    public void Save(bool force = false)
    {
        lock (_lock)
        {
            if (!_isDirty && !force)
                return;

            var directory = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            var temporaryPath = $"{_filePath}.{Guid.NewGuid():N}.tmp";
            try
            {
                File.WriteAllText(temporaryPath, JsonSerializer.Serialize(_document, JsonOptions));

                // Never silently overwrite unreadable/corrupt state. Keep the exact input as a
                // recovery copy, then replace the active state with the newly rebuilt document.
                if (LastLoadError != null && File.Exists(_filePath))
                {
                    LastRecoveryBackupPath =
                        $"{_filePath}.corrupt-{DateTime.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}";
                    File.Copy(_filePath, LastRecoveryBackupPath, overwrite: false);
                }

                File.Move(temporaryPath, _filePath, overwrite: true);
                _isDirty = false;
                LastLoadError = null;
            }
            finally
            {
                if (File.Exists(temporaryPath))
                    File.Delete(temporaryPath);
            }
        }
    }

    private void Load()
    {
        lock (_lock)
        {
            LastLoadError = null;
            try
            {
                if (!File.Exists(_filePath))
                {
                    _document = new BadgeStateDocument();
                }
                else
                {
                    var loaded = JsonSerializer.Deserialize<BadgeStateDocument>(
                        File.ReadAllText(_filePath), JsonOptions);
                    if (loaded == null || loaded.Version != 1)
                        throw new InvalidDataException("Unsupported subtitle badge state format.");

                    loaded.Items = new Dictionary<string, ItemBadgeState>(
                        loaded.Items ?? new Dictionary<string, ItemBadgeState>(),
                        StringComparer.OrdinalIgnoreCase);
                    _document = loaded;
                }
            }
            catch (Exception error)
            {
                LastLoadError = error;
                _document = new BadgeStateDocument();
            }

            _isDirty = false;
        }
    }

    private static string GetDefaultStateFilePath(string fileName)
    {
        var dataFolder = Plugin.Instance?.DataFolderPath;
        if (string.IsNullOrWhiteSpace(dataFolder))
            dataFolder = Path.Combine(AppContext.BaseDirectory, "data");

        return Path.Combine(dataFolder, fileName);
    }
}
