// Dev-only headless peer for manual testing against a running SqlDrop instance.
//   PeerCli <userId> send <file>
//   PeerCli <userId> listen
// The connection string is read from the SQLDROP_CONN environment variable (keeps the password out of the
// command line and shell history).
using SqlDrop.Core;

var conn = Environment.GetEnvironmentVariable("SQLDROP_CONN");
if (string.IsNullOrWhiteSpace(conn) || args.Length < 2 || args[1] is not ("send" or "listen") || (args[1] == "send" && args.Length < 3))
{
    Console.Error.WriteLine("""
        Usage: set the SQLDROP_CONN environment variable to a connection string, then run:
          PeerCli <userId> send <file>
          PeerCli <userId> listen
        """);
    return 1;
}

var (userId, mode) = (args[0], args[1]);
await using var session = new SqlDropSession(conn, userId, Guid.NewGuid());
session.ConnectionChanged += s => Console.WriteLine($"[conn] {s}");
session.TransferChanged += t => Console.WriteLine($"[transfer] {(t is null ? "none" : $"{t.Role} {t.Status} {t.FileName} {t.ChunksReady}/{t.ChunkCount} recv {t.ChunksReceived}")}");
session.TransferFinished += (o, n) => Console.WriteLine($"[finished] {o} {n}");
session.Start();

if (mode == "send")
{
    while (session.Connection != ConnectionState.Connected) await Task.Delay(200);
    await session.SendAsync(args[2]);
    Console.WriteLine("[sent] waiting up to 180 s for outcome");
    await Task.Delay(TimeSpan.FromSeconds(180));
}
else
{
    Console.WriteLine("listening 180 s");
    await Task.Delay(TimeSpan.FromSeconds(180));
}

return 0;
