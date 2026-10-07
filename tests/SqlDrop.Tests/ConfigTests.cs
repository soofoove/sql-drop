using SqlDrop.Core;

namespace SqlDrop.Tests;

public class ConfigTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sqldrop-cfg-" + Guid.NewGuid().ToString("N"));

    public ConfigTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string ConfigPath => Path.Combine(_dir, "config.json");

    [Fact]
    public void Defaults_are_used_when_nothing_is_configured()
    {
        var options = SessionOptions.FromConfig(new AppConfig());

        Assert.Equal(TimeSpan.FromSeconds(5), options.PeerTimeout);
        Assert.Equal(TimeSpan.FromSeconds(60), options.CommandTimeout);
    }

    [Fact]
    public void Configured_timeouts_are_applied()
    {
        var options = SessionOptions.FromConfig(new AppConfig { PeerTimeoutSeconds = 20, CommandTimeoutSeconds = 180 });

        Assert.Equal(TimeSpan.FromSeconds(20), options.PeerTimeout);
        Assert.Equal(TimeSpan.FromSeconds(180), options.CommandTimeout);
    }

    [Theory]
    [InlineData(0, 5)]
    [InlineData(-3, 5)]
    [InlineData(1, 2)]
    [InlineData(100000, 300)]
    public void Peer_timeout_is_clamped_to_a_sane_range(int configured, int expectedSeconds)
    {
        var options = SessionOptions.FromConfig(new AppConfig { PeerTimeoutSeconds = configured });

        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), options.PeerTimeout);
    }

    [Theory]
    [InlineData(0, 60)]
    [InlineData(1, 5)]
    [InlineData(100000, 600)]
    public void Command_timeout_is_clamped_to_a_sane_range(int configured, int expectedSeconds)
    {
        var options = SessionOptions.FromConfig(new AppConfig { CommandTimeoutSeconds = configured });

        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), options.CommandTimeout);
    }

    [Fact]
    public void Old_config_without_timeout_fields_loads_with_defaults()
    {
        File.WriteAllText(ConfigPath, """{ "ConnectionString": "Server=x", "UserId": "abc" }""");

        var config = new ConfigStore(ConfigPath).Load();

        Assert.Equal("Server=x", config.ConnectionString);
        Assert.Equal(5, config.PeerTimeoutSeconds);
        Assert.Equal(60, config.CommandTimeoutSeconds);
    }

    [Fact]
    public void Timeouts_survive_a_save_and_load()
    {
        var store = new ConfigStore(ConfigPath);
        store.Save(new AppConfig { UserId = "u", PeerTimeoutSeconds = 15, CommandTimeoutSeconds = 120 });

        var loaded = store.Load();

        Assert.Equal(15, loaded.PeerTimeoutSeconds);
        Assert.Equal(120, loaded.CommandTimeoutSeconds);
    }

    [Fact]
    public void Unreadable_config_is_not_overwritten()
    {
        File.WriteAllText(ConfigPath, "{ this is not json");

        var config = new ConfigStore(ConfigPath).Load();

        Assert.False(config.IsConfigured);
        Assert.Equal("{ this is not json", File.ReadAllText(ConfigPath));
    }
}
