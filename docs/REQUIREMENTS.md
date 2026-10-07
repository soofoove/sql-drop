# SqlDrop — Requirements (v0.1, draft for review)

A utility for transferring files between two machines of the same user through a shared MS SQL database.

## 1. Goal and scope

- Transfer one file from machine A to machine B (and vice versa) through tables in a shared MS SQL database.
- Both machines are configured with the same `userId`; it also serves as the source of the encryption key.
- **Out of scope (v1):** folders, multiple simultaneous files, resume after interruption, transfer history, auto-update, more than two instances per channel, protection against an attacker who has access to both the DB and the userId.

## 2. Tech stack

| Area | Decision |
|---|---|
| UI | WinUI 3 (Windows App SDK 1.8), C#, .NET 8, **unpackaged** (plain exe, no MSIX) |
| DB | MS SQL Server, `Microsoft.Data.SqlClient` |
| Data access | Dapper (3 tables, ~8 queries; EF Core is overkill) |
| Encryption | AES-256-GCM (`System.Security.Cryptography.AesGcm`) |
| Key derivation | SHA-256 of userId (see section 5) |
| Local config | Plain-text `config.json` **next to the executable** (portable) |
| Packaging | Portable folder, no installer: unpackaged, **framework-dependent** (not self-contained). The build output folder (Release/bin) is zipped and used as is. Target machines must already have the .NET Desktop Runtime and the Windows App SDK runtime installed. Deleting the folder removes everything. |
| Architecture | No layering. Two projects: `SqlDrop.Core` (plain class library: crypto, DB, session/transfer logic — testable against a real SQL Server) and `SqlDrop` (WinUI shell, code-behind, no MVVM framework). Tests in `SqlDrop.Tests` (xUnit, Docker SQL Server). |

## 3. Functional requirements

### 3.1 Settings (configuration window)
- FR-1. Fields: **connection string**, **userId**.
- FR-2. A "Generate" button creates a random userId (≥128 bits of entropy, base64url, ~22 chars). A "Copy" button sits next to it.
- FR-3. A "Test connection" button: opens a connection, creates the schema if missing, shows the result.
- FR-4. Saving applies the settings without restarting the app. Without valid settings the main window shows "Not configured".

### 3.2 Main window
- FR-5. Small square window (~320×320, fixed size, not resizable), Mica backdrop, light/dark theme following the system, always-on-top as an option (off by default).
- FR-6. Top bar: status indicator (colored dot + text) and a gear button.
  - Gray — "Not configured"; red — "No DB connection"; yellow — "Waiting for peer"; green — "Connected".
- FR-7. Center — drag-and-drop zone (click opens a file picker as well). Exactly one file is accepted; if several are dropped the first is taken and a message is shown; folders are rejected.
- FR-8. Sending is available only when the status is green. Otherwise the zone is disabled with a hint.
- FR-9. During a transfer the zone shows the file name, a progress bar and a "Cancel" button.

### 3.3 Presence (heartbeat)
- FR-10. Each instance has an `InstanceId` (random GUID generated on every process start, **never persisted** — two windows sharing one config, or a copied app folder, must still be distinct peers) and once per second runs `UPDATE Peers SET LastSeen = SYSUTCDATETIME()` (the row is created via `MERGE`/upsert on start).
- FR-11. The same round trip (or the next one) reads the other instance's `LastSeen` for the same channel. Time is compared **using the DB server clock** (`DATEDIFF(MILLISECOND, LastSeen, SYSUTCDATETIME())`), not client clocks.
- FR-12. Threshold: a peer is alive if its `LastSeen` is not older than **5 s** (configurable in the settings window / `PeerTimeoutSeconds` in `config.json`, 2–300 s). The SQL command timeout is likewise configurable (settings window / `CommandTimeoutSeconds`, default 60 s, 5–600 s); the login timeout is set in the connection string.
- FR-13. A DB error on heartbeat → status "No DB connection"; retries continue at the same interval; the status recovers automatically.
- FR-14. On graceful shutdown the app deletes its own `Peers` row so the peer sees the disconnect immediately.
- FR-15. If a live instance with a different InstanceId already exists in the channel, there are exactly two. A third active instance gets an error status "Channel busy". Rule: at most 2 live peers.

