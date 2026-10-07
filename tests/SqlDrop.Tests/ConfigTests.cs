using SqlDrop.Core;

namespace SqlDrop.Tests;

public class ConfigTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sqldrop-cfg-" + Guid.NewGuid().ToString("N"));

    public ConfigTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string ConfigPath => Path.Combine(_dir, "config.json");

    [Fact]
    public void Unreadable_config_is_not_overwritten()
    {
        File.WriteAllText(ConfigPath, "{ this is not json");

        var config = new ConfigStore(ConfigPath).Load();

        Assert.False(config.IsConfigured);
        Assert.Equal("{ this is not json", File.ReadAllText(ConfigPath));
    }
}
