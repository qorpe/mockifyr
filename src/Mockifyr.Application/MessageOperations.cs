using Mediant.Abstractions;
using Mediant.Results;
using Mockifyr.Core;

namespace Mockifyr.Application;

// Captured-message operations (G18a, ADR 0009): the admin surface over the tenant-scoped message
// inbox. Filters live here — in the management path — so every REST/UI consumer shares one
// definition of "matches", and the store stays a dumb bounded list.

/// <summary>Lists the tenant's captured messages, newest first, optionally filtered.</summary>
public sealed record GetMessagesQuery(
    TenantId Tenant,
    MessageChannel? Channel = null,
    string? Recipient = null,
    string? Contains = null,
    string? Matches = null,
    int? Limit = null) : IQuery<Result<IReadOnlyList<MessageEnvelope>>>;

/// <summary>Counts the tenant's captured messages under the same filters as <see cref="GetMessagesQuery"/>.</summary>
public sealed record CountMessagesQuery(
    TenantId Tenant,
    MessageChannel? Channel = null,
    string? Recipient = null,
    string? Contains = null,
    string? Matches = null) : IQuery<Result<int>>;

/// <summary>
/// Extracts a one-time code (G18f): from one message by <paramref name="Id"/>, or — the e2e shape —
/// from the <b>newest</b> message matching <paramref name="Recipient"/>/<paramref name="Channel"/>.
/// <paramref name="Pattern"/> defaults to 4–8 consecutive digits.
/// </summary>
public sealed record ExtractOtpQuery(
    TenantId Tenant,
    Guid? Id = null,
    string? Recipient = null,
    MessageChannel? Channel = null,
    string? Pattern = null) : IQuery<Result<OtpExtraction>>;

/// <summary>The extracted code and the message it came from.</summary>
public sealed record OtpExtraction(string Otp, Guid MessageId, DateTimeOffset ReceivedAt);

/// <summary>Reads one captured message.</summary>
public sealed record GetMessageQuery(Guid Id, TenantId Tenant) : IQuery<Result<MessageEnvelope>>;

/// <summary>Deletes one captured message.</summary>
public sealed record DeleteMessageCommand(Guid Id, TenantId Tenant) : ICommand<Result>;

/// <summary>Clears the tenant's inbox.</summary>
public sealed record ResetMessagesCommand(TenantId Tenant) : ICommand<Result>;

/// <summary>
/// The one filter definition every message query shares: channel equality, a case-insensitive
/// recipient match (any addressee), a case-insensitive substring over subject + bodies, and a
/// regex over the same text (G18f). A malformed or catastrophic regex matches nothing rather than
/// failing or hanging the admin surface.
/// </summary>
public static class MessageFilter
{
    private static readonly TimeSpan RegexBudget = TimeSpan.FromMilliseconds(250);

    /// <summary>Whether the message passes the given filters (null = don't care).</summary>
    public static bool Matches(
        MessageEnvelope message, MessageChannel? channel, string? recipient, string? contains, string? matches = null)
    {
        if (channel is not null && message.Channel != channel)
        {
            return false;
        }

        if (!string.IsNullOrEmpty(recipient) &&
            !message.To.Any(to => to.Contains(recipient, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        if (!string.IsNullOrEmpty(contains))
        {
            var hit = (message.Subject?.Contains(contains, StringComparison.OrdinalIgnoreCase) ?? false) ||
                      message.Body.Contains(contains, StringComparison.OrdinalIgnoreCase) ||
                      (message.HtmlBody?.Contains(contains, StringComparison.OrdinalIgnoreCase) ?? false);
            if (!hit)
            {
                return false;
            }
        }

        if (!string.IsNullOrEmpty(matches))
        {
            try
            {
                var regex = new System.Text.RegularExpressions.Regex(
                    matches, System.Text.RegularExpressions.RegexOptions.None, RegexBudget);
                var hit = (message.Subject is { } subject && regex.IsMatch(subject)) ||
                          regex.IsMatch(message.Body) ||
                          (message.HtmlBody is { } html && regex.IsMatch(html));
                if (!hit)
                {
                    return false;
                }
            }
            catch (Exception ex) when (ex is ArgumentException or System.Text.RegularExpressions.RegexMatchTimeoutException)
            {
                return false;
            }
        }

        return true;
    }
}

// ---- Behavior directives (G18e) ---------------------------------------------------------------

/// <summary>Reads the tenant's message-channel behavior directives.</summary>
public sealed record GetMessageBehaviorsQuery(TenantId Tenant) : IQuery<Result<MessageBehaviors>>;

/// <summary>Replaces the tenant's message-channel behavior directives.</summary>
public sealed record SetMessageBehaviorsCommand(MessageBehaviors Behaviors, TenantId Tenant) : ICommand<Result>;

/// <summary>Returns the tenant to the no-directives default.</summary>
public sealed record ResetMessageBehaviorsCommand(TenantId Tenant) : ICommand<Result>;
