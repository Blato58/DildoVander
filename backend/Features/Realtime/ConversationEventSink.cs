using System.Text;
using System.Text.Json;
using Kufr.Backend.Features.Access;

namespace Kufr.Backend.Features.Realtime;

// Raw application-stream transport only. The caller retains its write semaphore,
// generation predicates and commit-under-gate ordering until WriteAsync completes.
internal abstract class ConversationEventSink
{
    internal virtual void BindOperation(string operationId) { }
    internal virtual Task BeforeNextOperationAsync(CancellationToken token) => Task.CompletedTask;
    internal virtual Task<long> AdvanceOutputAsync(Func<long> transition, CancellationToken token) => Task.FromResult(transition());
    internal abstract bool HasStarted { get; }
    internal virtual CancellationToken Disconnected => CancellationToken.None;
    internal abstract void Start(Guid streamId);
    internal abstract Task WriteAsync(object item, CancellationToken token);
    internal abstract Task ProblemAsync(int status, string code);
    internal abstract void Abort();
}

internal sealed class HttpConversationEventSink(HttpContext context) : ConversationEventSink
{
    internal override bool HasStarted => context.Response.HasStarted;

    internal override void Start(Guid streamId)
    {
        context.Response.ContentType = "application/x-ndjson";
        context.Response.Headers["X-Accel-Buffering"] = "no";
    }

    internal override async Task WriteAsync(object item, CancellationToken token)
    {
        if (item is ConversationOperationBinding) { return; }
        await context.Response.WriteAsync(JsonSerializer.Serialize(item, RealtimeProtocol.JsonOptions) + "\n", Encoding.UTF8, token);
        await context.Response.Body.FlushAsync(token);
    }

    internal override Task ProblemAsync(int status, string code) => ApiProblem.Create(context, status, code).ExecuteAsync(context);
    internal override void Abort() => context.Abort();
}

// An internal transport publication carried through the existing writer and
// generation/visual gates. It adds no HTTP event or provider/domain operation.
internal sealed record ConversationOperationBinding(string OperationId, long ResponseGeneration);
