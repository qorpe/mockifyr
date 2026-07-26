using Mediant.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Mockifyr.Core;
using Mockifyr.Server;
using Mockifyr.Stores.InMemory;

namespace Mockifyr.Application.Tests;

/// <summary>
/// Behavioral coverage for the captured-message inbox (G18a, ADR 0009). No WireMock oracle exists —
/// WireMock has no message concept — so this is a self-test of Mockifyr's own claims: the store is
/// bounded (oldest evicted first), reads are newest-first, and the inbox is tenant-scoped below the
/// HTTP surface (ADR 0003).
/// </summary>
public sealed class G18aMessageStoreTests
{
    private static readonly TenantId Acme = new("acme");
    private static readonly TenantId Globex = new("globex");

    private static MessageEnvelope Message(
        string to = "user@example.com",
        MessageChannel channel = MessageChannel.Email,
        string subject = "Hi",
        string body = "Hello there",
        string? html = null) =>
        new(Guid.NewGuid(), channel, "noreply@app.test", [to], subject, body, html,
            new Dictionary<string, string>(), [], DateTimeOffset.UtcNow);

    // ---- Store semantics ----------------------------------------------------------------------

    [Fact]
    public void Reads_AreNewestFirst()
    {
        var store = new InMemoryMessageStore();
        var first = Message(subject: "first");
        var second = Message(subject: "second");
        store.Append(TenantId.Default, first);
        store.Append(TenantId.Default, second);

        var messages = store.GetMessages(TenantId.Default);

        Assert.Equal(["second", "first"], messages.Select(m => m.Subject));
    }

    [Fact]
    public void Capacity_EvictsOldestFirst()
    {
        var store = new InMemoryMessageStore(capacity: 3);
        for (var i = 1; i <= 5; i++)
        {
            store.Append(TenantId.Default, Message(subject: $"m{i}"));
        }

        var messages = store.GetMessages(TenantId.Default);

        // 5 appended into a bound of 3: m1 and m2 are gone, newest first.
        Assert.Equal(["m5", "m4", "m3"], messages.Select(m => m.Subject));
    }

    [Fact]
    public void AtExactlyCapacity_NothingIsEvicted()
    {
        var store = new InMemoryMessageStore(capacity: 3);
        for (var i = 1; i <= 3; i++)
        {
            store.Append(TenantId.Default, Message(subject: $"m{i}"));
        }

        Assert.Equal(["m3", "m2", "m1"], store.GetMessages(TenantId.Default).Select(m => m.Subject));
    }

    [Fact]
    public void NonPositiveCapacity_FallsBackToTheDefault()
    {
        Assert.Equal(InMemoryMessageStore.DefaultCapacity, new InMemoryMessageStore(0).Capacity)
;
        Assert.Equal(InMemoryMessageStore.DefaultCapacity, new InMemoryMessageStore(-5).Capacity);
        Assert.Equal(7, new InMemoryMessageStore(7).Capacity);
    }

    [Fact]
    public void Inboxes_AreTenantScoped()
    {
        var store = new InMemoryMessageStore();
        var acmeMessage = Message(subject: "acme-only");
        store.Append(Acme, acmeMessage);

        Assert.Empty(store.GetMessages(Globex));
        Assert.Null(store.Get(Globex, acmeMessage.Id));
        Assert.False(store.Remove(Globex, acmeMessage.Id));

        // The failed cross-tenant remove did not touch acme's inbox.
        Assert.Single(store.GetMessages(Acme));
    }

    [Fact]
    public void Remove_And_Reset_RoundTrip()
    {
        var store = new InMemoryMessageStore();
        var message = Message();
        store.Append(TenantId.Default, message);
        store.Append(TenantId.Default, Message());

        Assert.True(store.Remove(TenantId.Default, message.Id));
        Assert.False(store.Remove(TenantId.Default, message.Id));
        Assert.Single(store.GetMessages(TenantId.Default));

        store.Reset(TenantId.Default);
        Assert.Empty(store.GetMessages(TenantId.Default));
    }

    [Fact]
    public void Sink_AppendsToTheStore()
    {
        var store = new InMemoryMessageStore();
        var sink = new StoreMessageSink(store);
        sink.Accept(Acme, Message(subject: "via-sink"));

        Assert.Equal("via-sink", Assert.Single(store.GetMessages(Acme)).Subject);
    }

