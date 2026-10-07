using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace SqlDrop.Core;

/// <summary>
/// One running app instance on a channel: heartbeat, peer status, transfer tracking, send/receive.
/// Events are raised from background threads.
/// </summary>
public sealed class SqlDropSession : IAsyncDisposable
{
    private readonly SqlDropDb _db;
    private readonly ChannelCrypto _crypto;
    private readonly Guid _instanceId;
    private readonly SessionOptions _options;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<Guid, (string Name, long Size)> _metaCache = [];

    private Task? _loop;
    private int _disposed;
    private bool _schemaReady;
    private DateTime _lastCleanup = DateTime.MinValue;
    private Guid? _trackedId;
    private TransferView? _transfer;

    public ConnectionState Connection { get; private set; } = ConnectionState.DbError;

    /// <summary>Message of the last DB error while <see cref="Connection"/> is <see cref="ConnectionState.DbError"/>.</summary>
    public string? LastError { get; private set; }
    public TransferView? Transfer => _transfer;

    public event Action<ConnectionState>? ConnectionChanged;
    public event Action<TransferView?>? TransferChanged;
    public event Action<TransferOutcome, string>? TransferFinished;

    public SqlDropSession(string connectionString, string userId, Guid instanceId, SessionOptions? options = null)
    {
        _db = new SqlDropDb(connectionString);
        _crypto = new ChannelCrypto(userId);
        _instanceId = instanceId;
        _options = options ?? new SessionOptions();
    }

    /// <summary>Starts the heartbeat loop. DB errors (including schema creation) are retried and reported via <see cref="Connection"/>.</summary>
    public void Start() => _loop = Task.Run(RunLoopAsync);

    /// <summary>Creates the schema (throws on failure) and then starts the loop.</summary>
    public async Task StartAsync(CancellationToken ct = default)
    {
        await _db.EnsureSchemaAsync(ct);
        _schemaReady = true;
        Start();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return;

        _cts.Cancel();
        if (_loop is not null)
        {
            try { await _loop; } catch (OperationCanceledException) { }
        }
        try { await _db.RemovePeerAsync(_crypto.ChannelId, _instanceId); } catch { /* best effort */ }
        _cts.Dispose();
    }

    // ---- background loop ----

    private async Task RunLoopAsync()
    {
        var ct = _cts.Token;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await TickAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                SetConnection(ConnectionState.DbError);
            }

