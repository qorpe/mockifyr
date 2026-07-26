using Mediant.Abstractions;
using Mediant.Results;
using Mockifyr.Core;

namespace Mockifyr.Application;

// Message inbox handlers (G18a). Every handler reads the tenant off the operation and passes it to
// the store, so a request for tenant A can only ever touch tenant A's inbox (ADR 0003).

/// <summary>Lists the tenant's messages, newest first, filtered then capped.</summary>
public sealed class GetMessagesHandler(IMessageStore store)
    : IQueryHandler<GetMessagesQuery, Result<IReadOnlyList<MessageEnvelope>>>
{
    public ValueTask<Result<IReadOnlyList<MessageEnvelope>>> Handle(GetMessagesQuery query, CancellationToken cancellationToken)
    {
        IEnumerable<MessageEnvelope> messages = store.GetMessages(query.Tenant)
            .Where(m => MessageFilter.Matches(m, query.Channel, query.Recipient, query.Contains, query.Matches));
        if (query.Limit is > 0)
        {
            messages = messages.Take(query.Limit.Value);
        }

        return ValueTask.FromResult(Result.Success<IReadOnlyList<MessageEnvelope>>([.. messages]));
    }
}

/// <summary>Counts the tenant's messages under the same filter definition the list uses.</summary>
public sealed class CountMessagesHandler(IMessageStore store)
    : IQueryHandler<CountMessagesQuery, Result<int>>
{
    public ValueTask<Result<int>> Handle(CountMessagesQuery query, CancellationToken cancellationToken) =>
        ValueTask.FromResult(Result.Success(store.GetMessages(query.Tenant)
            .Count(m => MessageFilter.Matches(m, query.Channel, query.Recipient, query.Contains, query.Matches))));
}

/// <summary>Reads one message; NotFound when the id is not in this tenant's inbox.</summary>
public sealed class GetMessageHandler(IMessageStore store)
    : IQueryHandler<GetMessageQuery, Result<MessageEnvelope>>
{
    public ValueTask<Result<MessageEnvelope>> Handle(GetMessageQuery query, CancellationToken cancellationToken) =>
        ValueTask.FromResult(store.Get(query.Tenant, query.Id) is { } message
            ? Result.Success(message)
            : Result.Failure<MessageEnvelope>(Error.NotFound("Message.NotFound", "No such message in this tenant.")));
}

/// <summary>Deletes one message; NotFound when the id is not in this tenant's inbox.</summary>
public sealed class DeleteMessageHandler(IMessageStore store)
    : ICommandHandler<DeleteMessageCommand, Result>
{
    public ValueTask<Result> Handle(DeleteMessageCommand command, CancellationToken cancellationToken) =>
        ValueTask.FromResult(store.Remove(command.Tenant, command.Id)
            ? Result.Success()
            : Result.Failure(Error.NotFound("Message.NotFound", "No such message in this tenant.")));
}

/// <summary>Clears the tenant's inbox.</summary>
public sealed class ResetMessagesHandler(IMessageStore store)
    : ICommandHandler<ResetMessagesCommand, Result>
{
    public ValueTask<Result> Handle(ResetMessagesCommand command, CancellationToken cancellationToken)
    {
        store.Reset(command.Tenant);
        return ValueTask.FromResult(Result.Success());
    }
}

/// <summary>Reads the tenant's behavior directives (the no-directives default when never set).</summary>
public sealed class GetMessageBehaviorsHandler(IMessageBehaviorStore store)
    : IQueryHandler<GetMessageBehaviorsQuery, Result<MessageBehaviors>>
{
    public ValueTask<Result<MessageBehaviors>> Handle(GetMessageBehaviorsQuery query, CancellationToken cancellationToken) =>
        ValueTask.FromResult(Result.Success(store.Get(query.Tenant)));
}