### 3.4 Transfer
- FR-16. Sender: creates a `Transfers` record (status `Uploading`), reads the file as a stream, splits it into **1 MB** chunks, encrypts each chunk and writes it to `TransferChunks`. When done, status becomes `Ready`.
- FR-17. Size limit — **100 MB**, checked before sending starts (user gets a message).
- FR-18. Only one active transfer per channel at a time (`Uploading`/`Ready`/`Downloading`). Until it finishes, the drop zone is disabled ("Transfer in progress" / "Waiting to be accepted").
- FR-19. When a `Ready` transfer appears (polling once per second) the receiver shows a card: file name, size, **"Accept"** and **"Reject"** buttons.
- FR-20. "Accept" → folder picker (suggest the last used folder); file name comes from the metadata; on name conflict an automatic ` (1)` suffix is added.
- FR-21. The receiver downloads chunks in order, decrypts them, writes to a temporary `.part` file; after integrity verification renames it to the final file. Progress is visible to both sides (sender — via status and the received-chunks counter).
- FR-22. "Reject" or sender "Cancel" → status `Rejected`/`Cancelled`, chunks are deleted, both sides see a message.
- FR-23. If the peer disappears during a transfer (FR-12), the transfer is marked `Failed` and the UI shows an error. No resume — sending starts over.
- FR-24. A sender cannot accept its own file: check `SenderInstanceId != self`.

### 3.5 Cleanup
- FR-25. After the receiver successfully writes the file: status `Completed`, chunks are deleted immediately (the metadata row too — no history is kept).
- FR-26. **24 h** TTL for unaccepted/stuck transfers (finished-but-unreported ones — rejected/cancelled/failed — are deleted after 1 minute): any instance runs a `DELETE` of expired rows (by server time) on start and then once per minute (chunks cascade).

## 4. Data model (draft)

```sql
-- Actual table names carry a `SqlDrop_` prefix (SqlDrop_Peers, SqlDrop_Transfers, SqlDrop_TransferChunks, SqlDrop_SchemaInfo).
Peers(
  ChannelId   binary(32)   not null,   -- SHA-256(userId)
  InstanceId  uniqueidentifier not null,
  LastSeen    datetime2(3) not null,
  PRIMARY KEY (ChannelId, InstanceId))

Transfers(
  TransferId       uniqueidentifier PK,
  ChannelId        binary(32) not null,
  SenderInstanceId uniqueidentifier not null,
  Status           tinyint not null,   -- Uploading, Ready, Downloading, Completed, Rejected, Cancelled, Failed
  MetaEnc          varbinary(1024) not null,  -- encrypted blob: file name + exact file size
  ChunkCount       int not null,
  ChunksReady      int not null default 0,
  ChunksReceived   int not null default 0,
  CreatedAt        datetime2(3) not null,     -- SYSUTCDATETIME()
  UpdatedAt        datetime2(3) not null)

TransferChunks(
  TransferId uniqueidentifier not null FK -> Transfers ON DELETE CASCADE,
  ChunkIndex int not null,
  Data       varbinary(max) not null,   -- nonce(12) + ciphertext + tag(16)
  PRIMARY KEY (TransferId, ChunkIndex))
```

- There is no separate channels table; a channel is just a `ChannelId` value present in each table.
- The schema is created by the app idempotently on first connection (`IF OBJECT_ID(...) IS NULL CREATE TABLE ...`). The schema version is kept in a service table `SchemaInfo` for future migrations.
- Tables are shared by all users of the DB; isolation is by `ChannelId`.

## 5. Encryption and security

The app runs in a well-trusted environment, so security is deliberately kept minimal: encrypt the payload with a key derived from userId, and nothing more.

- SC-1. `ChannelId = SHA-256(userId)` identifies the channel in the DB; the raw userId is not stored there.
- SC-2. `Key = SHA-256(userId + "|key")` (32 bytes). No KDF stretching: userId is a random 128-bit value, not a human password.
- SC-3. Each chunk is encrypted with AES-256-GCM (random 12-byte nonce, stored with the chunk). The file metadata (name + exact size) is encrypted the same way.
- SC-4. GCM tag failure (wrong key/corruption) → transfer `Failed`, file is not written.
- SC-5. Data minimization: the DB stores only what the app needs. No machine names, user names, IPs, or file names/sizes in plaintext.
- SC-6. `config.json` (connection string, userId) is stored as **plain text** next to the exe. It is never logged.
- SC-7. On save, only the file-name part is used (`Path.GetFileName`) so a crafted name cannot escape the chosen folder.
- Explicitly not done: DPAPI, KDF stretching, AAD binding of chunks, key rotation, protection against an attacker holding both the DB and the userId.