    // ---- Filter semantics (shared by list + count) --------------------------------------------

    [Theory]
    [InlineData(null, null, null, true)]
    [InlineData(MessageChannel.Email, null, null, true)]
    [InlineData(MessageChannel.Sms, null, null, false)]
    [InlineData(null, "USER@example", null, true)] // recipient matches case-insensitively, substring
    [InlineData(null, "someone-else", null, false)]
    [InlineData(null, null, "hello", true)] // body, case-insensitive
    [InlineData(null, null, "HI", true)] // subject
    [InlineData(null, null, "absent", false)]
    public void Filter_Matches(MessageChannel? channel, string? recipient, string? contains, bool expected)
    {
        Assert.Equal(expected, MessageFilter.Matches(Message(), channel, recipient, contains));
    }

    [Fact]
    public void Filter_SearchesHtmlBodyToo()
    {
        var message = Message(html: "<b>secret-token</b>");
        Assert.True(MessageFilter.Matches(message, null, null, "secret-token"));
    }

    [Fact]
    public void Filter_Recipient_MatchesAnyAddressee_NotAll()
    {
        // Two recipients, only one matches the filter: ANY semantics — the message still matches.
        var message = new MessageEnvelope(Guid.NewGuid(), MessageChannel.Email, "noreply@app.test",
            ["first@a.test", "second@b.test"], "Hi", "body", null, new Dictionary<string, string>(), [],
            DateTimeOffset.UtcNow);
        Assert.True(MessageFilter.Matches(message, null, "second@b", null));
    }

    // ---- CQRS handlers (tenant isolation below the HTTP surface) ------------------------------

    private static ServiceProvider Host() => new ServiceCollection().AddMockifyr().BuildServiceProvider();

    [Fact]
    public async Task Handlers_AreTenantScoped_AndShareTheFilter()
    {
        using var provider = Host();
        var sender = provider.GetRequiredService<ISender>();
        var sink = provider.GetRequiredService<IMessageSink>();

        sink.Accept(Acme, Message(to: "a@acme.test", subject: "otp", body: "Your code is 123456"));
        sink.Accept(Acme, Message(to: "b@acme.test", channel: MessageChannel.Sms, subject: null!, body: "code 999"));
        sink.Accept(Globex, Message(to: "c@globex.test"));

        // List: tenant-scoped, filterable.
        var acmeAll = (await sender.Send(new GetMessagesQuery(Acme))).Value;
        Assert.Equal(2, acmeAll.Count);
        var acmeSms = (await sender.Send(new GetMessagesQuery(Acme, MessageChannel.Sms))).Value;
        Assert.Equal("code 999", Assert.Single(acmeSms).Body);

        // Count agrees with list under the same filter.
        Assert.Equal(1, (await sender.Send(new CountMessagesQuery(Acme, Contains: "123456"))).Value);

        // Get/Delete refuse to cross tenants.
        var acmeId = acmeAll[0].Id;
        Assert.False((await sender.Send(new GetMessageQuery(acmeId, Globex))).IsSuccess);
        Assert.False((await sender.Send(new DeleteMessageCommand(acmeId, Globex))).IsSuccess);
        Assert.True((await sender.Send(new DeleteMessageCommand(acmeId, Acme))).IsSuccess);

        // Reset clears only the addressed tenant.
        await sender.Send(new ResetMessagesCommand(Acme));
        Assert.Empty((await sender.Send(new GetMessagesQuery(Acme))).Value);
        Assert.Single((await sender.Send(new GetMessagesQuery(Globex))).Value);
    }

    [Fact]
    public async Task List_LimitZero_MeansNoCap_NotEmpty()
    {
        using var provider = Host();
        var sender = provider.GetRequiredService<ISender>();
        var sink = provider.GetRequiredService<IMessageSink>();
        sink.Accept(TenantId.Default, Message());
        sink.Accept(TenantId.Default, Message());

        // 0 is "no limit", not "take nothing" — only a positive limit caps the list.
        Assert.Equal(2, (await sender.Send(new GetMessagesQuery(TenantId.Default, Limit: 0))).Value.Count);
    }

