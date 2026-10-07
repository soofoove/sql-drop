using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using SqlDrop.Core;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace SqlDrop;

public sealed partial class MainWindow : Window
{
    private const int ClientSize = 320;

    private static readonly SolidColorBrush Gray = new(Colors.Gray);
    private static readonly SolidColorBrush Red = new(Colors.IndianRed);
    private static readonly SolidColorBrush Yellow = new(Colors.Goldenrod);
    private static readonly SolidColorBrush Green = new(Colors.MediumSeaGreen);

    private readonly ConfigStore _store;
    private readonly string? _startupError;

    // Per process, never persisted: two windows sharing one config.json (or a copied folder) must still be distinct peers.
    private readonly Guid _instanceId = Guid.NewGuid();
    private AppConfig _config;
    private SqlDropSession? _session;
    private SettingsWindow? _settings;

    private bool _busy;
    private double _localProgress;
    private string? _message;
    private int _messageVersion;

    public MainWindow(ConfigStore store, AppConfig config, string? startupError)
    {
        _store = store;
        _config = config;
        _startupError = startupError;

        InitializeComponent();
        SystemBackdrop = new MicaBackdrop();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        SetUpWindow();
        Closed += OnClosed;

        ApplyConfig(_config);
    }

    // ---- window ----

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    private void SetUpWindow()
    {
        var hwnd = WindowNative.GetWindowHandle(this);
        var scale = GetDpiForWindow(hwnd) / 96.0;
        AppWindow.ResizeClient(new SizeInt32((int)(ClientSize * scale), (int)(ClientSize * scale)));

        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
        }