/// <summary>
/// Replaces the tenant's behavior directives. Validation is the useful part: a negative delay or an
/// out-of-range provider error code would be stored and silently misapplied, so both are refused.
/// </summary>
public sealed class SetMessageBehaviorsHandler(IMessageBehaviorStore store)
    : ICommandHandler<SetMessageBehaviorsCommand, Result>
{
    public ValueTask<Result> Handle(SetMessageBehaviorsCommand command, CancellationToken cancellationToken)
    {
        if (command.Behaviors.SmtpDelayMs < 0)
        {
            return ValueTask.FromResult(Result.Failure(Error.Validation(
                "MessageBehaviors.InvalidDelay", "The SMTP delay must be zero or positive.")));
        }

        if (command.Behaviors.SmsErrorCode is < 10000 or > 99999)
        {
            return ValueTask.FromResult(Result.Failure(Error.Validation(
                "MessageBehaviors.InvalidErrorCode", "A Twilio-style error code has five digits.")));
        }

        store.Set(command.Tenant, command.Behaviors);
        return ValueTask.FromResult(Result.Success());
    }
}

/// <summary>Returns the tenant to the no-directives default.</summary>
public sealed class ResetMessageBehaviorsHandler(IMessageBehaviorStore store)
    : ICommandHandler<ResetMessageBehaviorsCommand, Result>
{
    public ValueTask<Result> Handle(ResetMessageBehaviorsCommand command, CancellationToken cancellationToken)
    {
        store.Reset(command.Tenant);
        return ValueTask.FromResult(Result.Success());
    }
}

/// <summary>
/// Extracts a one-time code (G18f) — the "wait for the OTP and read it" step of an e2e test as one
/// query. By id when given; otherwise the newest message matching recipient/channel (the store
/// reads newest-first, so the first hit is the latest). The default pattern is 4–8 consecutive
/// digits; a custom pattern's first match (first group when present) is returned.
/// </summary>
public sealed class ExtractOtpHandler(IMessageStore store)
    : IQueryHandler<ExtractOtpQuery, Result<OtpExtraction>>
{
    /// <summary>The default one-time-code shape: 4–8 consecutive digits on a word boundary.</summary>
    public const string DefaultPattern = @"\b\d{4,8}\b";

    public ValueTask<Result<OtpExtraction>> Handle(ExtractOtpQuery query, CancellationToken cancellationToken)
    {
        var message = query.Id is { } id
            ? store.Get(query.Tenant, id)
            : store.GetMessages(query.Tenant)
                .FirstOrDefault(m => MessageFilter.Matches(m, query.Channel, query.Recipient, contains: null));
        if (message is null)
        {
            return ValueTask.FromResult(Result.Failure<OtpExtraction>(
                Error.NotFound("Message.NotFound", "No such message in this tenant.")));
        }

        System.Text.RegularExpressions.Regex regex;
        try
        {
            regex = new System.Text.RegularExpressions.Regex(
                query.Pattern is { Length: > 0 } pattern ? pattern : DefaultPattern,
                System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromMilliseconds(250));
        }
        catch (ArgumentException)
        {
            return ValueTask.FromResult(Result.Failure<OtpExtraction>(
                Error.Validation("Otp.InvalidPattern", "The pattern is not a valid regular expression.")));
        }

        foreach (var text in new[] { message.Body, message.Subject, message.HtmlBody })
        {
            if (text is null)
            {
                continue;
            }

            try
            {
                if (regex.Match(text) is { Success: true } match)
                {
                    // Groups[1] is a safe non-match when the pattern has no group — no count check needed.
                    var otp = match.Groups[1].Success ? match.Groups[1].Value : match.Value;
                    return ValueTask.FromResult(Result.Success(
                        new OtpExtraction(otp, message.Id, message.ReceivedAt)));
                }
            }
            catch (System.Text.RegularExpressions.RegexMatchTimeoutException)
            {
                // A catastrophic pattern extracts nothing rather than hanging the admin surface.
            }
        }

        return ValueTask.FromResult(Result.Failure<OtpExtraction>(
            Error.NotFound("Otp.NoMatch", "The message carries no text matching the pattern.")));
    }
}
