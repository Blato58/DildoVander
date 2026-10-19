using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Kufr.Backend.Features.Conversations;
using Kufr.Backend.Features.GptLive;
using Kufr.Backend.Features.Realtime;
using Kufr.Backend.Features.VoiceTurns;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.Http;

internal static class CostumeImageTests
{
    internal static byte[] Jpeg() => Convert.FromBase64String("/9j/4AAQSkZJRgABAQAAAQABAAD/2wBDAAgGBgcGBQgHBwcJCQgKDBQNDAsLDBkSEw8UHRofHh0aHBwgJC4nICIsIxwcKDcpLDAxNDQ0Hyc5PTgyPC4zNDL/2wBDAQkJCQwLDBgNDRgyIRwhMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjL/wAARCAACAAIDASIAAhEBAxEB/8QAHwAAAQUBAQEBAQEAAAAAAAAAAAECAwQFBgcICQoL/8QAtRAAAgEDAwIEAwUFBAQAAAF9AQIDAAQRBRIhMUEGE1FhByJxFDKBkaEII0KxwRVS0fAkM2JyggkKFhcYGRolJicoKSo0NTY3ODk6Q0RFRkdISUpTVFVWV1hZWmNkZWZnaGlqc3R1dnd4eXqDhIWGh4iJipKTlJWWl5iZmqKjpKWmp6ipqrKztLW2t7i5usLDxMXGx8jJytLT1NXW19jZ2uHi4+Tl5ufo6erx8vP09fb3+Pn6/8QAHwEAAwEBAQEBAQEBAQAAAAAAAAECAwQFBgcICQoL/8QAtREAAgECBAQDBAcFBAQAAQJ3AAECAxEEBSExBhJBUQdhcRMiMoEIFEKRobHBCSMzUvAVYnLRChYkNOEl8RcYGRomJygpKjU2Nzg5OkNERUZHSElKU1RVVldYWVpjZGVmZ2hpanN0dXZ3eHl6goOEhYaHiImKkpOUlZaXmJmaoqOkpaanqKmqsrO0tba3uLm6wsPExcbHyMnK0tPU1dbX2Nna4uPk5ebn6Onq8vP09fb3+Pn6/9oADAMBAAIRAxEAPwD5/ooooA//2Q==");

    internal static async Task RunAsync()
    {
        await ConversationMediaLifecycleTests.RunAsync();
        Check(CostumeJpeg.IsValid(Jpeg()), "valid JPEG fixture");
        Check(!CostumeJpeg.IsValid([0xff, 0xd8, 0xff, 0xd9]), "truncated JPEG");
        Check(!CostumeJpeg.IsValid(new byte[CostumeJpeg.MaximumBytes + 1]), "oversized JPEG");
        var large = Jpeg();
        var frame = Array.FindIndex(large, value => value == 0xc0);
        large[frame + 6] = 3; // width 770 exceeds 640.
        Check(!CostumeJpeg.IsValid(large), "dimension limit");
        var metadata = Jpeg(); metadata[3] = 0xe1;
        Check(!CostumeJpeg.IsValid(metadata), "EXIF forbidden");
        await ProviderAsync();
        var timeoutContext = new DefaultHttpContext();
        var stalled = new StalledReadStream();
        timeoutContext.Request.Body = stalled;
        var timedOut = false;
        try { await CostumeImageEndpoints.ReadBodyAsync(timeoutContext, 100, TimeSpan.FromMilliseconds(20)); }
        catch (OperationCanceledException) { timedOut = true; }
        Check(timedOut && stalled.Buffer.Span.IndexOfAnyExcept((byte)0) < 0, "stalled upload expires and clears owned buffer");
        var rejected = new DefaultHttpContext();
        rejected.Request.Protocol = "HTTP/1.1";
        rejected.Request.ContentLength = 101;
        rejected.Request.Body = new MemoryStream(new byte[101]);
        Check(await CostumeImageEndpoints.ReadBodyAsync(rejected, 100) is null
            && rejected.Request.Body.Position == 0 && rejected.Response.Headers.Connection == "close",
            "declared oversized HTTP/1 body is bounded and explicitly closes connection");
        var chunked = new DefaultHttpContext();
        chunked.Request.Protocol = "HTTP/1.1";
        chunked.Request.Body = new MemoryStream(new byte[101]);
        Check(await CostumeImageEndpoints.ReadBodyAsync(chunked, 100) is null
            && chunked.Response.Headers.Connection == "close", "chunked overflow closes HTTP/1 connection");
        rejected.Request.Protocol = "HTTP/2";
        rejected.Response.Headers.Connection = default;
        Check(await CostumeImageEndpoints.ReadBodyAsync(rejected, 100) is null
            && !rejected.Response.Headers.ContainsKey("Connection"), "HTTP/2 has no forbidden connection header");
        await StoreAsync();
        await ClearCostumeAsync();
        await ImageCloseBarrierAsync();
        Console.WriteLine("Costume image tests passed.");
    }

