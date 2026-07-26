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
            .Where(m => MessageFilter.Matches(m, query.Channel, query.Recipient, query.Contains));
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
            .Count(m => MessageFilter.Matches(m, query.Channel, query.Recipient, query.Contains))));
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
