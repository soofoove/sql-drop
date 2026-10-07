using Dapper;
using Microsoft.Data.SqlClient;

namespace SqlDrop.Core;

/// <summary>All SQL used by the app. Time is always taken from the DB server clock.</summary>
public sealed class SqlDropDb(string connectionString, int commandTimeoutSeconds = 60)
{
    private const string Active = "(1, 2, 3)";

    private const string SchemaSql = """
        IF OBJECT_ID('dbo.SqlDrop_SchemaInfo') IS NULL
        BEGIN
            CREATE TABLE dbo.SqlDrop_SchemaInfo (Version int NOT NULL);
            INSERT INTO dbo.SqlDrop_SchemaInfo (Version) VALUES (1);
        END;

        IF OBJECT_ID('dbo.SqlDrop_Peers') IS NULL
            CREATE TABLE dbo.SqlDrop_Peers (
                ChannelId  binary(32)       NOT NULL,
                InstanceId uniqueidentifier NOT NULL,
                LastSeen   datetime2(3)     NOT NULL,
                CONSTRAINT PK_SqlDrop_Peers PRIMARY KEY (ChannelId, InstanceId));

        IF OBJECT_ID('dbo.SqlDrop_Transfers') IS NULL
            CREATE TABLE dbo.SqlDrop_Transfers (
                TransferId       uniqueidentifier NOT NULL CONSTRAINT PK_SqlDrop_Transfers PRIMARY KEY,
                ChannelId        binary(32)       NOT NULL,
                SenderInstanceId uniqueidentifier NOT NULL,
                Status           tinyint          NOT NULL,
                MetaEnc          varbinary(1024)  NOT NULL,
                ChunkCount       int              NOT NULL,
                ChunksReady      int              NOT NULL CONSTRAINT DF_SqlDrop_Transfers_Ready DEFAULT 0,
                ChunksReceived   int              NOT NULL CONSTRAINT DF_SqlDrop_Transfers_Received DEFAULT 0,
                CreatedAt        datetime2(3)     NOT NULL,
                UpdatedAt        datetime2(3)     NOT NULL);

        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_SqlDrop_Transfers_Channel')
            CREATE INDEX IX_SqlDrop_Transfers_Channel ON dbo.SqlDrop_Transfers (ChannelId, CreatedAt);

        IF OBJECT_ID('dbo.SqlDrop_TransferChunks') IS NULL
            CREATE TABLE dbo.SqlDrop_TransferChunks (
                TransferId uniqueidentifier NOT NULL,
                ChunkIndex int              NOT NULL,
                Data       varbinary(max)   NOT NULL,
                CONSTRAINT PK_SqlDrop_TransferChunks PRIMARY KEY (TransferId, ChunkIndex),
                CONSTRAINT FK_SqlDrop_TransferChunks_Transfers FOREIGN KEY (TransferId)
                    REFERENCES dbo.SqlDrop_Transfers (TransferId) ON DELETE CASCADE);
        """;

    private async Task<SqlConnection> OpenAsync(CancellationToken ct)
    {
        var conn = new SqlConnection(connectionString);
        await conn.OpenAsync(ct);
        return conn;
    }

    private CommandDefinition Cmd(string sql, object? param, CancellationToken ct) =>
        new(sql, param, commandTimeout: commandTimeoutSeconds, cancellationToken: ct);

    public async Task EnsureSchemaAsync(CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await conn.ExecuteAsync(Cmd(SchemaSql, null, ct));
    }

    // ---- presence ----

    private sealed class HeartbeatRow
    {
        public bool Registered { get; set; }
        public int LiveOthers { get; set; }
    }