## 6. Non-functional requirements

- NFR-1. DB load: ~2 lightweight queries per second per instance (heartbeat + state check). A transfer adds no extra idle load.
- NFR-2. Memory during a transfer: no more than ~2 chunks at a time (streaming, the whole file is never loaded).
- NFR-3. The UI never blocks: all DB and file work is async, UI updates go through `DispatcherQueue`.
- NFR-4. Speed is bound by the link to the DB; target: 100 MB over LAN in a reasonable time (tens of seconds), measured during implementation.
- NFR-5. All UI text is in English.
- NFR-6. *(Not implemented in v0.1.)* File log in `logs\` next to the executable (no secrets), size-capped with rotation (e.g. 1 MB × 3 files).
- NFR-7. **Portability:** no installer, no registry writes, no files outside the app folder (config and window position live next to the exe; the app itself uses no registry, `%APPDATA%`, `%LOCALAPPDATA%` or `%TEMP%` — only the OS file pickers keep their own recent-folder history). Deleting the folder leaves nothing behind in the OS. No autostart, no shell integration, no `%APPDATA%`/`%LOCALAPPDATA%`/`%TEMP%` usage by the app itself (the `.part` file is written in the destination folder the user chose).
- NFR-8. If the app folder is not writable (e.g. `Program Files`), the app shows a clear error on start instead of silently falling back to another location.

## 7. Acceptance criteria (end-to-end)

1. Two app copies (on two machines or two profiles) with identical settings show a green status ≤ 3 s after the second one starts.
2. Killing one side's process → the other side turns yellow within ~6 s.
3. A 50 MB file dropped on A is offered on B; after "Accept" it is byte-identical (SHA-256 comparison).
4. During the transfer the DB shows neither the file name nor the content in plaintext; after acceptance no transfer or chunk rows remain.
5. A wrong userId on B → B does not see A's channel (status "Waiting"), the file is unreachable.
6. A 101 MB file is rejected before sending.
7. An unaccepted transfer is deleted after 24 h (verified by temporarily lowering the TTL).
8. A 10 s DB connectivity loss → the app recovers on its own.
## 8. Assumptions and risks

- The app is framework-dependent: the target machine must already have the matching .NET Desktop Runtime and Windows App SDK runtime (same major/minor as the build). If missing, the app will not start (the OS/bootstrapper shows its own error); installing runtimes is outside the app's scope. This keeps the zip small. Self-contained and single-file publish are out of scope.
- WinUI templates normally ship with Visual Studio, so on this machine the project will be created manually / via `dotnet new` + csproj.
- Several instances may run from the same folder and share one `config.json`; each gets its own InstanceId at launch.
- Drag-and-drop in WinUI 3 does not work if the app runs elevated while Explorer does not — run without admin.
- DB response time affects presence accuracy: latencies > 5 s may cause false disconnects — the threshold is configurable.
- Storing 100 MB in `varbinary(max)` bloats the DB and transaction log; acceptable given the cleanup in FR-25/26. Recovery model SIMPLE is recommended for this DB.
- The DB account needs `CREATE TABLE` and `SELECT/INSERT/UPDATE/DELETE` rights.

## 9. Implementation plan (after approval)

1. WinUI 3 skeleton (unpackaged, framework-dependent; Release output folder is the distributable) + config next to exe + settings window.
2. Db layer (Dapper) + schema creation + connection test.
3. Crypto service + unit tests (round trip, tampered chunk, wrong key).
4. Presence (heartbeat/status) + status bar.
5. Transfer: send, receive, cleanup, cancel/reject.
6. Main window: drag-and-drop, accept card, progress.
7. E2E verification per section 7 (two app instances against a SQL Server on the LAN or a local Docker one).
