using System.Text.Json;

namespace SqlDrop.Core;

public sealed class AppConfig
{
    public string ConnectionString { get; set; } = "";
    public string UserId { get; set; } = "";
    public bool AlwaysOnTop { get; set; }

    /// <summary>How long (seconds) the other instance may stay silent before it is considered gone. Raise on unstable links.</summary>
    public int PeerTimeoutSeconds { get; set; } = 5;

    /// <summary>Timeout (seconds) of every SQL command, including chunk uploads/downloads. Raise on slow links.</summary>
    public int CommandTimeoutSeconds { get; set; } = 60;
    public int? WindowX { get; set; }
    public int? WindowY { get; set; }

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(ConnectionString) && !string.IsNullOrWhiteSpace(UserId);
}

/// <summary>Plain-text JSON config stored next to the executable.</summary>
public sealed class ConfigStore(string path)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public string Path { get; } = path;

    public AppConfig Load()
    {
        try
        {
            if (File.Exists(Path))
                return JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(Path)) ?? new AppConfig();
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            // Unreadable (corrupted, or being written by another instance): use defaults, but never overwrite the file.
            return new AppConfig();
        }

        var config = new AppConfig();
        Save(config);
        return config;
    }

    public void Save(AppConfig config) =>
        File.WriteAllText(Path, JsonSerializer.Serialize(config, Json));
}
