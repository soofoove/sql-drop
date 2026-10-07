using System.Text.Json;

namespace SqlDrop.Core;

public sealed class AppConfig
{
    public string ConnectionString { get; set; } = "";
    public string UserId { get; set; } = "";
    public bool AlwaysOnTop { get; set; }
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
            // Corrupted config: start from defaults.
        }

        var config = new AppConfig();
        Save(config);
        return config;
    }

    public void Save(AppConfig config) =>
        File.WriteAllText(Path, JsonSerializer.Serialize(config, Json));
}
