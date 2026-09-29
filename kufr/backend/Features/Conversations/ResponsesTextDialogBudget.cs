namespace Kufr.Backend.Features.Conversations;

internal static class ResponsesTextDialogBudget
{
    internal const string Model = "gpt-6.1-sol";

    // The request caps output at 1,200 tokens. This reserves another 15,200
    // input tokens at the cache-write rate before any paid call is started.
    internal const long ReservedMicroUsd = 50_000;

    internal static long CalculateActualMicroUsd(TextDialogResponse response)
    {
        if (response.ReturnedModel != Model
            || response.InputTokens < 0
            || response.OutputTokens < 0)
        {
            throw new TextDialogUnavailableException("Provider usage cannot be priced.");
        }

        // Standard processing: cache writes cost $2.50 and output costs $10
        // per million tokens. Charging all input at the cache-write rate is
        // conservative even when the provider reports cached input tokens.
        return checked((response.InputTokens * 5L + 1) / 2
            + response.OutputTokens * 10L);
    }
}
