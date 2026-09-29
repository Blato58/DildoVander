using Kufr.Backend.Features.Conversations;

internal static class ResponsesTextDialogBudgetTests
{
    internal static void Run()
    {
        var response = new TextDialogResponse(
            "Odpověď", "ask", "", null, ResponsesTextDialogBudget.Model, 621, 102, "default");
        var actual = ResponsesTextDialogBudget.CalculateActualMicroUsd(response);
        Require(actual == 2573, "all input is charged at the cache-write rate");
        Require(actual < ResponsesTextDialogBudget.ReservedMicroUsd,
            "measured test case fits the pre-call reservation");

        var maximumRequest = response with { InputTokens = 15_200, OutputTokens = 1_200 };
        Require(2 * ResponsesTextDialogBudget.CalculateActualMicroUsd(maximumRequest)
            == ResponsesTextDialogBudget.ReservedMicroUsd,
            "reservation covers Fast rates if the provider disregards the requested Standard tier");
        var serializedRequestCap = response with
        {
            InputTokens = ResponsesTextDialogBudget.MaxRequestBytes,
            OutputTokens = 1_200
        };
        Require(ResponsesTextDialogBudget.CalculateActualMicroUsd(serializedRequestCap)
            <= ResponsesTextDialogBudget.ReservedMicroUsd,
            "serialized request cap stays below the per-call reservation");

        var context = new string('\u0001', 1000) + new string('\u4e00', 3000);
        var withMemory = response with
        {
            InputTokens = ResponsesTextDialogBudget.RequestByteLimit(context),
            OutputTokens = 1200
        };
        Require(ResponsesTextDialogBudget.CalculateActualMicroUsd(withMemory)
            <= ResponsesTextDialogBudget.ReserveForContext(context),
            "escaped Unicode memory fits its pre-call reservation");
        Require(ResponsesTextDialogBudget.ReserveForContext(context)
            > ResponsesTextDialogBudget.ReservedMicroUsd,
            "memory increases reservation before provider dispatch");

        foreach (var tier in new string?[] { null, "fast", "priority", "unknown" })
        {
            try
            {
                ResponsesTextDialogBudget.CalculateActualMicroUsd(response with { ServiceTier = tier });
                throw new Exception("Unconfirmed service tier was priced at Standard rates.");
            }
            catch (TextDialogUnavailableException)
            {
            }
        }

        var wrongModel = response with { ReturnedModel = "other-model" };
        try
        {
            ResponsesTextDialogBudget.CalculateActualMicroUsd(wrongModel);
            throw new Exception("Unknown model was priced as GPT-6.1 Sol.");
        }
        catch (TextDialogUnavailableException)
        {
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new Exception(message);
        }
    }
}
