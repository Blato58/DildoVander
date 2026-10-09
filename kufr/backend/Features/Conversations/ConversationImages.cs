using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Kufr.Backend.Features.Conversations;

internal sealed partial class ConversationStore
{
    private readonly Dictionary<string, ImageCompletion> imageCompletions = new(StringComparer.Ordinal);

    internal async Task<MutationResult> EndConversationAfterImageAsync(string deviceId, string id, Guid key,
        CancellationToken requestAborted = default, TimeSpan? waitTimeout = null)
    {
        Task? pending = null;
        MutationResult result;
        lock (gate)
        {
            if (imageCompletions.TryGetValue(id, out var completion) && completion.DeviceId == deviceId)
            {
                pending = completion.Completion.Task;
            }
            result = EndConversation(deviceId, id, key);
        }
        if (result.Status != 204 || pending is null) { return result; }
        try
        {
            // Stop is already recorded, but only acknowledge a safe fresh start
            // once the old image has released its shared provider slot.
            await pending.WaitAsync(waitTimeout ?? TimeSpan.FromSeconds(5), requestAborted);
            return result;
        }
        catch (TimeoutException) { return new(503, false); }
        catch (OperationCanceledException) when (requestAborted.IsCancellationRequested) { throw; }
        catch (Exception) { return new(503, false); }
    }

    internal SubmitTurnResult SubmitImage(string deviceId, string id, Guid key, CostumeImageUpload upload)
    {
        if (!CostumeJpeg.IsValid(upload.Jpeg)) { return new(400, null, "invalid_image"); }
        var route = $"/v1/conversations/{id}/image";
        var hash = ImageHash(upload, upload.Jpeg);
        lock (gate)
        {
            var now = clock.GetUtcNow();
            CleanupExpired(now);
            var previous = journal.FindExisting(deviceId, route, key, hash);
            if (previous is not null)
            {
                return previous.Conflict ? new(409, null, "idempotency_conflict") : new(202, previous.OperationId, null);
            }
            var error = ValidateCostumeAccess(deviceId, id, upload, requireConsent: true);
            if (error is not null) { return error; }
            var conversation = conversations[id];
            if (conversation.ImageOperationId is not null) { return new(409, null, "image_already_submitted"); }
            if (conversation.CostumeCleared) { return new(409, null, "costume_context_discarded"); }
            if (conversation.ManualCostume && conversation.Costume is null)
            { return new(409, null, "costume_context_expired"); }
            if (conversation.TurnCount != 0 || HasActiveCall(id)) { return new(409, null, "image_before_dialogue_required"); }
            var paid = dialogRunner?.Enabled == true;
            if (paid && (activePaidCalls.Count != 0 || !dialogRunner!.TryAcquire()))
            { return new(503, null, "dialogue_busy"); }
            var queued = false;
            try
            {
                var correlation = new TurnCorrelation(deviceId, upload.BootId, id, upload.ClientTurnId,
                    upload.Generation, upload.DeviceRevision);
                var reservation = journal.Reserve(deviceId, route, key, hash, correlation, kind: "image",
                    consumesTurn: false, estimatedCostMicroUsd: paid ? CostumeImageClient.ReservationMicroUsd : 0);
                if (reservation.Conflict) { return new(409, null, "idempotency_conflict"); }
                if (reservation.Existing) { return new(202, reservation.OperationId, null); }
                conversation.ImageOperationId = reservation.OperationId;
                if (!paid)
                {
                    var context = new CostumeContext("Testovaci kostym; fake fotografie se neanalyzuje.", "", "high", "fake", now.AddSeconds(120));
                    if (!conversation.ManualCostume) { conversation.Costume = context; }
                    operations.Add(reservation.OperationId, new(reservation.OperationId, "image", "completed", correlation, conversation.Costume, null));
                    journal.MarkCompleted(reservation.OperationId);
                    return new(202, reservation.OperationId, null);
                }
                var cancellation = CancellationTokenSource.CreateLinkedTokenSource(dialogRunner!.Stopping);
                cancellation.CancelAfter(TimeSpan.FromSeconds(45));
                activePaidCalls.Add(reservation.OperationId, new(deviceId, id, cancellation));
                var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                imageCompletions.Add(id, new(deviceId, completion));
                var ownedJpeg = upload.Jpeg.ToArray();
                _ = Task.Run(() => ProcessImageAsync(reservation.OperationId, correlation, ownedJpeg, cancellation, completion));
                queued = true;
                return new(202, reservation.OperationId, null);
            }
            finally
            {
                if (paid && !queued) { dialogRunner!.Release(); }
            }
        }
    }

