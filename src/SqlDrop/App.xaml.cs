using Microsoft.UI.Xaml;
using SqlDrop.Core;

namespace SqlDrop;

public partial class App : Application
{
    private MainWindow? _window;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var store = new ConfigStore(Path.Combine(AppContext.BaseDirectory, "config.json"));
        var error = CheckWritable(AppContext.BaseDirectory);
        var config = error is null ? store.Load() : new AppConfig();

        _window = new MainWindow(store, config, error);
        _window.Activate();
    }

    /// <summary>Everything (config, logs) lives next to the exe, so the folder must be writable (NFR-8).</summary>
    private static string? CheckWritable(string folder)
    {
        try
        {
            var probe = Path.Combine(folder, ".write-test-" + Guid.NewGuid().ToString("N"));
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "The app folder is not writable. Move SqlDrop to a writable folder.";
        }
    }
}