        if (_config is { WindowX: { } x, WindowY: { } y })
        {
            var point = new PointInt32(x, y);
            if (DisplayArea.GetFromPoint(point, DisplayAreaFallback.None) is not null)
                AppWindow.Move(point);
        }
    }

    public void ApplyAlwaysOnTop()
    {
        if (AppWindow.Presenter is OverlappedPresenter presenter)
            presenter.IsAlwaysOnTop = _config.AlwaysOnTop;
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        _config.WindowX = AppWindow.Position.X;
        _config.WindowY = AppWindow.Position.Y;
        try { _store.Save(_config); } catch (IOException) { }

        _settings?.Close();
        var session = _session;
        _session = null;
        if (session is not null)
            Task.Run(() => session.DisposeAsync().AsTask()).Wait(TimeSpan.FromSeconds(3));
    }

    // ---- configuration / session ----

    /// <summary>(Re)creates the session from the given config. Called on startup and after settings are saved.</summary>
    public void ApplyConfig(AppConfig config)
    {
        _config = config;
        ApplyAlwaysOnTop();

        var old = _session;
        _session = null;
        if (old is not null)
            Task.Run(() => old.DisposeAsync().AsTask());

        _busy = false;
        _message = null;

        if (_startupError is null && config.IsConfigured)
        {
            var session = new SqlDropSession(config.ConnectionString, config.UserId, _instanceId);
            session.ConnectionChanged += _ => Dispatch(Render);
            session.TransferChanged += _ => Dispatch(Render);
            session.TransferFinished += (outcome, name) => Dispatch(() => OnTransferFinished(outcome, name));
            _session = session;
            session.Start();
        }

        Render();
    }

    private void Dispatch(Action action) => DispatcherQueue.TryEnqueue(() => action());

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_settings is not null)
        {
            _settings.Activate();
            return;
        }

        _settings = new SettingsWindow(_config, _store, this);
        _settings.Closed += (_, _) => _settings = null;
        _settings.Activate();
    }

    // ---- rendering ----

    private void Render()
    {
        var session = _session;
        var transfer = session?.Transfer;

        RenderStatus(session);

        IdlePanel.Visibility = BusyPanel.Visibility = IncomingPanel.Visibility = Visibility.Collapsed;

        if (transfer is { Role: TransferRole.Receiving, Status: TransferStatus.Ready } && !_busy)
        {
            IncomingPanel.Visibility = Visibility.Visible;
            IncomingName.Text = transfer.FileName;
            IncomingSize.Text = FormatSize(transfer.FileSize);
        }
        else if (transfer is not null || _busy)
        {
            BusyPanel.Visibility = Visibility.Visible;
            RenderBusy(transfer);
        }
        else
        {
            IdlePanel.Visibility = Visibility.Visible;
            RenderIdle(session);
        }
    }

    private void RenderStatus(SqlDropSession? session)
    {
        var (brush, text) = session?.Connection switch
        {
            null => (Gray, _startupError ?? "Not configured"),
            ConnectionState.DbError => (Red, "No DB connection"),
            ConnectionState.WaitingForPeer => (Yellow, "Waiting for peer"),
            ConnectionState.Connected => (Green, "Connected"),
            ConnectionState.ChannelBusy => (Red, "Channel busy"),
            _ => (Gray, ""),
        };
        StatusDot.Fill = brush;
        StatusText.Text = text;
        ToolTipService.SetToolTip(StatusText,
            session?.Connection == ConnectionState.DbError ? session.LastError : _startupError);
    }

    private void RenderIdle(SqlDropSession? session)
    {
        var ready = session?.Connection == ConnectionState.Connected;
        DropZone.Opacity = ready ? 1.0 : 0.6;
        IdleIcon.Glyph = ready ? "" : "";

        IdleText.Text = _message
            ?? (session is null ? "Open settings to configure"
                : ready ? "Drop a file here\nor click to choose"
                : "Waiting for the other device");
    }

    private void RenderBusy(TransferView? transfer)
    {
        DropZone.Opacity = 1.0;
        BusyName.Text = transfer?.FileName ?? "";
        CancelButton.IsEnabled = transfer is not null;

        string text;
        double? progress = null;
        switch (transfer)
        {
            case null:
                text = "Starting…";
                break;
            case { Status: TransferStatus.Uploading, Role: TransferRole.Sending }:
                text = "Uploading…";
                progress = _busy ? _localProgress : Ratio(transfer.ChunksReady, transfer.ChunkCount);
                break;
            case { Status: TransferStatus.Uploading }:
                text = "Incoming file is being uploaded…";
                progress = Ratio(transfer.ChunksReady, transfer.ChunkCount);
                break;
            case { Status: TransferStatus.Ready }:
                text = "Waiting for the other device to accept";
                break;
            default:
                text = transfer.Role == TransferRole.Receiving ? "Receiving…" : "Other device is receiving…";
                progress = _busy ? _localProgress : Ratio(transfer.ChunksReceived, transfer.ChunkCount);
                break;
        }

        BusyText.Text = text;
        BusyProgress.IsIndeterminate = progress is null;
        if (progress is { } p)
            BusyProgress.Value = p;
    }

    private static double Ratio(int done, int total) => total == 0 ? 1 : Math.Min(1.0, done / (double)total);

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        _ => $"{bytes / (1024.0 * 1024.0):0.#} MB",
    };

    private async void ShowMessage(string text)
    {
        var version = ++_messageVersion;
        _message = text;
        Render();
        await Task.Delay(TimeSpan.FromSeconds(4));
        if (version == _messageVersion)
        {
            _message = null;
            Render();
        }
    }

    private void OnTransferFinished(TransferOutcome outcome, string name)
    {
        ShowMessage(outcome switch
        {
            TransferOutcome.Completed => string.IsNullOrEmpty(name) ? "Done" : $"Done: {name}",
            TransferOutcome.Rejected => "The other device rejected the file",
            TransferOutcome.Cancelled => "Transfer cancelled",
            _ => "Transfer failed",
        });
    }

    // ---- send ----

    private void DropZone_DragOver(object sender, DragEventArgs e)
    {
        if (CanSend() && e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.DragUIOverride.Caption = "Send";
        }
    }

    private async void DropZone_Drop(object sender, DragEventArgs e)
    {
        if (!CanSend() || !e.DataView.Contains(StandardDataFormats.StorageItems))
            return;

        var items = await e.DataView.GetStorageItemsAsync();
        if (items.Count == 0)
            return;
        if (items[0] is not StorageFile file)
        {
            ShowMessage("Folders are not supported");
            return;
        }

        if (items.Count > 1)
            ShowMessage("Only one file at a time — sending the first");

        await SendAsync(file.Path);
    }

    private async void DropZone_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (!CanSend())
            return;

        var picker = new FileOpenPicker();
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        picker.FileTypeFilter.Add("*");
        var file = await picker.PickSingleFileAsync();
        if (file is not null)
            await SendAsync(file.Path);
    }

    private bool CanSend() =>
        _session is { Connection: ConnectionState.Connected, Transfer: null } && !_busy;

    private async Task SendAsync(string path)
    {
        var session = _session;
        if (session is null)
            return;

        _busy = true;
        _localProgress = 0;
        Render();
        try
        {
            await session.SendAsync(path, new Progress<double>(p =>
            {
                _localProgress = p;
                Render();
            }));
        }
        catch (Exception ex)
        {
            ShowMessage(ex.Message);
        }
        finally
        {
            _busy = false;
            Render();
        }
    }

    // ---- receive ----

    private async void AcceptButton_Click(object sender, RoutedEventArgs e)
    {
        var session = _session;
        if (session is null || _busy)
            return;

        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.Downloads, SettingsIdentifier = "SqlDropSave" };
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        picker.FileTypeFilter.Add("*");
        var folder = await picker.PickSingleFolderAsync();
        if (folder is null)
            return;

        _busy = true;
        _localProgress = 0;
        Render();
        try
        {
            await session.AcceptAsync(folder.Path, new Progress<double>(p =>
            {
                _localProgress = p;
                Render();
            }));
        }
        catch (Exception ex)
        {
            ShowMessage(ex is System.Security.Cryptography.CryptographicException
                ? "Could not decrypt the file (wrong userId?)"
                : ex.Message);
        }
        finally
        {
            _busy = false;
            Render();
        }
    }

    private async void RejectButton_Click(object sender, RoutedEventArgs e)
    {
        if (_session is { } session)
            await session.RejectAsync();
    }

    private async void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        if (_session is { } session)
            await session.CancelAsync();
    }
}
