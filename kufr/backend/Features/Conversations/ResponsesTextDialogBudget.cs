using System.Text;
using System.Text.Json;

namespace Kufr.Backend.Features.Conversations;

internal static class ResponsesTextDialogBudget
{
    internal const string Model = "gpt-6.1-sol";
    internal const int MinimumQuestions = 4;
    internal const int MaxRequestBytes = 14_000;

    // The 14 kB serialized request cap bounds input below 14,000 tokens;
    // 1,200 output tokens fit under $0.05 at Standard Sol rates. Reserve
    // twice that amount so an unexpected Fast response can retain its full cost.
    internal const long ReservedMicroUsd = 100_000;

    internal static int RequestByteLimit(string context, string personalityId = PersonalityCatalog.DefaultId) =>
        MaxRequestBytes + ContextBytes(context) + PersonalityCatalog.ExtraRequestBytes(personalityId);

    internal static bool IsSupportedModel(string model) => model == Model;

    internal static long ReserveForContext(string context, string model = Model, string personalityId = PersonalityCatalog.DefaultId)
    {
        var solReservation = checked(ReservedMicroUsd + (ContextBytes(context) + PersonalityCatalog.ExtraRequestBytes(personalityId)) * 5L);
        return model switch
        {
            Model => solReservation,
            _ => throw new InvalidOperationException("Only Sol is supported.")
        };
    }

    private static int ContextBytes(string context) => context.Length == 0
        ? 0
        : Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(context));

    internal static long CalculateActualMicroUsd(TextDialogResponse response, string expectedModel = Model)
    {
        if (!IsSupportedModel(expectedModel)
            || response.ReturnedModel != expectedModel
            || response.ServiceTier != "default"
            || response.InputTokens < 0
            || response.OutputTokens < 0)
        {
            throw new TextDialogUnavailableException("Provider usage cannot be priced.");
        }

        // This is a conservative list-price estimate, not verified account billing.
        // Standard processing: cache writes cost $2.50 and output costs $10
        // per million tokens. Charging all input at the cache-write rate is
        // conservative even when the provider reports cached input tokens.
        return expectedModel switch
        {
            Model => checked((response.InputTokens * 5L + 1) / 2 + response.OutputTokens * 10L),
            _ => throw new TextDialogUnavailableException("Provider model cannot be priced.")
        };
    }
}