    internal GetOperationResult CorrectCostume(string deviceId, string id, Guid key, CostumeImageUpload correlation,
        string description)
    {
        if (!CostumeImageClient.ValidText(description, 240)) { return new(400, null); }
        lock (gate)
        {
            CleanupExpired(clock.GetUtcNow());
            var error = ValidateCostumeAccess(deviceId, id, correlation, requireConsent: false);
            if (error is not null) { return new(error.Status, null); }
            var route = $"/v1/conversations/{id}/costume-context";
            var hash = ImageHash(correlation, Encoding.UTF8.GetBytes(description));
            var previous = journal.FindExisting(deviceId, route, key, hash);
            if (previous is not null)
            {
                return previous.Conflict ? new(409, null) : GetOperation(deviceId, previous.OperationId);
            }
            var conversation = conversations[id];
            if (conversation.TurnCount != 0 || realtimeLifetimes.ContainsKey(id)) { return new(409, null); }
            if (conversation.CostumeCorrectionCount >= 8) { return new(429, null); }
            var reservation = journal.Reserve(deviceId, route, key, hash,
                new(deviceId, correlation.BootId, id, correlation.ClientTurnId, correlation.Generation, correlation.DeviceRevision),
                kind: "costume", consumesTurn: false);
            if (reservation.Conflict) { return new(409, null); }
            conversation.ManualCostume = true;
            conversation.CostumeCleared = description.Length == 0;
            conversation.CostumeCorrectionCount++;
            var context = new CostumeContext(description, "", "high", "manual", clock.GetUtcNow().AddSeconds(120));
            // The empty manual receipt acknowledges a clear; it is never an active prompt cue.
            conversation.Costume = conversation.CostumeCleared ? null : context;
            if (conversation.ImageOperationId is { } imageId)
            {
                if (conversation.CostumeCleared)
                {
                    // Suppress publication without interrupting usage settlement or the cleanup barrier.
                    journal.CancelOperation(deviceId, imageId, key);
                    operations.Remove(imageId);
                }
                else if (operations.TryGetValue(imageId, out var imageOperation))
                {
                    operations[imageId] = imageOperation with { Result = context };
                }
            }
            var operation = new TurnOperation(reservation.OperationId, "costume", "completed",
                new(deviceId, correlation.BootId, id, correlation.ClientTurnId, correlation.Generation, correlation.DeviceRevision), context, null);
            operations.Add(operation.OperationId, operation);
            journal.MarkCompleted(operation.OperationId);
            return new(200, operation);
        }
    }

    private SubmitTurnResult? ValidateCostumeAccess(string device, string id, CostumeImageUpload upload, bool requireConsent)
    {
        if (!conversations.TryGetValue(id, out var conversation)) { return new(404, null, "conversation_not_found"); }
        if (conversation.DeviceId != device) { return new(403, null, "device_scope_mismatch"); }
        if (requireConsent && !conversation.ImageConsented) { return new(403, null, "image_consent_required"); }
        if (conversation.BootId != upload.BootId || conversation.Generation != upload.Generation
            || conversation.CatalogVersion != upload.CatalogVersion)
        { return new(409, null, "conversation_version_mismatch"); }
        return null;
    }

    private static string ImageHash(CostumeImageUpload upload, ReadOnlySpan<byte> bytes)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new {
            upload.ClientTurnId, upload.BootId, upload.Generation, upload.DeviceRevision, upload.CatalogVersion })));
        hash.AppendData(bytes);
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private async Task ProcessImageAsync(string operationId, TurnCorrelation correlation, byte[] jpeg,
        CancellationTokenSource cancellation, TaskCompletionSource completion)
    {
        try
        {
            lock (gate)
            {
                if (cancellation.IsCancellationRequested || !journal.MarkRunning(operationId))
                {
                    journal.SettleCost(operationId, 0);
                    return;
                }
            }
            var response = await dialogRunner!.AnalyzeImageAsync(jpeg, cancellation.Token);
            lock (gate)
            {
                if (!journal.SettleCost(operationId, response.CostMicroUsd)) { journal.MarkFailed(operationId); return; }
                CleanupExpired(clock.GetUtcNow());
                if (cancellation.IsCancellationRequested || !conversations.TryGetValue(correlation.ConversationId, out var conversation))
                { journal.MarkFailed(operationId); return; }
                if (conversation.CostumeCleared || conversation.ManualCostume && conversation.Costume is null)
                { journal.MarkFailed(operationId); return; }
                var context = new CostumeContext(response.Description, response.PossibleCharacter, response.Uncertainty,
                    "photo", clock.GetUtcNow().AddSeconds(120));
                if (!conversation.ManualCostume) { conversation.Costume = context; }
                // A manual correction also governs a late poll result.
                operations.Add(operationId, new(operationId, "image", "completed", correlation,
                    conversation.Costume, null));
                if (!journal.MarkCompleted(operationId)) { operations.Remove(operationId); }
            }
        }
        catch (Exception)
        {
            lock (gate) { journal.MarkFailed(operationId); }
        }
        finally
        {
            try
            {
                CryptographicOperations.ZeroMemory(jpeg);
                lock (gate)
                {
                    activePaidCalls.Remove(operationId);
                    cancellation.Dispose();
                    dialogRunner!.Release();
                    imageCompletions.Remove(correlation.ConversationId);
                    completion.TrySetResult();
                }
            }
            catch (Exception exception)
            {
                // A cleanup failure must never produce a false 204 barrier ack.
                completion.TrySetException(exception);
                throw;
            }
        }
    }

    private string CostumePrompt(ConversationEntry conversation) => conversation.Costume is { } context
        && context.ExpiresAt > clock.GetUtcNow() ? context.Prompt : "";

    private sealed record ImageCompletion(string DeviceId, TaskCompletionSource Completion);
}