    /// <summary>Upserts our heartbeat (unless two other live peers already own the channel) and counts live others.</summary>
    public async Task<(bool Registered, int LiveOthers)> HeartbeatAsync(
        byte[] channelId, Guid instanceId, int timeoutMs, CancellationToken ct)
    {
        const string sql = """
            SET NOCOUNT ON;
            DECLARE @now datetime2(3) = SYSUTCDATETIME();
            BEGIN TRAN;
            IF EXISTS (SELECT 1 FROM dbo.SqlDrop_Peers WITH (UPDLOCK, HOLDLOCK)
                       WHERE ChannelId = @ch AND InstanceId = @inst)
                UPDATE dbo.SqlDrop_Peers SET LastSeen = @now WHERE ChannelId = @ch AND InstanceId = @inst;
            ELSE IF (SELECT COUNT(*) FROM dbo.SqlDrop_Peers WITH (UPDLOCK, HOLDLOCK)
                     WHERE ChannelId = @ch AND DATEDIFF(MILLISECOND, LastSeen, @now) <= @timeoutMs) < 2
                INSERT INTO dbo.SqlDrop_Peers (ChannelId, InstanceId, LastSeen) VALUES (@ch, @inst, @now);
            COMMIT;
            SELECT
                CAST(CASE WHEN EXISTS (SELECT 1 FROM dbo.SqlDrop_Peers
                                       WHERE ChannelId = @ch AND InstanceId = @inst) THEN 1 ELSE 0 END AS bit) AS Registered,
                (SELECT COUNT(*) FROM dbo.SqlDrop_Peers
                 WHERE ChannelId = @ch AND InstanceId <> @inst
                   AND DATEDIFF(MILLISECOND, LastSeen, @now) <= @timeoutMs) AS LiveOthers;
            """;
        await using var conn = await OpenAsync(ct);
        var row = await conn.QuerySingleAsync<HeartbeatRow>(
            Cmd(sql, new { ch = channelId, inst = instanceId, timeoutMs }, ct));
        return (row.Registered, row.LiveOthers);
    }

    public async Task RemovePeerAsync(byte[] channelId, Guid instanceId)
    {
        await using var conn = await OpenAsync(CancellationToken.None);
        await conn.ExecuteAsync(Cmd(
            "DELETE FROM dbo.SqlDrop_Peers WHERE ChannelId = @ch AND InstanceId = @inst",
            new { ch = channelId, inst = instanceId }, CancellationToken.None));
    }

    // ---- transfers ----

    /// <summary>Creates an Uploading transfer unless the channel already has an active one.</summary>
    public async Task<bool> TryCreateTransferAsync(
        Guid id, byte[] channelId, Guid senderId, byte[] metaEnc, int chunkCount, CancellationToken ct)
    {
        var sql = $"""
            DECLARE @now datetime2(3) = SYSUTCDATETIME();
            INSERT INTO dbo.SqlDrop_Transfers
                (TransferId, ChannelId, SenderInstanceId, Status, MetaEnc, ChunkCount, CreatedAt, UpdatedAt)
            SELECT @id, @ch, @sender, 1, @meta, @count, @now, @now
            WHERE NOT EXISTS (SELECT 1 FROM dbo.SqlDrop_Transfers WITH (UPDLOCK, HOLDLOCK)
                              WHERE ChannelId = @ch AND Status IN {Active});
            """;
        await using var conn = await OpenAsync(ct);
        var rows = await conn.ExecuteAsync(Cmd(sql,
            new { id, ch = channelId, sender = senderId, meta = metaEnc, count = chunkCount }, ct));
        return rows == 1;
    }

    /// <summary>Stores a chunk and bumps ChunksReady; returns false if the transfer is no longer Uploading.</summary>
    public async Task<bool> AppendChunkAsync(Guid id, int index, byte[] data, CancellationToken ct)
    {
        const string sql = """
            SET XACT_ABORT ON;
            BEGIN TRAN;
            UPDATE dbo.SqlDrop_Transfers SET ChunksReady = @ready, UpdatedAt = SYSUTCDATETIME()
            WHERE TransferId = @id AND Status = 1;
            IF @@ROWCOUNT = 0
            BEGIN
                ROLLBACK;
                SELECT CAST(0 AS bit);
                RETURN;
            END;
            INSERT INTO dbo.SqlDrop_TransferChunks (TransferId, ChunkIndex, Data) VALUES (@id, @index, @data);
            COMMIT;
            SELECT CAST(1 AS bit);
            """;
        await using var conn = await OpenAsync(ct);
        return await conn.QuerySingleAsync<bool>(Cmd(sql, new { id, index, ready = index + 1, data }, ct));
    }

