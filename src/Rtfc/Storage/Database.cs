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

    public int SchemaVersion => int.Parse(
        Scalar<string>("SELECT value FROM meta WHERE key = 'schema_version'")!,
        System.Globalization.CultureInfo.InvariantCulture);

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
                              auto_owner_device, read_receipts, device_list_version, rev)
        VALUES ($person_id, $handle, $person_ca_cert, $status, $accepted_at, $inbound_mode, $auto_scope,
                $auto_owner_device, $read_receipts, $device_list_version, $rev)
        ON CONFLICT (person_id) DO UPDATE SET
          handle = excluded.handle, person_ca_cert = excluded.person_ca_cert, status = excluded.status,
          accepted_at = excluded.accepted_at, inbound_mode = excluded.inbound_mode, auto_scope = excluded.auto_scope,
          auto_owner_device = excluded.auto_owner_device, read_receipts = excluded.read_receipts,
          device_list_version = excluded.device_list_version, rev = excluded.rev
        """,
        ("$person_id", contact.PersonId), ("$handle", contact.Handle), ("$person_ca_cert", contact.PersonCaCert),
        ("$status", contact.Status), ("$accepted_at", Time(contact.AcceptedAt)), ("$inbound_mode", contact.InboundMode),
        ("$auto_scope", contact.AutoScope), ("$auto_owner_device", contact.AutoOwnerDevice),
        ("$read_receipts", contact.ReadReceipts ? 1L : 0L), ("$device_list_version", contact.DeviceListVersion),
        ("$rev", contact.Rev));

    private const string ContactColumns =
        "person_id, handle, person_ca_cert, status, accepted_at, inbound_mode, auto_scope, auto_owner_device, read_receipts, device_list_version, rev";

    private static ContactRow ReadContact(SqliteDataReader r) => new(
        r.GetString(0), r.GetString(1), Blob(r, 2), r.GetString(3), Timestamps.ParseOrNull(StringOrNull(r, 4)),
        r.GetString(5), StringOrNull(r, 6), StringOrNull(r, 7), r.GetInt64(8) != 0, r.GetInt64(9), r.GetInt64(10));

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

    // ---- inbox (person messages) ----

    /// <summary>Stores a message. Returns false when this id was already stored for this device, which is how a resend gets <c>ack: duplicate</c> (spec §7.2).</summary>
    public bool InsertMessage(InboxMessage m)
    {
        var affected = Execute(
            """
            INSERT OR IGNORE INTO inbox (id, to_device, kind, from_person, from_device, seq, reply_to, origin, hop, thread,
                                         body, sent_at, received_at, updated_at, state, handled_by, handled_at)
            VALUES ($id, $to_device, 'person', $from_person, $from_device, $seq, $reply_to, $origin, $hop, $thread,
                    $body, $sent_at, $received_at, $updated_at, $state, $handled_by, $handled_at)
            """,
            ("$id", m.Id), ("$to_device", m.ToDevice), ("$from_person", m.FromPerson), ("$from_device", m.FromDevice),
            ("$seq", m.Seq), ("$reply_to", m.ReplyTo), ("$origin", m.Origin), ("$hop", (long)m.Hop), ("$thread", m.Thread),
            ("$body", m.Body), ("$sent_at", Time(m.SentAt)), ("$received_at", Timestamps.Format(m.ReceivedAt)),
            ("$updated_at", Timestamps.Format(m.UpdatedAt)), ("$state", m.State), ("$handled_by", m.HandledBy),
            ("$handled_at", Time(m.HandledAt)));
        return affected == 1;
    }

    private const string MessageColumns =
        "id, to_device, from_person, from_device, seq, reply_to, origin, hop, thread, body, sent_at, received_at, updated_at, state, handled_by, handled_at";

    private static InboxMessage ReadMessage(SqliteDataReader r) => new(
        r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetInt64(4), StringOrNull(r, 5), r.GetString(6),
        (int)r.GetInt64(7), StringOrNull(r, 8), r.GetString(9), Timestamps.ParseOrNull(StringOrNull(r, 10)),
        Timestamps.Parse(r.GetString(11)), Timestamps.Parse(r.GetString(12)), r.GetString(13), StringOrNull(r, 14),
        Timestamps.ParseOrNull(StringOrNull(r, 15)));

    public InboxMessage? GetMessage(string id) =>
        QuerySingle($"SELECT {MessageColumns} FROM inbox WHERE id = $id AND kind = 'person'", ReadMessage, ("$id", id));

    /// <summary>Person messages, oldest first. <paramref name="state"/> null means every state.</summary>
    public IReadOnlyList<InboxMessage> ListMessages(string? state) => state is null
        ? Query($"SELECT {MessageColumns} FROM inbox WHERE kind = 'person' ORDER BY received_at, seq", ReadMessage)
        : Query($"SELECT {MessageColumns} FROM inbox WHERE kind = 'person' AND state = $state ORDER BY received_at, seq", ReadMessage, ("$state", state));

    public void SetMessageState(string id, string state, string? handledBy, DateTimeOffset now) => Execute(
        """
        UPDATE inbox SET state = $state, updated_at = $now,
          handled_by = CASE WHEN $handled_by IS NULL THEN handled_by ELSE $handled_by END,
          handled_at = CASE WHEN $handled_by IS NULL THEN handled_at ELSE $now END
        WHERE id = $id AND kind = 'person'
        """,
        ("$state", state), ("$now", Timestamps.Format(now)), ("$handled_by", handledBy), ("$id", id));

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

    private int Execute(string sql, params (string Name, object? Value)[] parameters)
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

    private static byte[] Blob(SqliteDataReader r, int i) => (byte[])r.GetValue(i);

    private static string? StringOrNull(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);

    private static string? Time(DateTimeOffset? value) => value is null ? null : Timestamps.Format(value.Value);

    public void Dispose() => _connection.Dispose();
}
