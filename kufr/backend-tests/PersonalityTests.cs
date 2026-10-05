using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.ClientModel.Primitives;
using Kufr.Backend.Features.Conversations;
using Kufr.Backend.Features.GptLive;
using Kufr.Backend.Features.Realtime;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

#pragma warning disable OPENAI002
internal static class PersonalityTests
{
    internal static async Task RunAsync()
    {
        var persona = PersonalityCatalog.Persona(PersonalityCatalog.OllivanderId);
        Require(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(persona)))
            == "99a72891209ef2bf5ad5a0120a7998c5a08bf46317f38812fb1d3816a5c3099d",
            "the supplied prompt remains byte-for-byte unchanged, including TICH0 and its examples");
        Require(PersonalityCatalog.Persona(PersonalityCatalog.DefaultId) == PartyDialoguePrompt.VoicePersona,
            "existing personality is unchanged");
        Require(PersonalityCatalog.Inventory.Count == 2, "two selectable personalities");

        var request = new RealtimeTurnRequest(Guid.NewGuid(), Guid.NewGuid(), 0, 63, 0,
            PartyWandCatalog.Version, true, "", null, "marin", 1);
        var context = new RealtimeTurnContext(request, true, "", []) { PersonalityId = PersonalityCatalog.OllivanderId };
        using var realtime = JsonDocument.Parse(ModelReaderWriter.Write(RealtimeProtocol.SessionOptions(context)).ToMemory());
        var instructions = realtime.RootElement.GetProperty("instructions").GetString()!;
        Require(instructions.StartsWith(persona, StringComparison.Ordinal)
            && !instructions.Contains(PartyDialoguePrompt.VoicePersona, StringComparison.Ordinal)
            && !instructions.Contains("nenabízej dvě možnosti"), "Realtime uses only the selected personality and its opening");
        using var live = JsonDocument.Parse(JsonSerializer.Serialize(GptLiveProtocol.Start(context)));
        Require(live.RootElement.GetProperty("session").GetProperty("instructions").GetString()!
            .StartsWith(persona, StringComparison.Ordinal), "Live uses the identical selected prompt");
        var newLimit = ResponsesTextDialogBudget.RequestByteLimit("", PersonalityCatalog.OllivanderId);
        Require(newLimit > ResponsesTextDialogBudget.MaxRequestBytes, "longer prompt has an explicit request allowance");
        var maximum = new TextDialogResponse("", "ask", "", null, ResponsesTextDialogBudget.Model, newLimit, 1200, "default");
        Require(2 * ResponsesTextDialogBudget.CalculateActualMicroUsd(maximum)
            <= ResponsesTextDialogBudget.ReserveForContext("", personalityId: PersonalityCatalog.OllivanderId),
            "longer prompt reservation still covers twice the token price");
        using var http = new HttpClient(new PromptHandler(persona));
        await new ResponsesTextDialogClient(http, "test-key", ResponsesTextDialogBudget.Model)
            .GenerateAsync("prostě mi nějakou vyber", 1, [], false, CancellationToken.None,
                personalityId: PersonalityCatalog.OllivanderId);
        await VerifySettingsAndTurnsAsync();
        Console.WriteLine("Personality prompt, provider requests, persistence and conversation policy tests passed.");
    }

    private static async Task VerifySettingsAndTurnsAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"kufr-personalities-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Backend:DatabasePath"] = Path.Combine(directory, "journal.sqlite")
        }).Build();
        try
        {
            using (var journal = new OperationJournal(configuration, TimeProvider.System))
            {
                var calls = 0;
                using var dialog = new TextDialogRunner((text, mask, history, question, token, memory, id) =>
                {
                    calls++;
                    Require(id == PersonalityCatalog.OllivanderId, "selected personality reaches the Sol runner");
                    Require(question == (text.Length == 0), "only opening forces a question; early selection is allowed");
                    return Task.FromResult(new TextDialogResponse("Test", question ? "ask" : "propose", "", question ? null : 1,
                        ResponsesTextDialogBudget.Model, 10, 10, "default"));
                });
                var store = new ConversationStore(TimeProvider.System, journal, dialog);
                var state = store.ReadServerModes();
                Require(state.PersonalityId == PersonalityCatalog.DefaultId, "existing installations default to original");
                Require(store.ChangeServerModes(new(state.Revision, "responses", "fake", "missing")).Error == "invalid_personality",
                    "unknown personality is rejected before publishing any change");
                var changed = store.ChangeServerModes(new(state.Revision, "responses", "fake", PersonalityCatalog.OllivanderId));
                Require(changed.Status == 200 && calls == 0 && changed.State!.Revision != state.Revision,
                    "switching itself makes no AI calls and advances the shared revision");
                Require(store.ChangeServerModes(new(state.Revision, "responses", "fake", PersonalityCatalog.DefaultId)).Status == 409,
                    "stale configuration cannot overwrite selection");
                var create = new CreateConversationRequest("test-device", Guid.NewGuid(), 0, PartyWandCatalog.Version, "cs-CZ", new(true, false));
                var conversation = store.Create(create.DeviceId, Guid.NewGuid(), "create"u8, create).Response!;
                var active = store.ReadServerModes();
                Require(store.ChangeServerModes(new(active.Revision, "responses", "fake", PersonalityCatalog.DefaultId)).Error == "conversations_active",
                    "active conversation freezes the personality");
                Require(store.ChangeServerModes(new(active.Revision, "responses", "fake")).Status == 200,
                    "older client omitting personality retains it and is a valid no-op");
                var opening = new TextTurnRequest(Guid.NewGuid(), create.BootId, 0, "", 1, 0, create.CatalogVersion);
                var accepted = store.SubmitTurn(create.DeviceId, conversation.ConversationId, Guid.NewGuid(), "opening"u8, opening, opening: true);
                Require((await WaitAsync(store, create.DeviceId, accepted.OperationId!)).Decision == "ask", "opening question accepted");
                accepted = store.SubmitTurn(create.DeviceId, conversation.ConversationId, Guid.NewGuid(), "answer"u8,
                    opening with { ClientTurnId = Guid.NewGuid(), Text = "prostě mi nějakou vyber" });
                Require((await WaitAsync(store, create.DeviceId, accepted.OperationId!)).Decision == "propose", "explicit early selection is accepted");
                store.EndConversation(create.DeviceId, conversation.ConversationId, Guid.NewGuid());
                state = store.ReadServerModes();
                store.ChangeServerModes(new(state.Revision, "fake", "fake"));
                create = create with { DeviceId = "debug-server-admin", BootId = Guid.NewGuid() };
                conversation = store.Create(create.DeviceId, Guid.NewGuid(), "fake"u8, create).Response!;
                for (var index = 0; index < 3; index++)
                {
                    var turn = new TextTurnRequest(Guid.NewGuid(), create.BootId, 0, index == 0 ? "" : "Nevím.", 1, 0, create.CatalogVersion);
                    accepted = store.SubmitTurn(create.DeviceId, conversation.ConversationId, Guid.NewGuid(), Encoding.UTF8.GetBytes(index.ToString()), turn, opening: index == 0);
                    Require((await WaitAsync(store, create.DeviceId, accepted.OperationId!)).Decision == (index < 2 ? "ask" : "propose"),
                        "Debug fake completes after the new character's two questions");
                }
            }
            using (var restarted = new OperationJournal(configuration, TimeProvider.System))
            {
                var state = new ConversationStore(TimeProvider.System, restarted).ReadServerModes();
                Require(state.PersonalityId == PersonalityCatalog.OllivanderId && state.PersonalityLifetime == "persistent",
                    "actual database reopen preserves selection");
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task<TurnResult> WaitAsync(ConversationStore store, string device, string id)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (true)
        {
            var operation = store.GetOperation(device, id).Operation;
            if (operation?.Status == "completed") return (TurnResult)operation.Result!;
            Require(operation?.Status is "pending" or "running", "turn did not fail");
            await Task.Delay(10, timeout.Token);
        }
    }

    private sealed class PromptHandler(string persona) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            Require(body.RootElement.GetProperty("instructions").GetString() == persona + "\n\n" + PartyDialoguePrompt.SelectionInstructions,
                "actual SDK Sol request preserves exact new prompt without mixing original");
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new
                {
                    model = ResponsesTextDialogBudget.Model, service_tier = "default", status = "completed",
                    output = new[] { new { type = "function_call", name = "propose_wand", call_id = "test", arguments = "{\"reply\":\"Test\",\"reason\":\"Test\",\"compartment\":1}" } },
                    usage = new { input_tokens = 10, output_tokens = 10 }
                }), Encoding.UTF8, "application/json")
            };
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
