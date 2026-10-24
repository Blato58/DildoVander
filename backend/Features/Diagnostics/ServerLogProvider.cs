namespace Kufr.Backend.Features.Diagnostics;

// Capture only known safe application events. Framework/provider exception
// messages, scopes, formatted messages and unrecognized state are excluded.
internal sealed class ServerLogProvider(ServerLogStore store) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new SafeLogger(store, categoryName);
    public void Dispose() { }

    private sealed class SafeLogger(ServerLogStore store, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => level >= LogLevel.Warning && category is
            "Kufr.Backend.Features.Conversations.ConversationStore" or
            "Kufr.Backend.Features.TestingSessions.TestingCharacterEnrichmentWorker" or
            "Kufr.Backend.Features.Customers.CharacterEnrichmentWorker";
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(level) || state is not IEnumerable<KeyValuePair<string, object?>> values) return;
            var fields = values.ToDictionary(pair => pair.Key, pair => pair.Value);
            var code = (fields.GetValueOrDefault("{OriginalFormat}") as string) switch
            {
                "Text dialog failed for operation {OperationId}." => "dialog_failed",
                "Could not record failure for operation {OperationId}." => "dialog_journal_failed",
                "Voice provider failed for operation {OperationId}." => "voice_failed",
                "Could not record voice failure for operation {OperationId}." => "voice_journal_failed",
                "Testing character enrichment worker failed ({ErrorType})." => "testing_character_worker_failed",
                "Character worker operation failed ({ErrorType})." => "character_worker_failed",
                _ => null
            };
            if (code is null) return;
            var operation = Guid.TryParse(fields.GetValueOrDefault("OperationId")?.ToString(), out var id) ? id.ToString() : null;
            store.Write(level >= LogLevel.Error ? "error" : "warning", code, operationId: operation);
        }
    }
}
