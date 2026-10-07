using System.Security.Cryptography;
using Dapper;
using Microsoft.Data.SqlClient;
using SqlDrop.Core;

namespace SqlDrop.Tests;

/// <summary>Creates the test database on the Docker SQL Server (see tools/start-test-db.ps1).</summary>
public sealed class DbFixture
{
    public string ConnectionString { get; }

    public DbFixture()
    {
        ConnectionString = Environment.GetEnvironmentVariable("SQLDROP_TEST_CONN")
            ?? "Server=127.0.0.1,14333;Database=SqlDropTest;User Id=sa;Password=SqlDrop_Test_123!;TrustServerCertificate=True";

        var master = new SqlConnectionStringBuilder(ConnectionString) { InitialCatalog = "master" };
        using var conn = new SqlConnection(master.ConnectionString);
        conn.Open();
        conn.Execute("IF DB_ID('SqlDropTest') IS NULL CREATE DATABASE SqlDropTest");
    }

    public int Count(string table, string userId)
    {
        var ch = new ChannelCrypto(userId).ChannelId;
        using var conn = new SqlConnection(ConnectionString);
        var sql = table == "chunks"
            ? "SELECT COUNT(*) FROM dbo.SqlDrop_TransferChunks c JOIN dbo.SqlDrop_Transfers t ON t.TransferId = c.TransferId WHERE t.ChannelId = @ch"
            : $"SELECT COUNT(*) FROM dbo.SqlDrop_{table} WHERE ChannelId = @ch";
        return conn.ExecuteScalar<int>(sql, new { ch });
    }
}

public class SessionTests(DbFixture db) : IClassFixture<DbFixture>, IAsyncLifetime
{
    private readonly List<SqlDropSession> _sessions = [];
    private readonly List<string> _tempPaths = [];

    private static SessionOptions FastOptions(Action<SessionOptionsBuilder>? tweak = null)
    {
        var b = new SessionOptionsBuilder();
        tweak?.Invoke(b);
        return new SessionOptions
        {
            HeartbeatInterval = TimeSpan.FromMilliseconds(200),
            PeerTimeout = TimeSpan.FromMilliseconds(1500),
            ChunkSize = 1024 * 1024,
            MaxFileSize = b.MaxFileSize,
            ActiveTransferTtl = b.ActiveTtl,
            CleanupInterval = b.CleanupInterval,
        };
    }

    private sealed class SessionOptionsBuilder
    {
        public long MaxFileSize { get; set; } = 100L * 1024 * 1024;
        public TimeSpan ActiveTtl { get; set; } = TimeSpan.FromHours(24);
        public TimeSpan CleanupInterval { get; set; } = TimeSpan.FromMinutes(1);
    }

    private async Task<SqlDropSession> StartAsync(string userId, SessionOptions? options = null)
    {
        var s = new SqlDropSession(db.ConnectionString, userId, Guid.NewGuid(), options ?? FastOptions());
        _sessions.Add(s);
        await s.StartAsync();
        return s;
    }

