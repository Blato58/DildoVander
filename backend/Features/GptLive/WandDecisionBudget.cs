using System.Text;
using System.Text.Json;
using Kufr.Backend.Features.Conversations;

namespace Kufr.Backend.Features.GptLive;

// Pricing for the narrow Live decision, independent of the text comedian.
internal static class WandDecisionBudget
{
    internal const string Luna = "gpt-6-luna";
    internal static bool Supports(string model) => model is Luna or ResponsesTextDialogBudget.Model;
    internal static int OutputTokens(string model) => model == Luna ? 512 : 1200;

    internal static long Reservation(WandDecisionInput input, string model) => model switch
    {
        // At most 14k input bytes/tokens + 512 output tokens cost $0.002006.
        // Reserve $0.01; account for all input at the cache-write price.
        Luna => 10_000,
        ResponsesTextDialogBudget.Model => ResponsesTextDialogBudget.ReservedMicroUsd
            + Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(input)) * 5L,
        _ => throw new InvalidOperationException("Unsupported Live decision model.")
    };

    internal static long Price(string returnedModel, string? tier, int inputTokens, int outputTokens, string expectedModel)
    {
        if (!Supports(expectedModel) || returnedModel != expectedModel || tier != "default"
            || inputTokens < 0 || outputTokens < 0)
            throw new TextDialogUnavailableException("Live decision usage cannot be priced.");
        return expectedModel == Luna
            // Standard: cache writes $0.125/M, output $0.50/M. Round up once.
            ? checked((inputTokens + outputTokens * 4L + 7) / 8)
            : ResponsesTextDialogBudget.CalculateUsageMicroUsd(returnedModel, tier, inputTokens, outputTokens, expectedModel);
    }
}