    private static object ProviderBody() => new
    {
        model = ResponsesTextDialogBudget.Model, status = "completed", service_tier = "default",
        output = new[] { new { type = "message", role = "assistant", status = "completed",
            content = new[] { new { type = "output_text", text = JsonSerializer.Serialize(new {
                description = "green cloak", possibleCharacter = "forest wizard", uncertainty = "high" }) } } } },
        usage = new { input_tokens = 100, output_tokens = 20 }
    };

    private static async Task ProviderAsync()
    {
        var requests = 0;
        using var http = new HttpClient(new Handler(async (request, token) =>
        {
            requests++;
            Check(request.RequestUri!.AbsoluteUri == "https://api.openai.com/v1/responses", "fixed SDK URL");
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            var root = body.RootElement;
            Check(!root.GetProperty("store").GetBoolean(), "provider store disabled");
            var image = root.GetProperty("input")[0].GetProperty("content")[1];
            Check(image.GetProperty("detail").GetString() == "low", "low detail");
            Check(image.GetProperty("image_url").GetString() == "data:image/jpeg;base64," + Convert.ToBase64String(Jpeg()),
                "actual JPEG sent through SDK");
            Check(root.GetProperty("text").GetProperty("format").GetProperty("strict").GetBoolean(), "strict output schema");
            Check(root.GetProperty("text").GetProperty("format").GetProperty("schema")
                .GetProperty("properties").GetProperty("description").GetProperty("minLength").GetInt32() == 1,
                "provider schema requires a nonempty photo description");
            return Response(ProviderBody());
        }));
        var result = await new CostumeImageClient(http, "fixture-key", ResponsesTextDialogBudget.Model).AnalyzeAsync(Jpeg(), default);
        Check(result.Description == "green cloak" && result.CostMicroUsd == 450 && requests == 1, "description and usage priced");
        foreach (var mutation in new Action<JsonObject>[] {
            root => root["status"] = "incomplete",
            root => root["model"] = "unpriced-model",
            root => root.Remove("usage"),
            root => root["output"]![0]!["content"]![0]!["text"] = "{\"description\":\"\",\"possibleCharacter\":\"\",\"uncertainty\":\"high\"}",
            root => root["output"]![0]!["content"]![0]!["text"] = "{\"description\":\"x\",\"possibleCharacter\":\"\",\"uncertainty\":\"high\",\"compartment\":1}",
            root => root["output"]![0]!["content"]![0]!["text"] = "{\"description\":\"x\",\"possibleCharacter\":\"\",\"uncertainty\":\"certain\"}"
        })
        {
            var invalid = JsonSerializer.SerializeToNode(ProviderBody())!.AsObject(); mutation(invalid);
            using var document = JsonDocument.Parse(invalid.ToJsonString());
            var rejected = false;
            try { CostumeImageClient.Parse(document.RootElement, ResponsesTextDialogBudget.Model); }
            catch (Exception) { rejected = true; }
            Check(rejected, "invalid/unpriced provider output rejected");
        }
        var errors = 0;
        using var failing = new HttpClient(new Handler((_, _) => {
            errors++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)); }));
        try { await new CostumeImageClient(failing, "fixture", ResponsesTextDialogBudget.Model).AnalyzeAsync(Jpeg(), default); }
        catch (Exception) { }
        Check(errors == 1, "no paid retry");
        foreach (var description in new[] { "x", new string('x', 240) })
        {
            var boundary = JsonSerializer.SerializeToNode(ProviderBody())!.AsObject();
            boundary["output"]![0]!["content"]![0]!["text"] = JsonSerializer.Serialize(new {
                description, possibleCharacter = "", uncertainty = "high" });
            using var document = JsonDocument.Parse(boundary.ToJsonString());
            Check(CostumeImageClient.Parse(document.RootElement, ResponsesTextDialogBudget.Model).Description == description,
                "nonempty photo description boundaries accepted");
        }
        using var cancelledHttp = new HttpClient(new Handler(async (_, token) => {
            await Task.Delay(Timeout.Infinite, token); return Response(ProviderBody()); }));
        using var cancelled = new CancellationTokenSource(TimeSpan.FromMilliseconds(20));
        var cancelledCall = false;
        try { await new CostumeImageClient(cancelledHttp, "fixture", ResponsesTextDialogBudget.Model).AnalyzeAsync(Jpeg(), cancelled.Token); }
        catch (OperationCanceledException) { cancelledCall = true; }
        Check(cancelledCall, "provider cancellation propagates without retry");

        var cue = new CostumeContext("green cloak", "forest wizard", "high", "photo", DateTimeOffset.UtcNow.AddMinutes(2)).Prompt;
        var requestTurn = new RealtimeTurnRequest(Guid.NewGuid(), Guid.NewGuid(), 1, 63, 1, "catalog", true, "", null, "marin", 1);
        var turn = new RealtimeTurnContext(requestTurn, true, "", []) { CostumeContext = cue };
        Check(RealtimeProtocol.SessionOptions(turn).Instructions.Contains("green cloak"), "Realtime opening receives costume");
        Check(JsonSerializer.Serialize(GptLiveProtocol.Start(turn)).Contains("green cloak"), "delegated Live opening receives costume");
        turn = turn with { LivePrompt = LivePromptSnapshot.Create(PersonalityCatalog.DefaultId, new() { Mode = LiveActorMode.AutonomousActor }) };
        Check(JsonSerializer.Serialize(GptLiveProtocol.AutonomousStart(turn, "test")).Contains("green cloak"), "autonomous Live receives costume");
        var decisionInput = new WandDecisionInput(1, 63, 1, [], turn.LivePrompt!.Policy, 1, false, CostumeContext: cue);
        Check(WandDecisionService.BuildRequest(decisionInput).Contains("green cloak"), "Sol decision receives separate visual data");
        await MaximumUnicodeContextAsync();
    }

    private static async Task MaximumUnicodeContextAsync()
    {
        var descriptions = new[] {
            new string('\u011b', 240), new string('"', 240), new string('\\', 240),
            string.Concat(Enumerable.Repeat("\U0001f600", 120))
        };
        foreach (var description in descriptions)
        {
            foreach (var source in new[] { "manual", "photo" })
            {
                var character = source == "photo" ? description[..80] : "";
                var context = new CostumeContext(description, character, "high", source, DateTimeOffset.UtcNow.AddMinutes(2));
                var cue = context.Prompt;
                Check(cue.Length <= CostumeContext.MaximumPromptCharacters, "all valid cue fields fit the derived serialization bound");
                var request = new RealtimeTurnRequest(Guid.NewGuid(), Guid.NewGuid(), 1, 63, 1,
                    "catalog", true, "", null, "marin", 1);
                var input = RealtimeProtocol.SessionOptions(new RealtimeTurnContext(request, true, "", [])
                    { CostumeContext = cue }).Instructions;
                Check(input.Contains(cue, StringComparison.Ordinal), "maximum Unicode cue reaches retained Realtime without truncation");
                using var visual = JsonDocument.Parse(cue[cue.IndexOf('{')..]);
                Check(visual.RootElement.GetProperty("Description").GetString() == description
                    && visual.RootElement.GetProperty("PossibleCharacter").GetString() == character,
                    "maximum Unicode and escaped descriptions preserve evidence");
                await Task.CompletedTask;
            }
        }
    }

    private static async Task StoreAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), "kufr-image-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var clock = new Clock();
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> {
                ["Backend:DatabasePath"] = Path.Combine(directory, "journal.sqlite"),
                ["Backend:Usage:SessionCapMicroUsd"] = "1" }).Build();
            using var journal = new OperationJournal(configuration, clock);
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var finish = new TaskCompletionSource<CostumeImageResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
            var calls = 0;
            byte[]? ownedBytes = null;
            string? prompt = null;
            using var runner = new TextDialogRunner((_, _, _, _, _, context, _) => {
                prompt = context; return Task.FromResult(new TextDialogResponse("Question?", "ask", "", null, ResponsesTextDialogBudget.Model, 10, 10, "default"));
            }, (jpeg, _) => { calls++; ownedBytes = jpeg; started.TrySetResult(); return finish.Task; });
            var store = new ConversationStore(clock, journal, runner);
            var boot = Guid.NewGuid();
            string Create(string device, bool consent) => store.Create(device, Guid.NewGuid(), Encoding.UTF8.GetBytes(device),
                new(device, boot, 1, "catalog", "cs-CZ", new(false, consent))).Response!.ConversationId;
            var id = Create("a", true);
            var noConsent = Create("b", false);
            var upload = new CostumeImageUpload(Guid.NewGuid(), boot, 1, 1, "catalog", Jpeg());
            Check(store.SubmitImage("b", noConsent, Guid.NewGuid(), upload).Status == 403, "image consent enforced");
            Check(store.SubmitImage("b", id, Guid.NewGuid(), upload).Status == 403, "device scope enforced");
            Check(store.SubmitImage("a", id, Guid.NewGuid(), upload with { Generation = 2 }).Status == 409, "generation enforced");
            var key = Guid.NewGuid();
            var accepted = store.SubmitImage("a", id, key, upload);
            Check(accepted.Status == 202, "image accepted");
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check(store.SubmitImage("a", id, key, upload).OperationId == accepted.OperationId, "same image idempotency");
            Check(store.SubmitImage("a", id, key, upload with { DeviceRevision = 2 }).Status == 409, "correlation included in hash");
            Check(store.SubmitImage("a", id, Guid.NewGuid(), upload).Status == 409, "single photo per conversation");
            var correctionKey = Guid.NewGuid();
            var corrected = store.CorrectCostume("a", id, correctionKey, upload, "red cloak");
            Check(corrected.Status == 200, "manual correction accepted during image work");
            Check(store.CorrectCostume("a", id, correctionKey, upload, "blue cloak").Status == 409, "manual idempotency conflict");
            finish.SetResult(new("green cloak", "forest wizard", "high", 450));
            await WaitAsync(() => store.GetOperation("a", accepted.OperationId!).Operation?.Result is CostumeContext);
            Check(((CostumeContext)store.GetOperation("a", accepted.OperationId!).Operation!.Result!).Description == "red cloak",
                "manual correction overrides late photo");
            await WaitAsync(() => ownedBytes!.All(value => value == 0));
            Check(calls == 1 && journal.ReadSessionCharge(id) == 450, "one provider call charged once");
            var text = new TextTurnRequest(Guid.NewGuid(), boot, 1, "", 63, 1, "catalog");
            var turn = store.SubmitTurn("a", id, Guid.NewGuid(), "opening"u8, text, true);
            await WaitAsync(() => store.GetOperation("a", turn.OperationId!).Operation?.Result is TurnResult);
            Check(prompt!.Contains("red cloak") && !prompt.Contains("green cloak"), "manual context reaches next turn");
            var other = store.SubmitTurn("b", noConsent, Guid.NewGuid(), "other"u8, text, true);
            await WaitAsync(() => store.GetOperation("b", other.OperationId!).Operation?.Result is TurnResult);
            Check(!prompt!.Contains("cloak"), "context never crosses conversations");
            clock.Now = clock.Now.AddSeconds(121);
            Check(store.ReadVisualContext("a", id).Context.Contains("red cloak"), "active visual context survives old two-minute expiry");
            using (var audit = new SqliteConnection("Data Source=" + Path.Combine(directory, "journal.sqlite")))
            {
                audit.Open();
                using var command = audit.CreateCommand(); command.CommandText = "SELECT * FROM turn_operations";
                using var records = command.ExecuteReader();
                while (records.Read())
                {
                    for (var column = 0; column < records.FieldCount; column++)
                    {
                        Check(!records.GetValue(column).ToString()!.Contains("red cloak"), "descriptor not persisted in journal");
                    }
                }
            }

            // Stop must suppress a provider result that ignores cancellation.
            var lateFinish = new TaskCompletionSource<CostumeImageResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
            var lateStart = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var lateRunner = new TextDialogRunner((_,_,_,_,_,_,_) => throw new Exception(),
                (_, _) => { lateStart.TrySetResult(); return lateFinish.Task; });
            var lateStore = new ConversationStore(clock, journal, lateRunner);
            var lateId = lateStore.Create("late", Guid.NewGuid(), "late"u8,
                new("late", boot, 1, "catalog", "cs-CZ", new(false,true))).Response!.ConversationId;
            var late = lateStore.SubmitImage("late", lateId, Guid.NewGuid(), upload);
            await lateStart.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check(lateStore.EndConversation("late", lateId, Guid.NewGuid()).Status == 204, "Stop during image");
            lateFinish.SetResult(new("late secret", "", "high", 100));
            await WaitAsync(() => lateRunner.TryAcquire()); lateRunner.Release();
            Check(lateStore.GetOperation("late", late.OperationId!).Operation?.Result is null, "late image never published after Stop");
            Check(journal.ReadSessionCharge(lateId) == 100, "late usage still settled");

            // Both fake and paid uploads must respect a correction made first.
            foreach (var paid in new[] { false, true })
            {
                using var precedenceRunner = new TextDialogRunner((_,_,_,_,_,context,_) => {
                    prompt = context;
                    return Task.FromResult(new TextDialogResponse("Question?", "ask", "", null, ResponsesTextDialogBudget.Model, 1, 1, "default"));
                }, (_, _) => Task.FromResult(new CostumeImageResponse("photo draft", "", "high", 1)));
                precedenceRunner.SetEnabled(paid);
                var precedenceStore = new ConversationStore(clock, journal, precedenceRunner);
                var device = "precedence-" + paid;
                var precedenceId = precedenceStore.Create(device, Guid.NewGuid(), Encoding.UTF8.GetBytes(device),
                    new(device, boot, 1, "catalog", "cs-CZ", new(false,true))).Response!.ConversationId;
                precedenceStore.CorrectCostume(device, precedenceId, Guid.NewGuid(), upload, "manual first");
                var image = precedenceStore.SubmitImage(device, precedenceId, Guid.NewGuid(), upload);
                await WaitAsync(() => precedenceStore.GetOperation(device, image.OperationId!).Operation?.Result is CostumeContext);
                Check(((CostumeContext)precedenceStore.GetOperation(device, image.OperationId!).Operation!.Result!).Description == "manual first",
                    "manual-before-photo precedence in fake and paid modes");
            }

            using var discardRunner = new TextDialogRunner((_,_,_,_,_,context,_) => {
                prompt = context;
                return Task.FromResult(new TextDialogResponse("Question?", "ask", "", null, ResponsesTextDialogBudget.Model, 1, 1, "default"));
            }, (_, _) => Task.FromResult(new CostumeImageResponse("discard secret", "", "high", 1)));
            var discardStore = new ConversationStore(clock, journal, discardRunner);
            var discardId = discardStore.Create("discard", Guid.NewGuid(), "discard"u8,
                new("discard", boot, 1, "catalog", "cs-CZ", new(false,true))).Response!.ConversationId;
            var discardedImage = discardStore.SubmitImage("discard", discardId, Guid.NewGuid(), upload);
            await WaitAsync(() => discardStore.GetOperation("discard", discardedImage.OperationId!).Operation?.Result is CostumeContext);
            await WaitAsync(() => discardRunner.TryAcquire()); discardRunner.Release();
            discardStore.CancelOperation("discard", discardedImage.OperationId!, Guid.NewGuid());
            var discardTurn = discardStore.SubmitTurn("discard", discardId, Guid.NewGuid(), "discard-opening"u8, text, true);
            await WaitAsync(() => discardStore.GetOperation("discard", discardTurn.OperationId!).Operation?.Result is TurnResult);
            Check(!prompt!.Contains("discard secret"), "completed image discard clears future dialogue cue");

            using var failedRunner = new TextDialogRunner((_,_,_,_,_,_,_) =>
                Task.FromResult(new TextDialogResponse("Question?", "ask", "", null, ResponsesTextDialogBudget.Model, 1, 1, "default")),
                (_, _) => throw new InvalidDataException("fixture image failure"));
            var failedStore = new ConversationStore(clock, journal, failedRunner);
            var failedId = failedStore.Create("failed-image", Guid.NewGuid(), "failed-image"u8,
                new("failed-image", boot, 1, "catalog", "cs-CZ", new(false,true))).Response!.ConversationId;
            var failedImage = failedStore.SubmitImage("failed-image", failedId, Guid.NewGuid(), upload);
            await WaitAsync(() => failedStore.GetOperation("failed-image", failedImage.OperationId!).Operation?.Status == "failed");
            await WaitAsync(() => failedRunner.TryAcquire()); failedRunner.Release();
            var afterFailure = failedStore.SubmitTurn("failed-image", failedId, Guid.NewGuid(), "after-failed-image"u8, text, true);
            await WaitAsync(() => failedStore.GetOperation("failed-image", afterFailure.OperationId!).Operation?.Result is TurnResult);
            Check(afterFailure.Status == 202, "optional image failure leaves dialogue usable");

            var expiredStore = new ConversationStore(clock, journal);
            var expiredId = expiredStore.Create("expired-manual", Guid.NewGuid(), "expired-manual"u8,
                new("expired-manual", boot, 1, "catalog", "cs-CZ", new(false,true))).Response!.ConversationId;
            expiredStore.CorrectCostume("expired-manual", expiredId, Guid.NewGuid(), upload, "expired correction");
            clock.Now = clock.Now.AddSeconds(121);
            var expiredPhoto = expiredStore.SubmitImage("expired-manual", expiredId, Guid.NewGuid(), upload);
            Check(expiredPhoto.Status == 202 && store.ReadVisualContext("expired-manual", expiredId).Context.Length == 0
                && expiredStore.ReadVisualContext("expired-manual", expiredId).Context.Contains("expired correction"),
                "active manual cue survives the retired two-minute expiry without crossing stores");

            var expiryStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var expiryFinish = new TaskCompletionSource<CostumeImageResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var expiryRunner = new TextDialogRunner((_,_,_,_,_,_,_) => throw new Exception(),
                (_, _) => { expiryStarted.TrySetResult(); return expiryFinish.Task; });
            var expiryStore = new ConversationStore(clock, journal, expiryRunner);
            var expiryId = expiryStore.Create("expired-pending", Guid.NewGuid(), "expired-pending"u8,
                new("expired-pending", boot, 1, "catalog", "cs-CZ", new(false,true))).Response!.ConversationId;
            var pendingPhoto = expiryStore.SubmitImage("expired-pending", expiryId, Guid.NewGuid(), upload);
            await expiryStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            expiryStore.CorrectCostume("expired-pending", expiryId, Guid.NewGuid(), upload, "manual while pending");
            clock.Now = clock.Now.AddSeconds(121);
            expiryFinish.TrySetResult(new("late draft", "", "high", 17));
            await WaitAsync(() => expiryStore.GetOperation("expired-pending", pendingPhoto.OperationId!).Operation?.Status == "completed");
            await WaitAsync(() => expiryRunner.TryAcquire()); expiryRunner.Release();
            Check(expiryStore.GetOperation("expired-pending", pendingPhoto.OperationId!).Operation?.Result is CostumeContext { Description: "manual while pending" }
                && journal.ReadSessionCharge(expiryId) == 17, "manual correction remains authoritative beyond retired expiry with usage settled");
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(directory, recursive: true); }
    }

    private static async Task ClearCostumeAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), "kufr-image-clear-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var clock = new Clock();
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
                ["Backend:DatabasePath"] = Path.Combine(directory, "journal.sqlite") }).Build();
            using var journal = new OperationJournal(configuration, clock);
            var boot = Guid.NewGuid();
            var upload = new CostumeImageUpload(Guid.NewGuid(), boot, 1, 1, "catalog", Jpeg());
            foreach (var stage in new[] { "before", "pending", "completed" })
            {
                var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var finish = new TaskCompletionSource<CostumeImageResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
                byte[]? ownedBytes = null;
                string? prompt = null;
                var calls = 0;
                using var runner = new TextDialogRunner((_, _, _, _, _, context, _) => {
                    prompt = context;
                    return Task.FromResult(new TextDialogResponse("Question?", "ask", "", null, ResponsesTextDialogBudget.Model, 1, 1, "default"));
                }, (jpeg, _) => {
                    calls++;
                    ownedBytes = jpeg;
                    started.TrySetResult();
                    return finish.Task;
                });
                var store = new ConversationStore(clock, journal, runner);
                var id = store.Create(stage, Guid.NewGuid(), Encoding.UTF8.GetBytes(stage),
                    new(stage, boot, 1, "catalog", "cs-CZ", new(true, true))).Response!.ConversationId;
                SubmitTurnResult? image = null;
                if (stage != "before")
                {
                    image = store.SubmitImage(stage, id, Guid.NewGuid(), upload);
                    await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    if (stage == "completed")
                    {
                        finish.SetResult(new("photo secret", "", "high", 17));
                        await WaitAsync(() => store.GetOperation(stage, image.OperationId!).Operation?.Status == "completed");
                        await WaitAsync(() => ownedBytes!.All(value => value == 0));
                    }
                }
                store.CorrectCostume(stage, id, Guid.NewGuid(), upload, "manual secret");
                var clearKey = Guid.NewGuid();
                var clear = store.CorrectCostume(stage, id, clearKey, upload, "");
                Check(clear.Status == 200 && clear.Operation is { Kind: "costume", Status: "completed",
                    Result: CostumeContext { Source: "manual", Description: "" } }, "clear returns compatible manual acknowledgement");
                Check(store.CorrectCostume(stage, id, clearKey, upload, "").Operation == clear.Operation,
                    "clear replay retains the same acknowledgement");
                Check(store.CorrectCostume(stage, id, clearKey, upload, "changed").Status == 409,
                    "clear replay cannot change its description");
                if (image is not null)
                {
                    Check(store.GetOperation(stage, image.OperationId!).Operation is { Status: "cancelled", Result: null },
                        "clear suppresses pending and completed image polls without a completed-null result");
                    if (stage == "pending") { finish.SetResult(new("late photo secret", "", "high", 17)); }
                    await WaitAsync(() => ownedBytes!.All(value => value == 0));
                    await WaitAsync(() => runner.TryAcquire());
                    runner.Release();
                    Check(calls == 1 && journal.ReadSessionCharge(id) == 17
                        && store.GetOperation(stage, image.OperationId!).Operation is { Status: "cancelled", Result: null },
                        "discarded photo settles once, wipes bytes, and cannot revive its cue");
                }
                else
                {
                    var rejected = store.SubmitImage(stage, id, Guid.NewGuid(), upload);
                    Check(rejected.ErrorCode == "costume_context_discarded" && journal.ReadSessionCharge(id) == 0,
                        "clear before photo rejects admission before provider charging");
                    clock.Now = clock.Now.AddSeconds(121);
                    Check(store.CorrectCostume(stage, id, clearKey, upload, "").Operation == clear.Operation,
                        "clear acknowledgement remains idempotent beyond the retired two-minute expiry");
                    Check(store.SubmitImage(stage, id, Guid.NewGuid(), upload).ErrorCode == "costume_context_discarded",
                        "clear remains distinct from an expired manual cue");
                }
                var turn = store.SubmitTurn(stage, id, Guid.NewGuid(), Encoding.UTF8.GetBytes(stage + "-opening"),
                    new(Guid.NewGuid(), boot, 1, "", 63, 1, "catalog"), true);
                await WaitAsync(() => store.GetOperation(stage, turn.OperationId!).Operation?.Result is TurnResult);
                Check(prompt == "", "clear supplies no cue to the next deterministic fixture turn");
            }

            var restoredStore = new ConversationStore(clock, journal);
            var restoredId = restoredStore.Create("restored", Guid.NewGuid(), "restored"u8,
                new("restored", boot, 1, "catalog", "cs-CZ", new(true, true))).Response!.ConversationId;
            restoredStore.CorrectCostume("restored", restoredId, Guid.NewGuid(), upload, "");
            restoredStore.CorrectCostume("restored", restoredId, Guid.NewGuid(), upload, "restored manual cue");
            var restoredPhoto = restoredStore.SubmitImage("restored", restoredId, Guid.NewGuid(), upload);
            Check(restoredPhoto.Status == 202 && restoredStore.GetOperation("restored", restoredPhoto.OperationId!).Operation?.Result
                is CostumeContext { Source: "manual", Description: "restored manual cue" },
                "a new nonempty correction restores manual priority and permits a first fake photo");

            using var dialog = new TextDialogRunner((_, _, _, _, _, _, _) => throw new Exception("No provider expected."));
            using var voice = new VoiceProviderRunner((_, _) => throw new Exception("No provider expected."),
                (_, _) => throw new Exception("No provider expected."));
            foreach (var gptLive in new[] { false, true })
            {
                var device = "clear-voice-" + gptLive;
                var store = new ConversationStore(clock, journal, dialog, voiceRunner: voice);
                var id = store.Create(device, Guid.NewGuid(), Encoding.UTF8.GetBytes(device),
                    new(device, boot, 1, PartyWandCatalog.Version, "cs-CZ", new(true, true))).Response!.ConversationId;
                var voiceCorrelation = upload with { CatalogVersion = PartyWandCatalog.Version };
                store.CorrectCostume(device, id, Guid.NewGuid(), voiceCorrelation, "manual secret");
                store.CorrectCostume(device, id, Guid.NewGuid(), voiceCorrelation, "");
                var request = new RealtimeTurnRequest(Guid.NewGuid(), boot, 1, 63, 1, PartyWandCatalog.Version, true, "", null, "marin", 1);
                var admission = store.BeginRealtimeTurn(device, id, Guid.NewGuid(), JsonSerializer.SerializeToUtf8Bytes(request),
                    request, true, default, default, gptLive: gptLive);
                Check(admission.Call is not null && admission.Call.Context.CostumeContext == "",
                    "clear supplies no cue to Realtime or Live admission");
                var turn = admission.Call!.Context;
                Check(!RealtimeProtocol.SessionOptions(turn).Instructions.Contains("Transient costume cue")
                    && !JsonSerializer.Serialize(GptLiveProtocol.Start(turn)).Contains("Transient costume cue")
                    && !JsonSerializer.Serialize(GptLiveProtocol.AutonomousStart(turn with {
                        LivePrompt = LivePromptSnapshot.Create(PersonalityCatalog.DefaultId, new() { Mode = LiveActorMode.AutonomousActor })
                    }, "test")).Contains("Transient costume cue"),
                    "voice model prompts exclude the clear acknowledgement");
                var input = new WandDecisionInput(1, 63, 1, [], turn.LivePrompt!.Policy, 1, false,
                    CostumeContext: turn.CostumeContext);
                Check(!WandDecisionService.BuildRequest(input).Contains("Transient costume cue"),
                    "Sol prompt excludes the clear acknowledgement");
                store.ReleaseRealtimeTurn(admission.Call, dispatched: false, completed: false);
            }
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(directory, recursive: true); }
    }

    private static async Task WaitAsync(Func<bool> predicate)
    {
        for (var i=0; i<200; i++) { if (predicate()) { return; } await Task.Delay(10); }
        throw new Exception("Costume image operation did not complete.");
    }

    private static async Task ImageCloseBarrierAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), "kufr-image-close-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> {
                ["Backend:DatabasePath"] = Path.Combine(directory, "journal.sqlite") }).Build();
            using var journal = new OperationJournal(configuration, TimeProvider.System);
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var finishCleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            byte[]? ownedBytes = null;
            using var runner = new TextDialogRunner((_,_,_,_,_,_,_) =>
                Task.FromResult(new TextDialogResponse("Question?", "ask", "", null, ResponsesTextDialogBudget.Model, 1, 1, "default")),
                async (jpeg, token) => {
                    ownedBytes = jpeg;
                    started.TrySetResult();
                    try { await Task.Delay(Timeout.Infinite, token); }
                    catch (OperationCanceledException) { cancelled.TrySetResult(); }
                    await finishCleanup.Task;
                    token.ThrowIfCancellationRequested();
                    throw new Exception("Cancellation expected.");
                });
            var store = new ConversationStore(TimeProvider.System, journal, runner);
            var boot = Guid.NewGuid();
            string Create(string device, bool image) => store.Create(device, Guid.NewGuid(), Encoding.UTF8.GetBytes(device),
                new(device, boot, 1, "catalog", "cs-CZ", new(false, image))).Response!.ConversationId;
            var id = Create("barrier", true);
            var upload = new CostumeImageUpload(Guid.NewGuid(), boot, 1, 1, "catalog", Jpeg());
            store.SubmitImage("barrier", id, Guid.NewGuid(), upload);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var otherDeviceClose = await store.EndConversationAfterImageAsync("other", id, Guid.NewGuid(),
                waitTimeout: TimeSpan.FromMilliseconds(20));
            Check(otherDeviceClose.Status == 403 && !cancelled.Task.IsCompleted, "foreign device neither cancels nor waits for image");
            var closeKey = Guid.NewGuid();
            var firstClose = store.EndConversationAfterImageAsync("barrier", id, closeKey,
                waitTimeout: TimeSpan.FromMilliseconds(100));
            await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check(!firstClose.IsCompleted && ownedBytes!.Any(value => value != 0), "DELETE waits while cancelled provider is still unwinding");
            Check((await firstClose).Status == 503, "bounded DELETE reports image cancellation pending");
            var retry = store.EndConversationAfterImageAsync("barrier", id, closeKey);
            Check(!retry.IsCompleted, "idempotent close retry also waits for release");
            finishCleanup.TrySetResult();
            Check((await retry).Status == 204 && ownedBytes!.All(value => value == 0), "204 requires owned image wipe and provider slot release");
            var fresh = Create("barrier", false);
            var opening = store.SubmitTurn("barrier", fresh, Guid.NewGuid(), "fresh-opening"u8,
                new(Guid.NewGuid(), boot, 1, "", 63, 1, "catalog"), true);
            Check(opening.Status == 202, "fresh no-image dialogue starts immediately after image close ack");
            await WaitAsync(() => store.GetOperation("barrier", opening.OperationId!).Operation?.Result is TurnResult);
            var noImageClose = store.EndConversationAfterImageAsync("barrier", fresh, Guid.NewGuid());
            Check(noImageClose.IsCompletedSuccessfully && (await noImageClose).Status == 204,
                "non-image conversation retains immediate close behavior");
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(directory, recursive: true); }
    }
    private static void Check(bool condition, string message) { if (!condition) { throw new Exception(message); } }
    private static HttpResponseMessage Response(object body) => new(HttpStatusCode.OK) {
        Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
    private sealed class Handler(Func<HttpRequestMessage,CancellationToken,Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request, token);
    }
    private sealed class Clock : TimeProvider
    {
        internal DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class StalledReadStream : MemoryStream
    {
        internal Memory<byte> Buffer { get; private set; }
        private bool read;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            Buffer = buffer;
            if (!read)
            {
                read = true;
                buffer.Span[..3].Fill(0xab);
                return 3;
            }
            await Task.Delay(Timeout.Infinite, token);
            return 0;
        }
    }
}
