using System.Reflection;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Rtfc.Storage;

/// <summary>
/// The SQLite file at <c>rtfc.db</c> (spec §13): WAL mode, hand-written SQL, and the
/// daemon as its single writer. One connection, serialized by a lock, is all that single
/// writer needs; SQLite calls return in microseconds and nothing here is worth a pool.
/// </summary>
public sealed class Database : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly Lock _lock = new();

    private Database(SqliteConnection connection)
    {
        _connection = connection;
    }

    public static Database Open(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString());
        connection.Open();

        var database = new Database(connection);
        try
        {
            database.Execute("PRAGMA journal_mode = WAL;");
            database.Execute("PRAGMA busy_timeout = 5000;");
            database.Execute("PRAGMA foreign_keys = ON;");
            database.Execute(Schema.Value);
            database.Migrate();
            return database;
        }
        catch
        {
            database.Dispose();
            throw;
        }
    }

    public static Database OpenInMemory() => Open(":memory:");

    private static readonly Lazy<string> Schema = new(() =>
    {
        using var stream = typeof(Database).Assembly.GetManifestResourceStream("rtfc.schema.sql")
            ?? throw new InvalidOperationException("The schema resource is missing from the assembly.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    });

    public const int CurrentSchemaVersion = 6;

    public int SchemaVersion => int.Parse(
        Scalar<string>("SELECT value FROM meta WHERE key = 'schema_version'")!,
        System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// Brings an older database up to <see cref="CurrentSchemaVersion"/>. schema.sql creates
    /// the current shape for new files; this is for files that already existed.
    /// </summary>
    private void Migrate()
    {
        lock (_lock)
        {
            using var transaction = _connection.BeginTransaction();
            var version = SchemaVersion;
            if (version < 2)
            {
                // v2 (Phase 2, auto-answer): a note and an attempt counter per message.
                Execute("ALTER TABLE inbox ADD COLUMN auto_note TEXT");
                Execute("ALTER TABLE inbox ADD COLUMN auto_attempts INTEGER NOT NULL DEFAULT 0");
                Execute("UPDATE meta SET value = '2' WHERE key = 'schema_version'");
                version = 2;
            }

            if (version < 3)
            {
                // v3 (Phase 4, async): the note serves every kind of message, and what was sent is recorded. The `sent` table itself
                // was already created by schema.sql, which runs first.
                Execute("ALTER TABLE inbox RENAME COLUMN auto_note TO note");
                Execute("UPDATE meta SET value = '3' WHERE key = 'schema_version'");
                version = 3;
            }

            if (version < 4)
            {
                // v4 (project-addressed messages, spec §7.6): where a reply to something we sent lands. A `sent` table that
                // schema.sql just created already has the column.
                if (!HasColumn("sent", "project_id"))
                {
                    Execute("ALTER TABLE sent ADD COLUMN project_id TEXT");
                }

                Execute("UPDATE meta SET value = '4' WHERE key = 'schema_version'");
                version = 4;
            }

            if (version < 5)
            {
                // v5 (auto_session, spec §7.3): which Claude Code session answers a contact.
                if (!HasColumn("contacts", "auto_session"))
                {
                    Execute("ALTER TABLE contacts ADD COLUMN auto_session TEXT");
                }

                Execute("UPDATE meta SET value = '5' WHERE key = 'schema_version'");
                version = 5;
            }

            if (version < 6)
            {
                // v6 (sources, spec §10.5): who the token belongs to, and the service's id for them.
                if (!HasColumn("accounts", "login"))
                {
                    Execute("ALTER TABLE accounts ADD COLUMN login TEXT");
                }

                if (!HasColumn("accounts", "account_id"))
                {
                    Execute("ALTER TABLE accounts ADD COLUMN account_id TEXT");
                }

                Execute("UPDATE meta SET value = '6' WHERE key = 'schema_version'");
            }

            transaction.Commit();
        }
    }

    // ---- meta ----

    public string? GetMeta(string key) => Scalar<string>("SELECT value FROM meta WHERE key = $key", ("$key", key));

    public void SetMeta(string key, string value) => Execute(
        "INSERT INTO meta (key, value) VALUES ($key, $value) ON CONFLICT (key) DO UPDATE SET value = excluded.value",
        ("$key", key), ("$value", value));

    // ---- self ----

    public void SaveSelf(SelfRow self) => Execute(
        """
        INSERT OR REPLACE INTO self (person_id, handle, person_ca_cert, device_id, device_name, device_cert, device_list_version)
        VALUES ($person_id, $handle, $person_ca_cert, $device_id, $device_name, $device_cert, $device_list_version)
        """,
        ("$person_id", self.PersonId), ("$handle", self.Handle), ("$person_ca_cert", self.PersonCaCert),
        ("$device_id", self.DeviceId), ("$device_name", self.DeviceName), ("$device_cert", self.DeviceCert),
        ("$device_list_version", self.DeviceListVersion));

    public SelfRow? GetSelf() => QuerySingle(
        "SELECT person_id, handle, person_ca_cert, device_id, device_name, device_cert, device_list_version FROM self",
        r => new SelfRow(r.GetString(0), r.GetString(1), Blob(r, 2), r.GetString(3), r.GetString(4), Blob(r, 5), r.GetInt64(6)));

    // ---- contacts ----

    public void UpsertContact(ContactRow contact) => Execute(
        """
        INSERT INTO contacts (person_id, handle, person_ca_cert, status, accepted_at, inbound_mode, auto_scope,
                              auto_owner_device, read_receipts, device_list_version, rev, auto_session)
        VALUES ($person_id, $handle, $person_ca_cert, $status, $accepted_at, $inbound_mode, $auto_scope,
                $auto_owner_device, $read_receipts, $device_list_version, $rev, $auto_session)
        ON CONFLICT (person_id) DO UPDATE SET
          handle = excluded.handle, person_ca_cert = excluded.person_ca_cert, status = excluded.status,
          accepted_at = excluded.accepted_at, inbound_mode = excluded.inbound_mode, auto_scope = excluded.auto_scope,
          auto_owner_device = excluded.auto_owner_device, read_receipts = excluded.read_receipts,
          device_list_version = excluded.device_list_version, rev = excluded.rev, auto_session = excluded.auto_session
        """,
        ("$person_id", contact.PersonId), ("$handle", contact.Handle), ("$person_ca_cert", contact.PersonCaCert),
        ("$status", contact.Status), ("$accepted_at", Time(contact.AcceptedAt)), ("$inbound_mode", contact.InboundMode),
        ("$auto_scope", contact.AutoScope), ("$auto_owner_device", contact.AutoOwnerDevice),
        ("$read_receipts", contact.ReadReceipts ? 1L : 0L), ("$device_list_version", contact.DeviceListVersion),
        ("$rev", contact.Rev), ("$auto_session", contact.AutoSession));

    private const string ContactColumns =
        "person_id, handle, person_ca_cert, status, accepted_at, inbound_mode, auto_scope, auto_owner_device, read_receipts, device_list_version, rev, auto_session";

    private static ContactRow ReadContact(SqliteDataReader r) => new(
        r.GetString(0), r.GetString(1), Blob(r, 2), r.GetString(3), Timestamps.ParseOrNull(StringOrNull(r, 4)),
        r.GetString(5), StringOrNull(r, 6), StringOrNull(r, 7), r.GetInt64(8) != 0, r.GetInt64(9), r.GetInt64(10), StringOrNull(r, 11));

    public ContactRow? GetContact(string personId) =>
        QuerySingle($"SELECT {ContactColumns} FROM contacts WHERE person_id = $id", ReadContact, ("$id", personId));

    /// <summary>Handles are local petnames and compared case-insensitively; a collision (spec §18.6) is the caller's problem.</summary>
    public ContactRow? FindContactByHandle(string handle) =>
        QuerySingle($"SELECT {ContactColumns} FROM contacts WHERE handle = $handle COLLATE NOCASE", ReadContact, ("$handle", handle));

    public IReadOnlyList<ContactRow> ListContacts() =>
        Query($"SELECT {ContactColumns} FROM contacts ORDER BY handle COLLATE NOCASE", ReadContact);

    // ---- devices ----

    public void UpsertDevice(DeviceRow device) => Execute(
        """
        INSERT INTO devices (device_id, person_id, name, cert, status, endpoints)
        VALUES ($device_id, $person_id, $name, $cert, $status, $endpoints)
        ON CONFLICT (device_id) DO UPDATE SET
          person_id = excluded.person_id, name = excluded.name, cert = excluded.cert,
          status = excluded.status, endpoints = excluded.endpoints
        """,
        ("$device_id", device.DeviceId), ("$person_id", device.PersonId), ("$name", device.Name), ("$cert", device.Cert),
        ("$status", device.Status), ("$endpoints", JsonSerializer.Serialize([.. device.Endpoints], StorageJson.Default.StringArray)));

    private static DeviceRow ReadDevice(SqliteDataReader r) => new(
        r.GetString(0), r.GetString(1), r.GetString(2), Blob(r, 3), r.GetString(4),
        JsonSerializer.Deserialize(r.GetString(5), StorageJson.Default.StringArray) ?? []);

    public DeviceRow? GetDevice(string deviceId) =>
        QuerySingle("SELECT device_id, person_id, name, cert, status, endpoints FROM devices WHERE device_id = $id", ReadDevice, ("$id", deviceId));

    public IReadOnlyList<DeviceRow> ListDevices(string personId) =>
        Query("SELECT device_id, person_id, name, cert, status, endpoints FROM devices WHERE person_id = $person ORDER BY name", ReadDevice, ("$person", personId));

    // ---- invites ----

    public void CreateInvite(string nonce, DateTimeOffset createdAt, DateTimeOffset expiresAt) => Execute(
        "INSERT INTO invites (nonce, created_at, expires_at) VALUES ($nonce, $created, $expires)",
        ("$nonce", nonce), ("$created", Timestamps.Format(createdAt)), ("$expires", Timestamps.Format(expiresAt)));

    public InviteRow? GetInvite(string nonce) => QuerySingle(
        "SELECT nonce, created_at, expires_at, used_by, used_at FROM invites WHERE nonce = $nonce",
        r => new InviteRow(r.GetString(0), Timestamps.Parse(r.GetString(1)), Timestamps.Parse(r.GetString(2)),
            StringOrNull(r, 3), Timestamps.ParseOrNull(StringOrNull(r, 4))),
        ("$nonce", nonce));

    /// <summary>
    /// Consumes a nonce atomically. Expiry is judged by the issuer's own clock, which is
    /// this one, so no two machines have to agree on the time (spec §5.1).
    /// </summary>
    public InviteUse TryUseInvite(string nonce, string usedBy, DateTimeOffset now)
    {
        lock (_lock)
        {
            using var transaction = _connection.BeginTransaction();
            var invite = GetInvite(nonce);
            if (invite is null)
            {
                return InviteUse.Unknown;
            }

            if (invite.UsedBy is not null)
            {
                return InviteUse.AlreadyUsed;
            }

            if (invite.ExpiresAt < now)
            {
                return InviteUse.Expired;
            }

            Execute("UPDATE invites SET used_by = $by, used_at = $at WHERE nonce = $nonce",
                ("$by", usedBy), ("$at", Timestamps.Format(now)), ("$nonce", nonce));
            transaction.Commit();
            return InviteUse.Used;
        }
    }

    // ---- inbox (messages, notices and source items) ----

    /// <summary>Stores a message. Returns false when this id was already stored for this device, which is how a resend gets <c>ack: duplicate</c> (spec §7.2).</summary>
    public bool InsertMessage(InboxMessage m)
    {
        var affected = Execute(
            """
            INSERT OR IGNORE INTO inbox (id, to_device, kind, from_person, from_device, seq, reply_to, origin, hop, thread,
                                         body, sent_at, received_at, updated_at, state, handled_by, handled_at,
                                         draft, note, auto_attempts, project_id, subscription_id, entity_key, url, title, events)
            VALUES ($id, $to_device, $kind, $from_person, $from_device, $seq, $reply_to, $origin, $hop, $thread,
                    $body, $sent_at, $received_at, $updated_at, $state, $handled_by, $handled_at,
                    $draft, $note, $auto_attempts, $project_id, $subscription_id, $entity_key, $url, $title, $events)
            """,
            ("$id", m.Id), ("$to_device", m.ToDevice), ("$from_person", m.FromPerson), ("$from_device", m.FromDevice),
            ("$seq", m.Seq), ("$reply_to", m.ReplyTo), ("$origin", m.Origin), ("$hop", (long)m.Hop), ("$thread", m.Thread),
            ("$body", m.Body), ("$sent_at", Time(m.SentAt)), ("$received_at", Timestamps.Format(m.ReceivedAt)),
            ("$updated_at", Timestamps.Format(m.UpdatedAt)), ("$state", m.State), ("$handled_by", m.HandledBy),
            ("$handled_at", Time(m.HandledAt)), ("$draft", m.Draft), ("$note", m.Note), ("$auto_attempts", (long)m.AutoAttempts), ("$kind", m.Kind),
            ("$project_id", m.ProjectId), ("$subscription_id", m.SubscriptionId), ("$entity_key", m.EntityKey), ("$url", m.Url), ("$title", m.Title),
            ("$events", m.Events));
        return affected == 1;
    }

    private const string MessageColumns =
        "id, to_device, from_person, from_device, seq, reply_to, origin, hop, thread, body, sent_at, received_at, updated_at, state, handled_by, handled_at, draft, note, auto_attempts, kind, project_id, "
        + "subscription_id, entity_key, url, title, events";

    /// <summary>Every kind the inbox shows: people's messages, rtfc's own notices, and source items (spec §10).</summary>
    private const string MessageKinds = "kind IN ('person', 'notice', 'source')";

    private static InboxMessage ReadMessage(SqliteDataReader r) => new(
        r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetInt64(4), StringOrNull(r, 5), r.GetString(6),
        (int)r.GetInt64(7), StringOrNull(r, 8), r.GetString(9), Timestamps.ParseOrNull(StringOrNull(r, 10)),
        Timestamps.Parse(r.GetString(11)), Timestamps.Parse(r.GetString(12)), r.GetString(13), StringOrNull(r, 14),
        Timestamps.ParseOrNull(StringOrNull(r, 15)), StringOrNull(r, 16), StringOrNull(r, 17), (int)r.GetInt64(18), r.GetString(19), StringOrNull(r, 20),
        StringOrNull(r, 21), StringOrNull(r, 22), StringOrNull(r, 23), StringOrNull(r, 24), StringOrNull(r, 25));

    /// <summary>The source item for an entity in a project, if it exists (one row per entity, spec §10.3).</summary>
    public InboxMessage? GetSourceItem(string projectId, string entityKey) =>
        QuerySingle($"SELECT {MessageColumns} FROM inbox WHERE kind = 'source' AND project_id = $project AND entity_key = $entity", ReadMessage,
            ("$project", projectId), ("$entity", entityKey));

    /// <summary>
    /// Appends what happened to an entity's item, creating it on first sight (spec §10.3). <paramref name="merge"/> gets the stored
    /// history (null for a new item) and returns the new title, body and history, or null when nothing is new; it runs under the
    /// lock, so two polls landing together cannot duplicate an event. An item that was read, dismissed or answered goes back to
    /// parked, because the new event is news. Returns the row, whether it was created, and how many events were added.
    /// </summary>
    public (InboxMessage? Item, bool Created, int Added) UpsertSourceItem(
        string projectId, string subscriptionId, string entityKey, string toDevice, DateTimeOffset now, Func<string?, SourceMerge?> merge)
    {
        lock (_lock)
        {
            using var transaction = _connection.BeginTransaction();
            var existing = GetSourceItem(projectId, entityKey);
            var merged = merge(existing?.Events);
            if (merged is null || merged.Added == 0)
            {
                transaction.Commit();
                return (existing, false, 0);
            }

            if (existing is null)
            {
                InsertMessage(new InboxMessage(
                    Ulid.NewUlid(now), toDevice, FromPerson: "", FromDevice: "", Seq: 0, ReplyTo: null, MessageOrigin.Source, Hop: 0, Thread: entityKey,
                    merged.Body, SentAt: now, ReceivedAt: now, UpdatedAt: now, InboxState.Parked, HandledBy: null, HandledAt: null,
                    Kind: InboxKind.Source, ProjectId: projectId, SubscriptionId: subscriptionId, EntityKey: entityKey, Url: merged.Url, Title: merged.Title, Events: merged.Events));
            }
            else
            {
                Execute(
                    """
                    UPDATE inbox SET title = $title, url = $url, body = $body, events = $events, updated_at = $now, subscription_id = $subscription,
                      state = CASE WHEN state IN ('read', 'dismissed', 'answered') THEN 'parked' ELSE state END,
                      received_at = CASE WHEN state IN ('read', 'dismissed', 'answered') THEN $now ELSE received_at END
                    WHERE id = $id AND to_device = $device
                    """,
                    ("$title", merged.Title), ("$url", merged.Url), ("$body", merged.Body), ("$events", merged.Events), ("$now", Timestamps.Format(now)),
                    ("$subscription", subscriptionId), ("$id", existing.Id), ("$device", existing.ToDevice));
            }

            var item = GetSourceItem(projectId, entityKey)!;
            transaction.Commit();
            return (item, existing is null, merged.Added);
        }
    }

    /// <summary>Removes a project's source items, when the project is forgotten (spec §10.2).</summary>
    public int DeleteSourceItems(string projectId) =>
        Execute("DELETE FROM inbox WHERE kind = 'source' AND project_id = $project", ("$project", projectId));

    public InboxMessage? GetMessage(string id) =>
        QuerySingle($"SELECT {MessageColumns} FROM inbox WHERE id = $id AND {MessageKinds}", ReadMessage, ("$id", id));

    /// <summary>Person messages, oldest first. <paramref name="state"/> null means every state.</summary>
    public IReadOnlyList<InboxMessage> ListMessages(string? state) => state is null
        ? Query($"SELECT {MessageColumns} FROM inbox WHERE {MessageKinds} ORDER BY received_at, seq", ReadMessage)
        : Query($"SELECT {MessageColumns} FROM inbox WHERE {MessageKinds} AND state = $state ORDER BY received_at, seq", ReadMessage, ("$state", state));

    public void SetMessageState(string id, string state, string? handledBy, DateTimeOffset now) => Execute(
        $"""
        UPDATE inbox SET state = $state, updated_at = $now,
          handled_by = CASE WHEN $handled_by IS NULL THEN handled_by ELSE $handled_by END,
          handled_at = CASE WHEN $handled_by IS NULL THEN handled_at ELSE $now END
        WHERE id = $id AND {MessageKinds}
        """,
        ("$state", state), ("$now", Timestamps.Format(now)), ("$handled_by", handledBy), ("$id", id));

    /// <summary>Replaces the human-facing note on a message without touching its state.</summary>
    public void SetMessageNote(string id, string? note, DateTimeOffset now) => Execute(
        $"UPDATE inbox SET note = $note, updated_at = $now WHERE id = $id AND {MessageKinds}",
        ("$note", note), ("$now", Timestamps.Format(now)), ("$id", id));

    /// <summary>Moves a message through the auto-answer states, optionally recording a note, a draft and one more attempt.</summary>
    public void SetAutoState(string id, string state, string? note, string? draft, bool countAttempt, DateTimeOffset now) => Execute(
        $"""
        UPDATE inbox SET state = $state, updated_at = $now, note = $note,
          draft = CASE WHEN $draft IS NULL THEN draft ELSE $draft END,
          auto_attempts = auto_attempts + $attempt
        WHERE id = $id AND {MessageKinds}
        """,
        ("$state", state), ("$now", Timestamps.Format(now)), ("$note", note), ("$draft", draft), ("$attempt", countAttempt ? 1L : 0L), ("$id", id));

    /// <summary>Auto-answer runs started since <paramref name="since"/>, for one person or for everyone (spec §7.4).</summary>
    public int CountAutoAnswers(string? fromPerson, DateTimeOffset since) => (int)(fromPerson is null
        ? Scalar<long>("SELECT COUNT(*) FROM inbox WHERE kind = 'person' AND auto_attempts > 0 AND received_at > $since", ("$since", Timestamps.Format(since)))
        : Scalar<long>("SELECT COUNT(*) FROM inbox WHERE kind = 'person' AND auto_attempts > 0 AND from_person = $person AND received_at > $since",
            ("$person", fromPerson), ("$since", Timestamps.Format(since))));

    /// <summary>Messages stored from one device since <paramref name="since"/>, for the inbound rate limit (spec §7.4).</summary>
    public int CountReceivedFrom(string fromDevice, DateTimeOffset since) => (int)Scalar<long>(
        "SELECT COUNT(*) FROM inbox WHERE kind = 'person' AND from_device = $device AND received_at > $since",
        ("$device", fromDevice), ("$since", Timestamps.Format(since)));

    // ---- outbox (spec §7.2) ----

    public void InsertOutbox(OutboxRow o) => Execute(
        """
        INSERT INTO outbox (id, to_person, to_device, kind, envelope, created_at, expires_at, attempts, state)
        VALUES ($id, $to_person, $to_device, $kind, $envelope, $created_at, $expires_at, $attempts, $state)
        """,
        ("$id", o.Id), ("$to_person", o.ToPerson), ("$to_device", o.ToDevice), ("$kind", o.Kind), ("$envelope", o.Envelope),
        ("$created_at", Timestamps.Format(o.CreatedAt)), ("$expires_at", Timestamps.Format(o.ExpiresAt)), ("$attempts", (long)o.Attempts), ("$state", o.State));

    private const string OutboxColumns = "id, to_person, to_device, kind, envelope, created_at, expires_at, attempts, state";

    private static OutboxRow ReadOutbox(SqliteDataReader r) => new(
        r.GetString(0), r.GetString(1), StringOrNull(r, 2), r.GetString(3), r.GetString(4), Timestamps.Parse(r.GetString(5)),
        Timestamps.Parse(r.GetString(6)), (int)r.GetInt64(7), r.GetString(8));

    public OutboxRow? GetOutbox(string id) =>
        QuerySingle($"SELECT {OutboxColumns} FROM outbox WHERE id = $id", ReadOutbox, ("$id", id));

    /// <summary>Oldest first. <paramref name="state"/> null means every state.</summary>
    public IReadOnlyList<OutboxRow> ListOutbox(string? state) => state is null
        ? Query($"SELECT {OutboxColumns} FROM outbox ORDER BY created_at", ReadOutbox)
        : Query($"SELECT {OutboxColumns} FROM outbox WHERE state = $state ORDER BY created_at", ReadOutbox, ("$state", state));

    public void SetOutboxState(string id, string state, bool countAttempt) => Execute(
        "UPDATE outbox SET state = $state, attempts = attempts + $attempt WHERE id = $id",
        ("$state", state), ("$attempt", countAttempt ? 1L : 0L), ("$id", id));

    public int CountOutbox(string state, string? toPerson = null) => (int)(toPerson is null
        ? Scalar<long>("SELECT COUNT(*) FROM outbox WHERE state = $state", ("$state", state))
        : Scalar<long>("SELECT COUNT(*) FROM outbox WHERE state = $state AND to_person = $person", ("$state", state), ("$person", toPerson)));

    // ---- sent ----

    public void InsertSent(SentRow s) => Execute(
        """
        INSERT OR REPLACE INTO sent (id, to_person, to_device, thread, reply_to, origin, kind, body, sent_at, delivered_at, read_at, expires_at, state, project_id)
        VALUES ($id, $to_person, $to_device, $thread, $reply_to, $origin, $kind, $body, $sent_at, $delivered_at, $read_at, $expires_at, $state, $project_id)
        """,
        ("$id", s.Id), ("$to_person", s.ToPerson), ("$to_device", s.ToDevice), ("$thread", s.Thread), ("$reply_to", s.ReplyTo), ("$origin", s.Origin),
        ("$kind", s.Kind), ("$body", s.Body), ("$sent_at", Timestamps.Format(s.SentAt)), ("$delivered_at", Time(s.DeliveredAt)), ("$read_at", Time(s.ReadAt)),
        ("$expires_at", Time(s.ExpiresAt)), ("$state", s.State), ("$project_id", s.ProjectId));

    private const string SentColumns = "id, to_person, to_device, thread, reply_to, origin, kind, body, sent_at, delivered_at, read_at, expires_at, state, project_id";

    private static SentRow ReadSent(SqliteDataReader r) => new(
        r.GetString(0), r.GetString(1), StringOrNull(r, 2), StringOrNull(r, 3), StringOrNull(r, 4), r.GetString(5), r.GetString(6), r.GetString(7),
        Timestamps.Parse(r.GetString(8)), Timestamps.ParseOrNull(StringOrNull(r, 9)), Timestamps.ParseOrNull(StringOrNull(r, 10)),
        Timestamps.ParseOrNull(StringOrNull(r, 11)), r.GetString(12), StringOrNull(r, 13));

    public SentRow? GetSent(string id) => QuerySingle($"SELECT {SentColumns} FROM sent WHERE id = $id", ReadSent, ("$id", id));

    /// <summary>Everything sent in answer to one inbox message, oldest first.</summary>
    public IReadOnlyList<SentRow> ListSentReplies(string replyTo) =>
        Query($"SELECT {SentColumns} FROM sent WHERE reply_to = $reply_to ORDER BY sent_at", ReadSent, ("$reply_to", replyTo));

    public void SetSentState(string id, string state, DateTimeOffset? deliveredAt, DateTimeOffset? readAt) => Execute(
        """
        UPDATE sent SET state = $state,
          delivered_at = CASE WHEN $delivered_at IS NULL THEN delivered_at ELSE $delivered_at END,
          read_at = CASE WHEN $read_at IS NULL THEN read_at ELSE $read_at END
        WHERE id = $id
        """,
        ("$state", state), ("$delivered_at", Time(deliveredAt)), ("$read_at", Time(readAt)), ("$id", id));

    // ---- projects (spec §10.2) ----

    /// <summary>Records a project root, or refreshes its name if the root is known. Returns the stored row, whose id is stable.</summary>
    public ProjectRow UpsertProject(ProjectRow project)
    {
        lock (_lock)
        {
            Execute(
                "INSERT INTO projects (id, root_path, name) VALUES ($id, $root, $name) ON CONFLICT (root_path) DO UPDATE SET name = excluded.name",
                ("$id", project.Id), ("$root", project.RootPath), ("$name", project.Name));
            return GetProjectByRoot(project.RootPath)!;
        }
    }

    private static ProjectRow ReadProject(SqliteDataReader r) => new(r.GetString(0), r.GetString(1), r.GetString(2));

    public ProjectRow? GetProject(string id) =>
        QuerySingle("SELECT id, root_path, name FROM projects WHERE id = $id", ReadProject, ("$id", id));

    public ProjectRow? GetProjectByRoot(string rootPath) =>
        QuerySingle("SELECT id, root_path, name FROM projects WHERE root_path = $root", ReadProject, ("$root", rootPath));

    /// <summary>Every project with this folder name, compared case-insensitively. More than one means the name is ambiguous here.</summary>
    public IReadOnlyList<ProjectRow> FindProjectsByName(string name) =>
        Query("SELECT id, root_path, name FROM projects WHERE name = $name COLLATE NOCASE", ReadProject, ("$name", name));

    public IReadOnlyList<ProjectRow> ListProjects() =>
        Query("SELECT id, root_path, name FROM projects ORDER BY name COLLATE NOCASE", ReadProject);

    /// <summary>Forgets a project (spec §10.2): its subscriptions and source items go with it; messages addressed to it fall back to the shared inbox.</summary>
    public void DeleteProject(string id)
    {
        lock (_lock)
        {
            using var transaction = _connection.BeginTransaction();
            Execute("DELETE FROM subscriptions WHERE project_id = $id", ("$id", id));
            DeleteSourceItems(id);
            Execute("DELETE FROM projects WHERE id = $id", ("$id", id));
            transaction.Commit();
        }
    }

    // ---- accounts (spec §10.5): the row is public, the token is a file ----

    public void UpsertAccount(AccountRow a) => Execute(
        """
        INSERT INTO accounts (name, type, base_url, created_at, login, account_id) VALUES ($name, $type, $url, $created, $login, $account)
        ON CONFLICT (name) DO UPDATE SET type = excluded.type, base_url = excluded.base_url, login = excluded.login, account_id = excluded.account_id
        """,
        ("$name", a.Name), ("$type", a.Type), ("$url", a.BaseUrl), ("$created", Timestamps.Format(a.CreatedAt)), ("$login", a.Login), ("$account", a.AccountId));

    private static AccountRow ReadAccount(SqliteDataReader r) =>
        new(r.GetString(0), r.GetString(1), StringOrNull(r, 2), StringOrNull(r, 3), StringOrNull(r, 4), Timestamps.Parse(r.GetString(5)));

    private const string AccountColumns = "name, type, base_url, login, account_id, created_at";

    public AccountRow? GetAccount(string name) =>
        QuerySingle($"SELECT {AccountColumns} FROM accounts WHERE name = $name", ReadAccount, ("$name", name));

    public IReadOnlyList<AccountRow> ListAccounts() =>
        Query($"SELECT {AccountColumns} FROM accounts ORDER BY name", ReadAccount);

    /// <summary>Removes an account; the subscriptions that used it are left in the error state so the user sees why they stopped.</summary>
    public bool DeleteAccount(string name)
    {
        lock (_lock)
        {
            using var transaction = _connection.BeginTransaction();
            Execute("UPDATE subscriptions SET status = 'error' WHERE account = $name", ("$name", name));
            var removed = Execute("DELETE FROM accounts WHERE name = $name", ("$name", name));
            transaction.Commit();
            return removed == 1;
        }
    }

    // ---- subscriptions (spec §10.2) ----

    private const string SubscriptionColumns = "id, project_id, account, selector, events, mode, status, config_hash";

    private static SubscriptionRow ReadSubscription(SqliteDataReader r) =>
        new(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5), r.GetString(6), r.GetString(7));

    public void UpsertSubscription(SubscriptionRow s) => Execute(
        """
        INSERT INTO subscriptions (id, project_id, account, selector, events, mode, status, config_hash)
        VALUES ($id, $project, $account, $selector, $events, $mode, $status, $hash)
        ON CONFLICT (id) DO UPDATE SET project_id = excluded.project_id, account = excluded.account, selector = excluded.selector,
          events = excluded.events, mode = excluded.mode, status = excluded.status, config_hash = excluded.config_hash
        """,
        ("$id", s.Id), ("$project", s.ProjectId), ("$account", s.Account), ("$selector", s.Selector), ("$events", s.Events), ("$mode", s.Mode),
        ("$status", s.Status), ("$hash", s.ConfigHash));

    public IReadOnlyList<SubscriptionRow> ListSubscriptions(string? projectId = null) => projectId is null
        ? Query($"SELECT {SubscriptionColumns} FROM subscriptions ORDER BY project_id, id", ReadSubscription)
        : Query($"SELECT {SubscriptionColumns} FROM subscriptions WHERE project_id = $project ORDER BY id", ReadSubscription, ("$project", projectId));

    public SubscriptionRow? GetSubscription(string id) =>
        QuerySingle($"SELECT {SubscriptionColumns} FROM subscriptions WHERE id = $id", ReadSubscription, ("$id", id));

    public int SetSubscriptionStatus(string id, string status) =>
        Execute("UPDATE subscriptions SET status = $status WHERE id = $id", ("$status", status), ("$id", id));

    public int DeleteSubscription(string id) => Execute("DELETE FROM subscriptions WHERE id = $id", ("$id", id));

    /// <summary>Parked or failed source items per project, split by the entity prefix (<c>jira:</c>, <c>bitbucket:</c>, …), for the status line.</summary>
    public int CountSourceItems(string projectId, string entityPrefix) => (int)Scalar<long>(
        "SELECT COUNT(*) FROM inbox WHERE kind = 'source' AND project_id = $project AND state IN ('parked', 'auto_failed') AND entity_key LIKE $prefix ESCAPE '\\'",
        ("$project", projectId), ("$prefix", entityPrefix.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%"));

    // ---- source cursors (spec §10.1) ----

    public SourceCursorRow? GetCursor(string pollKey) =>
        QuerySingle("SELECT poll_key, cursor, boundary_ids, next_poll_at, last_error FROM source_cursors WHERE poll_key = $key",
            r => new SourceCursorRow(r.GetString(0), r.GetString(1), r.GetString(2), Timestamps.ParseOrNull(StringOrNull(r, 3)), StringOrNull(r, 4)), ("$key", pollKey));

    public void UpsertCursor(SourceCursorRow c) => Execute(
        """
        INSERT INTO source_cursors (poll_key, cursor, boundary_ids, next_poll_at, last_error) VALUES ($key, $cursor, $boundary, $next, $error)
        ON CONFLICT (poll_key) DO UPDATE SET cursor = excluded.cursor, boundary_ids = excluded.boundary_ids, next_poll_at = excluded.next_poll_at, last_error = excluded.last_error
        """,
        ("$key", c.PollKey), ("$cursor", c.Cursor), ("$boundary", c.BoundaryIds), ("$next", Time(c.NextPollAt)), ("$error", c.LastError));

    public int DeleteCursor(string pollKey) => Execute("DELETE FROM source_cursors WHERE poll_key = $key", ("$key", pollKey));

    // ---- retention (spec §13) ----

    /// <summary>Removes what nobody needs any more: handled messages, finished sent rows and finished outbox rows older than the given instants. Returns how many rows went.</summary>
    public int Prune(DateTimeOffset handledBefore, DateTimeOffset outboxBefore)
    {
        lock (_lock)
        {
            using var transaction = _connection.BeginTransaction();
            var removed = Execute(
                $"DELETE FROM inbox WHERE {MessageKinds} AND state IN ('answered', 'dismissed', 'auto_done') AND updated_at < $before",
                ("$before", Timestamps.Format(handledBefore)));
            removed += Execute(
                "DELETE FROM sent WHERE state IN ('delivered', 'read', 'expired') AND sent_at < $before",
                ("$before", Timestamps.Format(handledBefore)));
            removed += Execute(
                "DELETE FROM outbox WHERE state IN ('delivered', 'expired') AND created_at < $before",
                ("$before", Timestamps.Format(outboxBefore)));
            transaction.Commit();
            return removed;
        }
    }

    // ---- sequence numbers ----

    /// <summary>The next outbound sequence number for a stream to one device, allocated durably (spec §7.1).</summary>
    public long NextSeqOut(string toDevice)
    {
        lock (_lock)
        {
            using var transaction = _connection.BeginTransaction();
            Execute("INSERT OR IGNORE INTO seq_out (to_device, next_seq) VALUES ($device, 1)", ("$device", toDevice));
            var seq = Scalar<long>("SELECT next_seq FROM seq_out WHERE to_device = $device", ("$device", toDevice));
            Execute("UPDATE seq_out SET next_seq = next_seq + 1 WHERE to_device = $device", ("$device", toDevice));
            transaction.Commit();
            return seq;
        }
    }

    /// <summary>Records the highest sequence seen from a device. Returns the previous high-water mark, for gap detection.</summary>
    public long RecordSeqIn(string fromDevice, long seq)
    {
        lock (_lock)
        {
            using var transaction = _connection.BeginTransaction();
            var previous = Scalar<long?>("SELECT max_seq FROM seq_in WHERE from_device = $device", ("$device", fromDevice)) ?? 0;
            Execute(
                """
                INSERT INTO seq_in (from_device, max_seq) VALUES ($device, $seq)
                ON CONFLICT (from_device) DO UPDATE SET max_seq = MAX(max_seq, excluded.max_seq)
                """,
                ("$device", fromDevice), ("$seq", seq));
            transaction.Commit();
            return previous;
        }
    }

    // ---- plumbing ----

    internal int Execute(string sql, params (string Name, object? Value)[] parameters)
    {
        lock (_lock)
        {
            using var command = Command(sql, parameters);
            return command.ExecuteNonQuery();
        }
    }

    private T? Scalar<T>(string sql, params (string Name, object? Value)[] parameters)
    {
        lock (_lock)
        {
            using var command = Command(sql, parameters);
            var value = command.ExecuteScalar();
            return value is null or DBNull ? default : (T)value;
        }
    }

    private T? QuerySingle<T>(string sql, Func<SqliteDataReader, T> read, params (string Name, object? Value)[] parameters)
        where T : class
    {
        lock (_lock)
        {
            using var command = Command(sql, parameters);
            using var reader = command.ExecuteReader();
            return reader.Read() ? read(reader) : null;
        }
    }

    private IReadOnlyList<T> Query<T>(string sql, Func<SqliteDataReader, T> read, params (string Name, object? Value)[] parameters)
    {
        lock (_lock)
        {
            using var command = Command(sql, parameters);
            using var reader = command.ExecuteReader();
            var rows = new List<T>();
            while (reader.Read())
            {
                rows.Add(read(reader));
            }

            return rows;
        }
    }

    private SqliteCommand Command(string sql, (string Name, object? Value)[] parameters)
    {
        var command = _connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        return command;
    }

    private bool HasColumn(string table, string column) =>
        Scalar<long>($"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name = $column", ("$column", column)) > 0;

    private static byte[] Blob(SqliteDataReader r, int i) => (byte[])r.GetValue(i);

    private static string? StringOrNull(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);

    private static string? Time(DateTimeOffset? value) => value is null ? null : Timestamps.Format(value.Value);

    public void Dispose() => _connection.Dispose();
}