            try { await Task.Delay(_options.HeartbeatInterval, ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        if (!_schemaReady)
        {
            await _db.EnsureSchemaAsync(ct);
            _schemaReady = true;
        }

        var (registered, liveOthers) = await _db.HeartbeatAsync(
            _crypto.ChannelId, _instanceId, (int)_options.PeerTimeout.TotalMilliseconds, ct);
        var state = !registered ? ConnectionState.ChannelBusy
            : liveOthers > 0 ? ConnectionState.Connected
            : ConnectionState.WaitingForPeer;
        SetConnection(state);

        if (registered && DateTime.UtcNow - _lastCleanup >= _options.CleanupInterval)
        {
            await _db.CleanupAsync(
                _crypto.ChannelId, _options.ActiveTransferTtl, _options.FinishedTransferTtl, TimeSpan.FromMinutes(1), ct);
            _lastCleanup = DateTime.UtcNow;
        }

        var row = await _db.GetLatestAsync(_crypto.ChannelId, ct);

        // The transfer we were tracking may have been superseded or deleted.
        if (_trackedId is { } tracked && row?.TransferId != tracked)
            row = await _db.GetByIdAsync(tracked, ct);

        // Peer gone while a transfer is active -> fail it (FR-23).
        if (row is { IsActive: true } && state == ConnectionState.WaitingForPeer)
        {
            await FailAsync(row.TransferId);
            row = await _db.GetByIdAsync(row.TransferId, ct);
        }

        UpdateTransfer(row);
    }

    private void UpdateTransfer(TransferRow? row)
    {
        if (row is { IsActive: true })
        {
            _trackedId = row.TransferId;
            Publish(ToView(row));
            return;
        }

        Publish(null);

        if (_trackedId is null)
            return;

        // Tracked transfer finished: deleted row == delivered, otherwise report its terminal status.
        var outcome = row?.StatusValue switch
        {
            null => TransferOutcome.Completed,
            TransferStatus.Rejected => TransferOutcome.Rejected,
            TransferStatus.Cancelled => TransferOutcome.Cancelled,
            _ => TransferOutcome.Failed,
        };
        _metaCache.TryRemove(_trackedId.Value, out var meta);
        var name = meta.Name ?? "";
        _trackedId = null;
        TransferFinished?.Invoke(outcome, name);
    }

    private TransferView ToView(TransferRow row)
    {
        if (!_metaCache.TryGetValue(row.TransferId, out var meta))
        {
            try { meta = _crypto.DecryptMeta(row.MetaEnc); }
            catch (CryptographicException) { meta = ("(unreadable)", 0); }
            _metaCache[row.TransferId] = meta;
        }

        var role = row.SenderInstanceId == _instanceId ? TransferRole.Sending : TransferRole.Receiving;
        return new TransferView(row.TransferId, role, row.StatusValue, meta.Name, meta.Size,
            row.ChunkCount, row.ChunksReady, row.ChunksReceived);
    }

    private void Publish(TransferView? view)
    {
        if (Equals(view, _transfer))
            return;
        _transfer = view;
        TransferChanged?.Invoke(view);
    }

    private void SetConnection(ConnectionState state)
    {
        if (state == Connection)
            return;
        Connection = state;
        ConnectionChanged?.Invoke(state);
    }

    private async Task FailAsync(Guid id)
    {
        await _db.SetStatusAsync(id, TransferStatus.Failed, CancellationToken.None,
            TransferStatus.Uploading, TransferStatus.Ready, TransferStatus.Downloading);
        await _db.DeleteChunksAsync(id);
    }

    // ---- send ----

    /// <summary>Encrypts and uploads a file. Returns normally also when the transfer was cancelled/rejected remotely.</summary>
    public async Task SendAsync(string path, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        if (Connection != ConnectionState.Connected)
            throw new InvalidOperationException("The other device is not connected.");

        var info = new FileInfo(path);
        if (!info.Exists)
            throw new FileNotFoundException("File not found.", path);
        if (info.Length > _options.MaxFileSize)
            throw new InvalidOperationException(
                $"File is too large (limit {_options.MaxFileSize / (1024 * 1024)} MB).");

        var chunkSize = _options.ChunkSize;
        var chunkCount = (int)((info.Length + chunkSize - 1) / chunkSize);
        var id = Guid.NewGuid();
        var meta = _crypto.EncryptMeta(info.Name, info.Length);

        if (!await _db.TryCreateTransferAsync(id, _crypto.ChannelId, _instanceId, meta, chunkCount, ct))
            throw new InvalidOperationException("Another transfer is already in progress.");

        _metaCache[id] = (info.Name, info.Length);
        _trackedId = id;

        try
        {
            await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
                bufferSize: 81920, useAsync: true);
            var buffer = new byte[chunkSize];
            for (var i = 0; i < chunkCount; i++)
            {
                var read = await file.ReadAtLeastAsync(buffer.AsMemory(), buffer.Length, throwOnEndOfStream: false, ct);
                var encrypted = _crypto.Encrypt(buffer.AsSpan(0, read));
                if (!await _db.AppendChunkAsync(id, i, encrypted, ct))
                    return; // cancelled, rejected or failed by the other side
                progress?.Report((i + 1) / (double)chunkCount);
            }

            await _db.SetStatusAsync(id, TransferStatus.Ready, ct, TransferStatus.Uploading);
        }
        catch (OperationCanceledException)
        {
            await _db.SetStatusAsync(id, TransferStatus.Cancelled, CancellationToken.None,
                TransferStatus.Uploading, TransferStatus.Ready);
            await _db.DeleteChunksAsync(id);
            throw;
        }
        catch
        {
            await FailAsync(id);
            throw;
        }
    }

