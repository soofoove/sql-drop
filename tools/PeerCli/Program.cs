// Dev-only headless peer for manual testing: PeerCli <connString> <userId> send <file> | listen
using SqlDrop.Core;

var (conn, userId, mode) = (args[0], args[1], args[2]);
await using var session = new SqlDropSession(conn, userId, Guid.NewGuid());
session.ConnectionChanged += s => Console.WriteLine($"[conn] {s}");
session.TransferChanged += t => Console.WriteLine($"[transfer] {(t is null ? "none" : $"{t.Role} {t.Status} {t.FileName} {t.ChunksReady}/{t.ChunkCount} recv {t.ChunksReceived}")}");
session.TransferFinished += (o, n) => Console.WriteLine($"[finished] {o} {n}");
session.Start();

if (mode == "send")
{
    while (session.Connection != ConnectionState.Connected) await Task.Delay(200);
    await session.SendAsync(args[3]);
    Console.WriteLine("[sent] waiting up to 180 s for outcome");
    await Task.Delay(TimeSpan.FromSeconds(180));
}
else
{
    Console.WriteLine("listening 180 s");
    await Task.Delay(TimeSpan.FromSeconds(180));
}
