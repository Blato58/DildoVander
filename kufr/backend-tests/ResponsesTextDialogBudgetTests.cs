using Kufr.Backend.Features.Conversations;

internal static class ResponsesTextDialogBudgetTests
{
    internal static void Run()
    {
        var response = new TextDialogResponse(
            "Odpověď", "ask", "", null, ResponsesTextDialogBudget.Model, 621, 102);
        var actual = ResponsesTextDialogBudget.CalculateActualMicroUsd(response);
        Require(actual == 2573, "all input is charged at the cache-write rate");
        Require(actual < ResponsesTextDialogBudget.ReservedMicroUsd,
            "measured test case fits the pre-call reservation");

        var maximumRequest = response with { InputTokens = 15_200, OutputTokens = 1_200 };
        Require(ResponsesTextDialogBudget.CalculateActualMicroUsd(maximumRequest)
            == ResponsesTextDialogBudget.ReservedMicroUsd,
            "reservation covers the documented output cap and generous input allowance");
        var serializedRequestCap = response with
        {
            InputTokens = ResponsesTextDialogBudget.MaxRequestBytes,
            OutputTokens = 1_200
        };
        Require(ResponsesTextDialogBudget.CalculateActualMicroUsd(serializedRequestCap)
            <= ResponsesTextDialogBudget.ReservedMicroUsd,
            "serialized request cap stays below the per-call reservation");

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
