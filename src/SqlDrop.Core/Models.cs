namespace SqlDrop.Core;

public enum ConnectionState
{
    DbError,
    WaitingForPeer,
    Connected,
    ChannelBusy,
}

public enum TransferStatus : byte
{
    Uploading = 1,
    Ready = 2,
    Downloading = 3,
    Rejected = 4,
    Cancelled = 5,
    Failed = 6,
}

public enum TransferRole
{
    Sending,
    Receiving,
}

public enum TransferOutcome
{
    Completed,
    Rejected,
    Cancelled,
    Failed,
}

public sealed record TransferView(
    Guid Id,
    TransferRole Role,
    TransferStatus Status,
    string FileName,
    long FileSize,
    int ChunkCount,
    int ChunksReady,
    int ChunksReceived);

public sealed class SessionOptions
{
    public TimeSpan HeartbeatInterval { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan PeerTimeout { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan CommandTimeout { get; init; } = TimeSpan.FromSeconds(60);
    public int ChunkSize { get; init; } = 1024 * 1024;
    public long MaxFileSize { get; init; } = 100L * 1024 * 1024;
    public TimeSpan ActiveTransferTtl { get; init; } = TimeSpan.FromHours(24);
    public TimeSpan FinishedTransferTtl { get; init; } = TimeSpan.FromMinutes(1);
    public TimeSpan CleanupInterval { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>Builds options from the user config; out-of-range values are clamped, non-positive ones fall back to defaults.</summary>
    public static SessionOptions FromConfig(AppConfig config) => new()
    {
        PeerTimeout = TimeSpan.FromSeconds(Clamp(config.PeerTimeoutSeconds, 5, min: 2, max: 300)),
        CommandTimeout = TimeSpan.FromSeconds(Clamp(config.CommandTimeoutSeconds, 60, min: 5, max: 600)),
    };

    private static int Clamp(int value, int fallback, int min, int max) =>
        value <= 0 ? fallback : Math.Clamp(value, min, max);
}

internal sealed class TransferRow
{
    public Guid TransferId { get; set; }
    public Guid SenderInstanceId { get; set; }
    public byte Status { get; set; }
    public byte[] MetaEnc { get; set; } = [];
    public int ChunkCount { get; set; }
    public int ChunksReady { get; set; }
    public int ChunksReceived { get; set; }

    public TransferStatus StatusValue => (TransferStatus)Status;
    public bool IsActive => Status is >= 1 and <= 3;
}
