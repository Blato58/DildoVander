using System.Net;
using System.Text;
using System.Text.Json;
using Kufr.Backend.Features.Conversations;
using Kufr.Backend.Features.Show;

internal static class DuelCommentaryTests
{
    internal static async Task RunAsync()
    {
        var identity = DuelRules.Snapshot("left-id", "Archivář\nPRIVATE-NOT-IN-PROMPT", "wand-1");
        var rounds = Enumerable.Range(1, 3).Select(number => DuelRules.Resolve("fixture-seed", number,
            identity, identity with { IdentityId = "right-id" }, DuelRules.Commit("fixture-seed", number), null, null)).ToArray();
        var view = new DuelView("duel-id", "device-secret", "evening-secret", DuelRules.Version, "autoplay", 5, "Completed",
            identity, identity with { IdentityId = "right-id" }, 3, null, false, false, rounds,
            DuelRules.Result(rounds, false), "server-seed-never-in-prompt");
        var request = DuelCommentaryService.BuildRequest(view);
        Check(!request.Contains("device-secret") && !request.Contains("evening-secret")
            && !request.Contains("server-seed-never-in-prompt") && !request.Contains("\\nPRIVATE"),
            "provider request excludes device/evening/seed and strips control characters");
        using var valid = JsonDocument.Parse(Response(view));
        var accepted = DuelCommentaryService.Parse(valid.RootElement, view);
        Check(accepted.Commentary?.Rounds.Count == 3 && accepted.Audit is { Status: "accepted", EstimatedMicroUsd: 750 },
            "verified structured composition keeps estimated usage audit");
        Check(accepted.Commentary!.Rounds[0].Quip == DuelRules.ApprovedQuips[0], "AI selects approved Czech words only");
        foreach (var response in new[]
        {
            Response(view, winner: "right"), Response(view, leftIdentity: "foreign-id"),
            Response(view, leftScore: 999), Response(view, quip: 6), Response(view, extraProperty: true)
        })
        {
            using var invalid = JsonDocument.Parse(response);
            var rejected = DuelCommentaryService.Parse(invalid.RootElement, view);
            Check(rejected.Commentary is null && rejected.Audit.EstimatedMicroUsd == 750,
                "model cannot change winner, identities, score or narrative vocabulary; rejected usage is still audited");
        }
        using var malformed = JsonDocument.Parse("{}");
        Check(DuelCommentaryService.Parse(malformed.RootElement, view).Commentary is null, "malformed provider response keeps local fallback");
        PersonalizedCommentary(view);
        var handler = new FixtureHandler(Response(view));
        using var http = new HttpClient(handler);
        using var service = new DuelCommentaryService(http, "fixture-not-a-real-provider-key");
        var result = await service.GenerateAsync(view, default);
        Check(result.Commentary is not null && handler.Calls == 1
            && handler.RequestBody?.Contains("narrate_duel") == true, "registered adapter uses actual official Responses SDK protocol without paid calls");
        var failing = new FixtureHandler("not-json", HttpStatusCode.ServiceUnavailable);
        using var failureHttp = new HttpClient(failing);
        using var failureService = new DuelCommentaryService(failureHttp, "fixture-not-a-real-provider-key");
        try { await failureService.GenerateAsync(view, default); }
        catch (Exception) { }
        Check(failing.Calls == 1, "provider failure never hides a second billed retry");
    }

    private static void PersonalizedCommentary(DuelView legacy)
    {
        var view = legacy with { RulesVersion = DuelRules.CharacterVersion,
            Left = legacy.Left with { Temperament = "Teatrální", Affinity = "Záblesky", Weakness = "Ticho", CombatArchetype = "offensive" } };
        var request = DuelCommentaryService.BuildRequest(view);
        Check(request.Contains("flourish") && request.Contains("CombatArchetype") && request.Contains("progression"),
            "v3 narration receives bounded personality, actions and coherent progression");
        string ResponseV3(string action = "strike", string flourish = "Hvězdná magie rozčísla ministerskou paruku.") => JsonSerializer.Serialize(new
        {
            status = "completed", model = ResponsesTextDialogBudget.Model, service_tier = "default",
            usage = new { input_tokens = 100, output_tokens = 50 },
            output = new[] { new { type = "function_call", name = "narrate_duel", arguments = JsonSerializer.Serialize(new
            {
                leftIdentityId = view.Left.IdentityId, rightIdentityId = view.Right.IdentityId, rulesVersion = view.RulesVersion,
                leftPoints = view.Result!.LeftPoints, rightPoints = view.Result.RightPoints, winner = view.Result.Winner,
                rounds = view.Rounds.Select((round, index) => new
                {
                    number = round.Number, leftScore = round.LeftScore, rightScore = round.RightScore,
                    leftAction = action == "strike" ? round.LeftAction : action, rightAction = round.RightAction,
                    flourish, mishap = "Neviditelný zapisovatel převrhl kalamář do vlastní kariéry."
                })
            }) } }
        });
        using var accepted = JsonDocument.Parse(ResponseV3());
        var commentary = DuelCommentaryService.Parse(accepted.RootElement, view).Commentary;
        Check(commentary?.Rounds.All(round => round.Quip.Contains("ministerskou paruku")) == true
            && commentary.Rounds[0].Quip.StartsWith(view.Rounds[0].Quip, StringComparison.Ordinal),
            "original narrative flavor wraps immutable server-owned facts");
        foreach (var response in new[] { ResponseV3("unselected-spell"), ResponseV3(flourish: "GPIO otoč servo 999"), ResponseV3(flourish: "<script>"), ResponseV3(flourish: "Vítězí pravá strana.") })
        {
            using var invalid = JsonDocument.Parse(response);
            Check(DuelCommentaryService.Parse(invalid.RootElement, view).Commentary is null,
                "uncommitted actions and unsafe provider flavor are rejected without altering events");
        }
    }

    private static string Response(DuelView view, string? winner = null, string? leftIdentity = null,
        int? leftScore = null, int quip = 0, bool extraProperty = false)
    {
        var arguments = new Dictionary<string, object?>
        {
            ["leftIdentityId"] = leftIdentity ?? view.Left.IdentityId,
            ["rightIdentityId"] = view.Right.IdentityId, ["rulesVersion"] = view.RulesVersion,
            ["leftPoints"] = view.Result!.LeftPoints, ["rightPoints"] = view.Result.RightPoints,
            ["winner"] = winner ?? view.Result.Winner,
            ["rounds"] = view.Rounds.Select(round => new
            {
                number = round.Number, leftScore = leftScore ?? round.LeftScore,
                rightScore = round.RightScore, quip, mishap = 1
            }).ToArray()
        };
        if (extraProperty) { arguments["overrideScore"] = "attempt"; }
        return JsonSerializer.Serialize(new
        {
            status = "completed", model = ResponsesTextDialogBudget.Model, service_tier = "default",
            usage = new { input_tokens = 100, output_tokens = 50 },
            output = new[] { new { type = "function_call", name = "narrate_duel", arguments = JsonSerializer.Serialize(arguments) } }
        });
    }
    private static void Check(bool condition, string message)
    {
        if (!condition) { throw new InvalidOperationException($"Duel commentary test failed: {message}"); }
    }
    private sealed class FixtureHandler(string response, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        internal int Calls;
        internal string? RequestBody;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            RequestBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new(status) { Content = new StringContent(response, Encoding.UTF8, "application/json") };
        }
    }
}