    [Fact]
    public async Task MissingMessage_FailsWithTheMessageNotFoundCode()
    {
        using var provider = Host();
        var sender = provider.GetRequiredService<ISender>();

        var get = await sender.Send(new GetMessageQuery(Guid.NewGuid(), TenantId.Default));
        Assert.False(get.IsSuccess);
        Assert.Equal("Message.NotFound", get.Error.Code);
        Assert.NotEmpty(get.Error.Description);

        var delete = await sender.Send(new DeleteMessageCommand(Guid.NewGuid(), TenantId.Default));
        Assert.False(delete.IsSuccess);
        Assert.Equal("Message.NotFound", delete.Error.Code);
        Assert.NotEmpty(delete.Error.Description);
    }

    [Fact]
    public async Task List_Limit_CapsAfterFiltering()
    {
        using var provider = Host();
        var sender = provider.GetRequiredService<ISender>();
        var sink = provider.GetRequiredService<IMessageSink>();
        for (var i = 1; i <= 5; i++)
        {
            sink.Accept(TenantId.Default, Message(subject: $"m{i}"));
        }

        var limited = (await sender.Send(new GetMessagesQuery(TenantId.Default, Limit: 2))).Value;

        Assert.Equal(["m5", "m4"], limited.Select(m => m.Subject));
    }
}

// NOTE: appended by G18e — behavior-directive handlers share this file's host helper.
public sealed class G18eMessageBehaviorHandlerTests
{
    private static ServiceProvider Host() => new ServiceCollection().AddMockifyr().BuildServiceProvider()
;
    private static readonly TenantId Acme = new("acme");

    [Fact]
    public async Task Behaviors_DefaultToNone_SetAndReset_TenantScoped()
    {
        using var provider = Host();
        var sender = provider.GetRequiredService<ISender>();

        Assert.Equal(MessageBehaviors.None, (await sender.Send(new GetMessageBehaviorsQuery(TenantId.Default))).Value);

        var directive = new MessageBehaviors(SmtpFaultMode.Reject, SmtpDelayMs: 100, SmsErrorCode: 21211, WebhookUrl: "http://x/hook");
        Assert.True((await sender.Send(new SetMessageBehaviorsCommand(directive, Acme))).IsSuccess);

        Assert.Equal(directive, (await sender.Send(new GetMessageBehaviorsQuery(Acme))).Value);
        Assert.Equal(MessageBehaviors.None, (await sender.Send(new GetMessageBehaviorsQuery(TenantId.Default))).Value);

        Assert.True((await sender.Send(new ResetMessageBehaviorsCommand(Acme))).IsSuccess);
        Assert.Equal(MessageBehaviors.None, (await sender.Send(new GetMessageBehaviorsQuery(Acme))).Value);
    }

    [Theory]
    [InlineData(-1, null, "MessageBehaviors.InvalidDelay")]
    [InlineData(0, 9999, "MessageBehaviors.InvalidErrorCode")]
    [InlineData(0, 100000, "MessageBehaviors.InvalidErrorCode")]
    public async Task InvalidDirectives_AreRefused(int delayMs, int? errorCode, string expectedCode)
    {
        using var provider = Host();
        var sender = provider.GetRequiredService<ISender>();

        var result = await sender.Send(new SetMessageBehaviorsCommand(
            new MessageBehaviors(SmtpDelayMs: delayMs, SmsErrorCode: errorCode), TenantId.Default));

        Assert.False(result.IsSuccess);
        Assert.Equal(expectedCode, result.Error.Code);
        Assert.NotEmpty(result.Error.Description);
    }

    [Theory]
    [InlineData(10000)]
    [InlineData(99999)]
    public async Task BoundaryErrorCodes_AreAccepted(int code)
    {
        using var provider = Host();
        var sender = provider.GetRequiredService<ISender>();
        Assert.True((await sender.Send(new SetMessageBehaviorsCommand(
            new MessageBehaviors(SmsErrorCode: code), TenantId.Default))).IsSuccess);
    }

    [Fact]
    public async Task ZeroDelay_IsValid()
    {
        using var provider = Host();
        var sender = provider.GetRequiredService<ISender>();
        Assert.True((await sender.Send(new SetMessageBehaviorsCommand(
            new MessageBehaviors(SmtpDelayMs: 0), TenantId.Default))).IsSuccess);
    }
}
