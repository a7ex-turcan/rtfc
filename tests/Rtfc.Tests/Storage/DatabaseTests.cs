using Rtfc.Storage;

namespace Rtfc.Tests.Storage;

public class DatabaseTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 27, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Opening_creates_the_schema_and_is_idempotent()
    {
        using var temp = new TempHome();

        using (var first = Database.Open(temp.Home.DatabasePath))
        {
            Assert.Equal(Database.CurrentSchemaVersion, first.SchemaVersion);
        }

        using var second = Database.Open(temp.Home.DatabasePath);
        Assert.Equal(Database.CurrentSchemaVersion, second.SchemaVersion);
        Assert.Null(second.GetSelf());
    }

    [Fact]
    public void A_version_1_file_is_migrated_on_open()
    {
        using var temp = new TempHome();

        using (var db = Database.Open(temp.Home.DatabasePath))
        {
            // Shape the file the way 0.1.x left it.
            db.Execute("ALTER TABLE inbox DROP COLUMN note");
            db.Execute("ALTER TABLE inbox DROP COLUMN auto_attempts");
            db.Execute("DROP TABLE sent");
            db.Execute("UPDATE meta SET value = '1' WHERE key = 'schema_version'");
            Assert.Equal(1, db.SchemaVersion);
        }

        using var migrated = Database.Open(temp.Home.DatabasePath);
        Assert.Equal(Database.CurrentSchemaVersion, migrated.SchemaVersion);
        Assert.True(migrated.InsertMessage(Message("01J8ZQ4Y7K3M9V2T6H0XWBNC5R", seq: 1) with { Note = "kept" }));
        Assert.Equal("kept", migrated.GetMessage("01J8ZQ4Y7K3M9V2T6H0XWBNC5R")!.Note);
        Assert.Null(migrated.GetSent("nothing"));
    }

    [Fact]
    public void A_version_2_file_is_migrated_on_open()
    {
        using var temp = new TempHome();

        using (var db = Database.Open(temp.Home.DatabasePath))
        {
            // Shape the file the way 0.2.0 left it: the note column under its old name, no sent table.
            db.Execute("ALTER TABLE inbox RENAME COLUMN note TO auto_note");
            db.Execute("DROP TABLE sent");
            db.Execute("UPDATE meta SET value = '2' WHERE key = 'schema_version'");
        }

        using var migrated = Database.Open(temp.Home.DatabasePath);
        Assert.Equal(3, migrated.SchemaVersion);
        Assert.True(migrated.InsertMessage(Message("01J8ZQ4Y7K3M9V2T6H0XWBNC5R", seq: 1) with { Note = "kept" }));
        Assert.Equal("kept", migrated.GetMessage("01J8ZQ4Y7K3M9V2T6H0XWBNC5R")!.Note);
    }

    [Fact]
    public void Outbox_and_sent_rows_round_trip_and_prune()
    {
        using var db = Database.OpenInMemory();
        var old = Now.AddDays(-40);

        db.InsertOutbox(new OutboxRow("o1", "p_sasha", null, OutboxKind.Reply, "{}", Now, Now.AddDays(7), 0, OutboxState.Pending));
        db.InsertOutbox(new OutboxRow("o2", "p_sasha", "d_sasha", OutboxKind.Receipt, "{}", old, old.AddDays(7), 3, OutboxState.Delivered));
        db.InsertSent(new SentRow("o1", "p_sasha", null, "t", "m1", MessageOrigin.Human, SentKind.Reply, "hello", Now, null, null, Now.AddDays(7), SentState.Queued));
        db.InsertSent(new SentRow("s2", "p_sasha", null, null, null, MessageOrigin.Human, SentKind.Message, "old", old, old, null, null, SentState.Delivered));
        db.InsertMessage(Message("01J8ZQ4Y7K3M9V2T6H0XWBNC5R", seq: 1) with { State = InboxState.Answered, UpdatedAt = old });
        db.InsertMessage(Message("01J8ZQ4Y7K3M9V2T6H0XWBNC5S", seq: 2));

        Assert.Equivalent(new OutboxRow("o1", "p_sasha", null, OutboxKind.Reply, "{}", Now, Now.AddDays(7), 0, OutboxState.Pending), db.GetOutbox("o1"), strict: true);
        Assert.Single(db.ListOutbox(OutboxState.Pending));
        Assert.Equal(2, db.ListOutbox(null).Count);
        Assert.Equal(1, db.CountOutbox(OutboxState.Pending));
        Assert.Equal(1, db.CountOutbox(OutboxState.Pending, "p_sasha"));
        Assert.Equal(0, db.CountOutbox(OutboxState.Pending, "p_other"));

        db.SetOutboxState("o1", OutboxState.Pending, countAttempt: true);
        Assert.Equal(1, db.GetOutbox("o1")!.Attempts);
        db.SetSentState("o1", SentState.Delivered, deliveredAt: Now.AddMinutes(5), readAt: null);
        var sent = db.GetSent("o1")!;
        Assert.Equal(SentState.Delivered, sent.State);
        Assert.Equal(Now.AddMinutes(5), sent.DeliveredAt);
        Assert.Equal("o1", Assert.Single(db.ListSentReplies("m1")).Id);

        Assert.Equal("1", db.GetMeta("away") ?? "1");
        db.SetMeta("away", "0");
        Assert.Equal("0", db.GetMeta("away"));

        // Retention: the old answered message, the old sent row and the old delivered outbox row go; the live ones stay.
        Assert.Equal(3, db.Prune(Now.AddDays(-30), Now.AddDays(-1)));
        Assert.Null(db.GetMessage("01J8ZQ4Y7K3M9V2T6H0XWBNC5R"));
        Assert.NotNull(db.GetMessage("01J8ZQ4Y7K3M9V2T6H0XWBNC5S"));
        Assert.Null(db.GetSent("s2"));
        Assert.NotNull(db.GetSent("o1"));
        Assert.Null(db.GetOutbox("o2"));
        Assert.NotNull(db.GetOutbox("o1"));
    }

    [Fact]
    public void Notices_live_beside_messages()
    {
        using var db = Database.OpenInMemory();
        db.InsertMessage(Message("01J8ZQ4Y7K3M9V2T6H0XWBNC5R", seq: 0) with { Kind = InboxKind.Notice, FromPerson = "p_me", FromDevice = "d_me", Body = "Your reply expired." });

        var notice = Assert.Single(db.ListMessages(InboxState.Parked));
        Assert.Equal(InboxKind.Notice, notice.Kind);
        Assert.Equal(0, db.CountReceivedFrom("d_me", Now.AddHours(-1))); // notices are not inbound traffic
    }

    [Fact]
    public void Auto_answer_state_notes_drafts_and_attempts_are_counted()
    {
        using var db = Database.OpenInMemory();
        db.InsertMessage(Message("01J8ZQ4Y7K3M9V2T6H0XWBNC5R", seq: 1));
        db.InsertMessage(Message("01J8ZQ4Y7K3M9V2T6H0XWBNC5S", seq: 2) with { FromPerson = "p_other", FromDevice = "d_other" });

        Assert.Equal(0, db.CountAutoAnswers(null, Now.AddHours(-1)));
        db.SetAutoState("01J8ZQ4Y7K3M9V2T6H0XWBNC5R", InboxState.AutoRunning, null, null, countAttempt: true, Now);
        db.SetAutoState("01J8ZQ4Y7K3M9V2T6H0XWBNC5R", InboxState.AutoDone, null, "the answer", countAttempt: false, Now.AddSeconds(5));

        var done = db.GetMessage("01J8ZQ4Y7K3M9V2T6H0XWBNC5R")!;
        Assert.Equal(InboxState.AutoDone, done.State);
        Assert.Equal("the answer", done.Draft);
        Assert.Equal(1, done.AutoAttempts);
        Assert.Equal(1, db.CountAutoAnswers("p_sasha", Now.AddHours(-1)));
        Assert.Equal(0, db.CountAutoAnswers("p_other", Now.AddHours(-1)));
        Assert.Equal(1, db.CountAutoAnswers(null, Now.AddHours(-1)));
        Assert.Equal(0, db.CountAutoAnswers(null, Now.AddMinutes(1)));

        Assert.Equal(1, db.CountReceivedFrom("d_sasha", Now.AddHours(-1)));
        Assert.Equal(1, db.CountReceivedFrom("d_other", Now.AddHours(-1)));
        Assert.Equal(0, db.CountReceivedFrom("d_nobody", Now.AddHours(-1)));
    }

    [Fact]
    public void Self_and_contacts_round_trip()
    {
        using var db = Database.OpenInMemory();

        db.SaveSelf(new SelfRow("p_me", "alex", [1, 2], "d_me", "laptop", [3, 4], 1));
        Assert.Equal("alex", db.GetSelf()!.Handle);

        var sasha = new ContactRow("p_sasha", "Sasha", [9], ContactStatus.Active, Now, InboundMode.Park, null, null, true, 0, 0);
        db.UpsertContact(sasha);
        Assert.Equivalent(sasha, db.GetContact("p_sasha"), strict: true);
        Assert.Equivalent(sasha, db.FindContactByHandle("sasha"), strict: true);
        Assert.Null(db.FindContactByHandle("nobody"));

        db.UpsertContact(sasha with { Status = ContactStatus.Removed, Rev = 1 });
        Assert.Equal(ContactStatus.Removed, Assert.Single(db.ListContacts()).Status);
    }

    [Fact]
    public void Devices_keep_their_endpoint_hints()
    {
        using var db = Database.OpenInMemory();

        var device = new DeviceRow("d_1", "p_sasha", "laptop", [1], DeviceStatus.Active, ["tcp:sasha-laptop:47821", "tcp:10.0.0.7:47821"]);
        db.UpsertDevice(device);

        Assert.Equivalent(device, db.GetDevice("d_1"), strict: true);
        Assert.Equivalent(device, Assert.Single(db.ListDevices("p_sasha")), strict: true);
        Assert.Empty(db.ListDevices("p_nobody"));
    }

    [Fact]
    public void An_invite_can_be_used_exactly_once_before_it_expires()
    {
        using var db = Database.OpenInMemory();
        db.CreateInvite("n1", Now, Now.AddHours(24));
        db.CreateInvite("n2", Now, Now.AddHours(24));

        Assert.Equal(InviteUse.Unknown, db.TryUseInvite("nope", "p_sasha", Now));
        Assert.Equal(InviteUse.Used, db.TryUseInvite("n1", "p_sasha", Now.AddHours(1)));
        Assert.Equal(InviteUse.AlreadyUsed, db.TryUseInvite("n1", "p_other", Now.AddHours(2)));
        Assert.Equal(InviteUse.Expired, db.TryUseInvite("n2", "p_sasha", Now.AddHours(25)));

        var used = db.GetInvite("n1")!;
        Assert.Equal("p_sasha", used.UsedBy);
        Assert.Equal(Now.AddHours(1), used.UsedAt);
    }

    [Fact]
    public void A_message_is_stored_once_and_a_resend_is_a_duplicate()
    {
        using var db = Database.OpenInMemory();
        var message = Message("01J8ZQ4Y7K3M9V2T6H0XWBNC5R", seq: 1);

        Assert.True(db.InsertMessage(message));
        Assert.False(db.InsertMessage(message));
        Assert.True(db.InsertMessage(message with { ToDevice = "d_other" })); // fan-out copy, same id

        Assert.Equal(message, db.GetMessage(message.Id));
        Assert.Equal(2, db.ListMessages(InboxState.Parked).Count);
        Assert.Empty(db.ListMessages(InboxState.Read));
    }

    [Fact]
    public void State_changes_record_who_handled_it_and_when()
    {
        using var db = Database.OpenInMemory();
        db.InsertMessage(Message("01J8ZQ4Y7K3M9V2T6H0XWBNC5R", seq: 1));

        db.SetMessageState("01J8ZQ4Y7K3M9V2T6H0XWBNC5R", InboxState.Read, handledBy: null, Now.AddMinutes(5));
        var read = db.GetMessage("01J8ZQ4Y7K3M9V2T6H0XWBNC5R")!;
        Assert.Equal(InboxState.Read, read.State);
        Assert.Null(read.HandledBy);
        Assert.Equal(Now.AddMinutes(5), read.UpdatedAt);

        db.SetMessageState("01J8ZQ4Y7K3M9V2T6H0XWBNC5R", InboxState.Answered, "d_me", Now.AddMinutes(9));
        var answered = db.GetMessage("01J8ZQ4Y7K3M9V2T6H0XWBNC5R")!;
        Assert.Equal("d_me", answered.HandledBy);
        Assert.Equal(Now.AddMinutes(9), answered.HandledAt);
    }

    [Fact]
    public void Sequence_numbers_count_per_stream_and_survive_reopening()
    {
        using var temp = new TempHome();

        using (var db = Database.Open(temp.Home.DatabasePath))
        {
            Assert.Equal(1, db.NextSeqOut("d_a"));
            Assert.Equal(2, db.NextSeqOut("d_a"));
            Assert.Equal(1, db.NextSeqOut("d_b"));

            Assert.Equal(0, db.RecordSeqIn("d_a", 1));
            Assert.Equal(1, db.RecordSeqIn("d_a", 3));
            Assert.Equal(3, db.RecordSeqIn("d_a", 2)); // out of order never lowers the mark
        }

        using var reopened = Database.Open(temp.Home.DatabasePath);
        Assert.Equal(3, reopened.NextSeqOut("d_a"));
        Assert.Equal(3, reopened.RecordSeqIn("d_a", 4));
    }

    private static InboxMessage Message(string id, long seq) => new(
        id, "d_me", "p_sasha", "d_sasha", seq, ReplyTo: null, MessageOrigin.Human, Hop: 0, Thread: null,
        "How does your retry policy handle poison messages?", SentAt: Now, ReceivedAt: Now, UpdatedAt: Now,
        InboxState.Parked, HandledBy: null, HandledAt: null);
}