    /// <summary>Atomically moves a transfer to <paramref name="to"/> if it is currently in one of <paramref name="from"/>.</summary>
    public async Task<bool> SetStatusAsync(
        Guid id, TransferStatus to, CancellationToken ct, params TransferStatus[] from)
    {
        var fromList = string.Join(", ", from.Select(s => (byte)s));
        var sql = $"""
            UPDATE dbo.SqlDrop_Transfers SET Status = @to, UpdatedAt = SYSUTCDATETIME()
            WHERE TransferId = @id AND Status IN ({fromList})
            """;
        await using var conn = await OpenAsync(ct);
        return await conn.ExecuteAsync(Cmd(sql, new { id, to = (byte)to }, ct)) == 1;
    }

    /// <summary>Records receive progress; returns false if the transfer is no longer Downloading.</summary>
    public async Task<bool> ReportReceivedAsync(Guid id, int received, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        return await conn.ExecuteAsync(Cmd(
            """
            UPDATE dbo.SqlDrop_Transfers SET ChunksReceived = @received, UpdatedAt = SYSUTCDATETIME()
            WHERE TransferId = @id AND Status = 3
            """,
            new { id, received }, ct)) == 1;
    }

    internal async Task<TransferRow?> GetLatestAsync(byte[] channelId, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        return await conn.QueryFirstOrDefaultAsync<TransferRow>(Cmd(
            """
            SELECT TOP 1 TransferId, SenderInstanceId, Status, MetaEnc, ChunkCount, ChunksReady, ChunksReceived
            FROM dbo.SqlDrop_Transfers WHERE ChannelId = @ch ORDER BY CreatedAt DESC
            """,
            new { ch = channelId }, ct));
    }

    internal async Task<TransferRow?> GetByIdAsync(Guid id, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        return await conn.QueryFirstOrDefaultAsync<TransferRow>(Cmd(
            """
            SELECT TransferId, SenderInstanceId, Status, MetaEnc, ChunkCount, ChunksReady, ChunksReceived
            FROM dbo.SqlDrop_Transfers WHERE TransferId = @id
            """,
            new { id }, ct));
    }

    public async Task<byte[]?> GetChunkAsync(Guid id, int index, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        return await conn.QueryFirstOrDefaultAsync<byte[]>(Cmd(
            "SELECT Data FROM dbo.SqlDrop_TransferChunks WHERE TransferId = @id AND ChunkIndex = @index",
            new { id, index }, ct));
    }

    public async Task DeleteChunksAsync(Guid id)
    {
        await using var conn = await OpenAsync(CancellationToken.None);
        await conn.ExecuteAsync(Cmd(
            "DELETE FROM dbo.SqlDrop_TransferChunks WHERE TransferId = @id", new { id }, CancellationToken.None));
    }

    public async Task DeleteTransferAsync(Guid id)
    {
        await using var conn = await OpenAsync(CancellationToken.None);
        await conn.ExecuteAsync(Cmd(
            "DELETE FROM dbo.SqlDrop_Transfers WHERE TransferId = @id", new { id }, CancellationToken.None));
    }

    /// <summary>Deletes expired transfers (chunks cascade) and long-dead peer rows of our channel.</summary>
    public async Task CleanupAsync(
        byte[] channelId, TimeSpan activeTtl, TimeSpan finishedTtl, TimeSpan deadPeerTtl, CancellationToken ct)
    {
        var sql = $"""
            DELETE FROM dbo.SqlDrop_Transfers
            WHERE ChannelId = @ch AND (
                (Status IN {Active} AND UpdatedAt < DATEADD(SECOND, -@activeSec, SYSUTCDATETIME()))
                OR (Status NOT IN {Active} AND UpdatedAt < DATEADD(SECOND, -@finishedSec, SYSUTCDATETIME())));
            DELETE FROM dbo.SqlDrop_Peers
            WHERE ChannelId = @ch AND LastSeen < DATEADD(SECOND, -@peerSec, SYSUTCDATETIME());
            """;
        await using var conn = await OpenAsync(ct);
        await conn.ExecuteAsync(Cmd(sql, new
        {
            ch = channelId,
            activeSec = (int)activeTtl.TotalSeconds,
            finishedSec = (int)finishedTtl.TotalSeconds,
            peerSec = (int)deadPeerTtl.TotalSeconds,
        }, ct));
    }
}
