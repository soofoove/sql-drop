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
    public int ChunkSize { get; init; } = 1024 * 1024;
    public long MaxFileSize { get; init; } = 100L * 1024 * 1024;
    public TimeSpan ActiveTransferTtl { get; init; } = TimeSpan.FromHours(24);
    public TimeSpan FinishedTransferTtl { get; init; } = TimeSpan.FromMinutes(1);
    public TimeSpan CleanupInterval { get; init; } = TimeSpan.FromMinutes(1);
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