    // ---- receive ----

    /// <summary>Downloads the pending incoming transfer into <paramref name="folder"/>. Returns the saved path, or null if aborted remotely.</summary>
    public async Task<string?> AcceptAsync(string folder, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var view = _transfer;
        if (view is not { Role: TransferRole.Receiving, Status: TransferStatus.Ready })
            throw new InvalidOperationException("There is no incoming file to accept.");

        var row = await _db.GetByIdAsync(view.Id, ct);
        if (row is null)
            return null;

        if (!await _db.SetStatusAsync(view.Id, TransferStatus.Downloading, ct, TransferStatus.Ready))
            return null;

        _trackedId = view.Id;
        var (finalPath, partPath) = ReservePath(folder, view.FileName);

        try
        {
            await using (var output = new FileStream(partPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                bufferSize: 81920, useAsync: true))
            {
                for (var i = 0; i < row.ChunkCount; i++)
                {
                    var encrypted = await _db.GetChunkAsync(view.Id, i, ct);
                    if (encrypted is null)
                    {
                        output.Close();
                        File.Delete(partPath);
                        return null;
                    }

                    var plain = _crypto.Decrypt(encrypted);
                    await output.WriteAsync(plain, ct);

                    if (!await _db.ReportReceivedAsync(view.Id, i + 1, ct))
                    {
                        output.Close();
                        File.Delete(partPath);
                        return null;
                    }
                    progress?.Report((i + 1) / (double)row.ChunkCount);
                }

                if (output.Length != view.FileSize)
                    throw new InvalidDataException("Received file size does not match.");
            }

            File.Move(partPath, finalPath);
        }
        catch (OperationCanceledException)
        {
            TryDelete(partPath);
            await _db.SetStatusAsync(view.Id, TransferStatus.Cancelled, CancellationToken.None, TransferStatus.Downloading);
            await _db.DeleteChunksAsync(view.Id);
            throw;
        }
        catch
        {
            TryDelete(partPath);
            await FailAsync(view.Id);
            throw;
        }

        // Delivered: remove the transfer; the loop reports it as Completed on the sender side.
        await _db.DeleteTransferAsync(view.Id);
        _trackedId = null;
        _metaCache.TryRemove(view.Id, out _);
        Publish(null);
        TransferFinished?.Invoke(TransferOutcome.Completed, Path.GetFileName(finalPath));
        return finalPath;
    }

    public async Task RejectAsync()
    {
        if (_transfer is not { Role: TransferRole.Receiving } view)
            return;
        if (await _db.SetStatusAsync(view.Id, TransferStatus.Rejected, CancellationToken.None, TransferStatus.Ready))
            await _db.DeleteChunksAsync(view.Id);
    }

    /// <summary>Cancels the current transfer from either side.</summary>
    public async Task CancelAsync()
    {
        if (_transfer is not { } view)
            return;
        if (await _db.SetStatusAsync(view.Id, TransferStatus.Cancelled, CancellationToken.None,
                TransferStatus.Uploading, TransferStatus.Ready, TransferStatus.Downloading))
            await _db.DeleteChunksAsync(view.Id);
    }

    // ---- helpers ----

    private static (string Final, string Part) ReservePath(string folder, string fileName)
    {
        var name = Path.GetFileName(fileName);
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        if (string.IsNullOrWhiteSpace(name))
            name = "file";

        var stem = Path.GetFileNameWithoutExtension(name);
        var ext = Path.GetExtension(name);
        var candidate = name;
        for (var n = 1; File.Exists(Path.Combine(folder, candidate)) || File.Exists(Path.Combine(folder, candidate + ".part")); n++)
            candidate = $"{stem} ({n}){ext}";

        var final = Path.Combine(folder, candidate);
        return (final, final + ".part");
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { }
    }
}