    private string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "sqldrop-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        _tempPaths.Add(dir);
        return dir;
    }

    private static async Task WaitFor(Func<bool> condition, string what, int timeoutMs = 10_000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > until)
                throw new TimeoutException("Timed out waiting for: " + what);
            await Task.Delay(50);
        }
    }

    private static async Task<(SqlDropSession A, SqlDropSession B, string UserId)> ConnectedPair(SessionTests t, SessionOptions? options = null)
    {
        var userId = ChannelCrypto.GenerateUserId();
        var a = await t.StartAsync(userId, options);
        var b = await t.StartAsync(userId, options);
        await WaitFor(() => a.Connection == ConnectionState.Connected && b.Connection == ConnectionState.Connected, "both connected");
        return (a, b, userId);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var s in _sessions)
            await s.DisposeAsync();
        foreach (var p in _tempPaths)
            if (Directory.Exists(p)) Directory.Delete(p, recursive: true);
    }

    [Fact]
    public async Task Two_instances_become_connected_and_detect_disconnect()
    {
        var (a, b, _) = await ConnectedPair(this);

        await b.DisposeAsync();

        await WaitFor(() => a.Connection == ConnectionState.WaitingForPeer, "peer lost");
    }

    [Fact]
    public async Task Different_user_ids_do_not_connect()
    {
        var a = await StartAsync(ChannelCrypto.GenerateUserId());
        var b = await StartAsync(ChannelCrypto.GenerateUserId());

        await Task.Delay(1500);

        Assert.Equal(ConnectionState.WaitingForPeer, a.Connection);
        Assert.Equal(ConnectionState.WaitingForPeer, b.Connection);
    }

    [Fact]
    public async Task Third_instance_gets_channel_busy_and_does_not_disturb_the_pair()
    {
        var (a, b, userId) = await ConnectedPair(this);

        var c = await StartAsync(userId);
        await WaitFor(() => c.Connection == ConnectionState.ChannelBusy, "third is busy");

        Assert.Equal(ConnectionState.Connected, a.Connection);
        Assert.Equal(ConnectionState.Connected, b.Connection);
    }

    [Fact]
    public async Task File_is_transferred_intact_and_removed_from_db()
    {
        var (a, b, userId) = await ConnectedPair(this);
        var dir = TempDir();
        var source = Path.Combine(dir, "source file.bin");
        await File.WriteAllBytesAsync(source, RandomNumberGenerator.GetBytes(3 * 1024 * 1024 + 12345));
        var outcome = new TaskCompletionSource<(TransferOutcome, string)>();
        a.TransferFinished += (o, n) => outcome.TrySetResult((o, n));

        await a.SendAsync(source);

        await WaitFor(() => b.Transfer is { Status: TransferStatus.Ready }, "incoming file offered");
        Assert.Equal("source file.bin", b.Transfer!.FileName);
        Assert.Equal(new FileInfo(source).Length, b.Transfer.FileSize);
        Assert.Equal(TransferRole.Receiving, b.Transfer.Role);

        var targetDir = TempDir();
        var saved = await b.AcceptAsync(targetDir);

        Assert.Equal(Path.Combine(targetDir, "source file.bin"), saved);
        Assert.Equal(SHA256.HashData(await File.ReadAllBytesAsync(source)), SHA256.HashData(await File.ReadAllBytesAsync(saved!)));
        Assert.Equal(TransferOutcome.Completed, (await outcome.Task.WaitAsync(TimeSpan.FromSeconds(10))).Item1);
        Assert.Equal(0, db.Count("Transfers", userId));
        Assert.Equal(0, db.Count("chunks", userId));
        Assert.Empty(Directory.GetFiles(targetDir, "*.part"));
    }

    [Fact]
    public async Task Existing_file_name_gets_a_numeric_suffix_and_empty_files_work()
    {
        var (a, b, _) = await ConnectedPair(this);
        var dir = TempDir();
        var source = Path.Combine(dir, "empty.txt");
        await File.WriteAllBytesAsync(source, []);
        var targetDir = TempDir();
        await File.WriteAllTextAsync(Path.Combine(targetDir, "empty.txt"), "existing");

        await a.SendAsync(source);
        await WaitFor(() => b.Transfer is { Status: TransferStatus.Ready }, "incoming file offered");
        var saved = await b.AcceptAsync(targetDir);

        Assert.Equal(Path.Combine(targetDir, "empty (1).txt"), saved);
        Assert.Equal(0, new FileInfo(saved!).Length);
        Assert.Equal("existing", await File.ReadAllTextAsync(Path.Combine(targetDir, "empty.txt")));
    }

    [Fact]
    public async Task Reject_notifies_sender_and_clears_chunks()
    {
        var (a, b, userId) = await ConnectedPair(this);
        var source = Path.Combine(TempDir(), "r.bin");
        await File.WriteAllBytesAsync(source, RandomNumberGenerator.GetBytes(1_500_000));
        var outcome = new TaskCompletionSource<TransferOutcome>();
        a.TransferFinished += (o, _) => outcome.TrySetResult(o);

        await a.SendAsync(source);
        await WaitFor(() => b.Transfer is { Status: TransferStatus.Ready }, "incoming file offered");
        await b.RejectAsync();

        Assert.Equal(TransferOutcome.Rejected, await outcome.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(0, db.Count("chunks", userId));
        await WaitFor(() => a.Transfer is null, "sender is free again");
    }

    [Fact]
    public async Task Sender_can_cancel_a_pending_transfer()
    {
        var (a, b, userId) = await ConnectedPair(this);
        var source = Path.Combine(TempDir(), "c.bin");
        await File.WriteAllBytesAsync(source, RandomNumberGenerator.GetBytes(1_500_000));
        var receiverOutcome = new TaskCompletionSource<TransferOutcome>();
        b.TransferFinished += (o, _) => receiverOutcome.TrySetResult(o);

        await a.SendAsync(source);
        await WaitFor(() => b.Transfer is { Status: TransferStatus.Ready }, "incoming file offered");
        await a.CancelAsync();

        Assert.Equal(TransferOutcome.Cancelled, await receiverOutcome.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(0, db.Count("chunks", userId));
    }

    [Fact]
    public async Task Second_transfer_is_refused_while_one_is_pending()
    {
        var (a, b, _) = await ConnectedPair(this);
        var source = Path.Combine(TempDir(), "p.bin");
        await File.WriteAllBytesAsync(source, new byte[1000]);

        await a.SendAsync(source);

        await Assert.ThrowsAsync<InvalidOperationException>(() => b.SendAsync(source));
    }

    [Fact]
    public async Task File_over_the_limit_is_refused_before_upload()
    {
        var (a, _, userId) = await ConnectedPair(this, FastOptions(o => o.MaxFileSize = 1000));
        var source = Path.Combine(TempDir(), "big.bin");
        await File.WriteAllBytesAsync(source, new byte[1001]);

        await Assert.ThrowsAsync<InvalidOperationException>(() => a.SendAsync(source));

        Assert.Equal(0, db.Count("Transfers", userId));
    }

    [Fact]
    public async Task Sending_without_a_peer_is_refused()
    {
        var a = await StartAsync(ChannelCrypto.GenerateUserId());
        var source = Path.Combine(TempDir(), "x.bin");
        await File.WriteAllBytesAsync(source, new byte[10]);

        await Assert.ThrowsAsync<InvalidOperationException>(() => a.SendAsync(source));
    }

    [Fact]
    public async Task Peer_disappearing_fails_the_pending_transfer()
    {
        var (a, b, userId) = await ConnectedPair(this);
        var source = Path.Combine(TempDir(), "f.bin");
        await File.WriteAllBytesAsync(source, RandomNumberGenerator.GetBytes(1_200_000));
        var outcome = new TaskCompletionSource<TransferOutcome>();
        a.TransferFinished += (o, _) => outcome.TrySetResult(o);

        await a.SendAsync(source);
        await b.DisposeAsync();

        Assert.Equal(TransferOutcome.Failed, await outcome.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(0, db.Count("chunks", userId));
    }

    [Fact]
    public async Task Unaccepted_transfer_expires_after_ttl()
    {
        var options = FastOptions(o =>
        {
            o.ActiveTtl = TimeSpan.FromSeconds(2);
            o.CleanupInterval = TimeSpan.FromMilliseconds(300);
        });
        var (a, _, userId) = await ConnectedPair(this, options);
        var source = Path.Combine(TempDir(), "t.bin");
        await File.WriteAllBytesAsync(source, RandomNumberGenerator.GetBytes(1_200_000));

        await a.SendAsync(source);
        Assert.Equal(1, db.Count("Transfers", userId));

        await WaitFor(() => db.Count("Transfers", userId) == 0, "expired transfer deleted", 15_000);
        Assert.Equal(0, db.Count("chunks", userId));
    }

    [Fact]
    public async Task Corrupted_chunk_fails_the_transfer_and_writes_no_file()
    {
        var (a, b, userId) = await ConnectedPair(this);
        var source = Path.Combine(TempDir(), "k.bin");
        await File.WriteAllBytesAsync(source, RandomNumberGenerator.GetBytes(1_200_000));
        await a.SendAsync(source);
        await WaitFor(() => b.Transfer is { Status: TransferStatus.Ready }, "incoming file offered");

        await using (var conn = new SqlConnection(db.ConnectionString))
        {
            await conn.ExecuteAsync(
                "UPDATE c SET Data = CAST(0xFF AS varbinary(1)) + SUBSTRING(Data, 2, DATALENGTH(Data)) FROM dbo.SqlDrop_TransferChunks c JOIN dbo.SqlDrop_Transfers t ON t.TransferId = c.TransferId WHERE t.ChannelId = @ch AND c.ChunkIndex = 1",
                new { ch = new ChannelCrypto(userId).ChannelId });
        }

        var targetDir = TempDir();
        await Assert.ThrowsAnyAsync<CryptographicException>(() => b.AcceptAsync(targetDir));

        Assert.Empty(Directory.GetFiles(targetDir));
        Assert.Equal(0, db.Count("chunks", userId));
    }
}
