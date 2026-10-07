using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using SqlDrop.Core;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using WinRT.Interop;

namespace SqlDrop;

public sealed partial class SettingsWindow : Window
{
    private readonly AppConfig _config;
    private readonly ConfigStore _store;
    private readonly MainWindow _main;

    public SettingsWindow(AppConfig config, ConfigStore store, MainWindow main)
    {
        _config = config;
        _store = store;
        _main = main;

        InitializeComponent();
        SystemBackdrop = new Microsoft.UI.Xaml.Media.MicaBackdrop();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        var hwnd = WindowNative.GetWindowHandle(this);
        var scale = GetDpiForWindow(hwnd) / 96.0;
        AppWindow.ResizeClient(new SizeInt32((int)(460 * scale), (int)(420 * scale)));
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
        }

        ConnectionBox.Text = config.ConnectionString;
        UserIdBox.Text = config.UserId;
        TopmostSwitch.IsOn = config.AlwaysOnTop;

        // Show the effective (validated) values, so an invalid number in config.json displays as what will actually be used.
        var options = SessionOptions.FromConfig(config);
        PeerTimeoutBox.Value = options.PeerTimeout.TotalSeconds;
        CommandTimeoutBox.Value = options.CommandTimeout.TotalSeconds;
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    private void Generate_Click(object sender, RoutedEventArgs e) =>
        UserIdBox.Text = ChannelCrypto.GenerateUserId();

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        var package = new DataPackage();
        package.SetText(UserIdBox.Text);
        Clipboard.SetContent(package);
    }

    private async void Test_Click(object sender, RoutedEventArgs e)
    {
        TestButton.IsEnabled = false;
        TestResult.Text = "Connecting…";
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await new SqlDropDb(ConnectionBox.Text.Trim()).EnsureSchemaAsync(cts.Token);
            TestResult.Text = "OK — connected, tables are ready.";
        }
        catch (Exception ex)
        {
            TestResult.Text = ex.Message;
        }
        finally
        {
            TestButton.IsEnabled = true;
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        _config.ConnectionString = ConnectionBox.Text.Trim();
        _config.UserId = UserIdBox.Text.Trim();
        _config.AlwaysOnTop = TopmostSwitch.IsOn;
        _config.PeerTimeoutSeconds = ToSeconds(PeerTimeoutBox.Value, _config.PeerTimeoutSeconds);
        _config.CommandTimeoutSeconds = ToSeconds(CommandTimeoutBox.Value, _config.CommandTimeoutSeconds);
        _store.Save(_config);
        _main.ApplyConfig(_config);
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>NumberBox yields NaN when the field is empty or invalid; keep the previous value then.</summary>
    private static int ToSeconds(double value, int fallback) =>
        double.IsNaN(value) ? fallback : (int)Math.Round(value);
}
