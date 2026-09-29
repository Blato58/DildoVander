using System.Security.Cryptography;
using System.Text.Json;
using Kufr.Backend.Features.Access;
using Microsoft.AspNetCore.Http.Features;

namespace Kufr.Backend.Features.Conversations;

internal static class ConversationEndpoints
{
    public static IEndpointRouteBuilder MapConversations(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/v1/conversations", CreateConversationAsync);
        endpoints.MapPost("/v1/conversations/{id}/turns", SubmitTurnAsync);
        endpoints.MapPost("/v1/conversations/{id}/transcriptions", SubmitTranscriptionAsync);
        endpoints.MapPost("/v1/conversations/{id}/speech", SubmitSpeechAsync);
        endpoints.MapGet("/v1/operations/{id}", GetOperation);
        endpoints.MapGet("/v1/audio/{audioId}", GetAudio);
        endpoints.MapDelete("/v1/operations/{id}", CancelOperationAsync);
        endpoints.MapDelete("/v1/conversations/{id}", EndConversationAsync);
        return endpoints;
    }

    private static async Task<IResult> CreateConversationAsync(
        HttpContext context,
        DeviceAccessStore access,
        ConversationStore conversations)
    {
        var deviceId = DeviceAccessEndpoints.Authenticate(context, access);
        if (deviceId is null)
        {
            return ApiProblem.Create(context, 401, "access_token_required");
        }

        if (!Guid.TryParseExact(context.Request.Headers["Idempotency-Key"], "D", out var key))
        {
            return ApiProblem.Create(context, 400, "invalid_idempotency_key");
        }

        byte[]? body;
        try
        {
            body = await ReadSmallBodyAsync(context);
        }
        catch (BadHttpRequestException exception) when (exception.StatusCode == 413)
        {
            return ApiProblem.Create(context, 413, "request_too_large");
        }

        if (body is null)
        {
            return ApiProblem.Create(context, 400, "invalid_request");
        }
        CreateConversationRequest? request;
        try
        {
            using var document = JsonDocument.Parse(body);
            if (!HasExpectedProperties(
                    document.RootElement,
                    ["deviceId", "bootId", "generation", "catalogVersion", "locale", "consent"])
                || !document.RootElement.TryGetProperty("consent", out var consent)
                || !HasExpectedProperties(consent, ["audio", "image"])
                || !document.RootElement.TryGetProperty("generation", out var generation)
                || generation.ValueKind != JsonValueKind.Number
                || !int.TryParse(generation.GetRawText(), out _)
                || !document.RootElement.TryGetProperty("bootId", out var bootId)
                || bootId.ValueKind != JsonValueKind.String
                || !Guid.TryParseExact(bootId.GetString(), "D", out _))
            {
                return ApiProblem.Create(context, 400, "invalid_request");
            }

            request = JsonSerializer.Deserialize<CreateConversationRequest>(
                body,
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
        }
        catch (JsonException)
        {
            return ApiProblem.Create(context, 400, "invalid_request");
        }

        if (request is null
            || request.DeviceId is not { Length: > 0 and <= 128 }
            || request.BootId == Guid.Empty
            || request.Generation < 0
            || request.CatalogVersion is not { Length: > 0 and <= 128 }
            || request.Locale != "cs-CZ"
            || request.Consent is null)
        {
            return ApiProblem.Create(context, 400, "invalid_request");
        }

        if (request.DeviceId != deviceId)
        {
            return ApiProblem.Create(context, 403, "device_scope_mismatch");
        }

        var result = conversations.Create(deviceId, key, body, request);
        return result.Response is null
            ? ApiProblem.Create(context, result.Status, "conversation_conflict")
            : Results.Json(result.Response, statusCode: result.Status);
    }

    private static async Task<IResult> SubmitTurnAsync(
        string id,
        HttpContext context,
        DeviceAccessStore access,
        ConversationStore conversations)
    {
        var deviceId = DeviceAccessEndpoints.Authenticate(context, access);
        if (deviceId is null)
        {
            return ApiProblem.Create(context, 401, "access_token_required");
        }

        if (!Guid.TryParseExact(context.Request.Headers["Idempotency-Key"], "D", out var key))
        {
            return ApiProblem.Create(context, 400, "invalid_idempotency_key");
        }

        byte[]? body;
        try
        {
            body = await ReadSmallBodyAsync(context);
        }
        catch (BadHttpRequestException exception) when (exception.StatusCode == 413)
        {
            return ApiProblem.Create(context, 413, "request_too_large");
        }

        if (body is null)
        {
            return ApiProblem.Create(context, 400, "invalid_request");
        }
        TextTurnRequest? request;
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (!HasExpectedProperties(
                    root,
                    ["clientTurnId", "bootId", "generation", "text", "readyMask", "deviceRevision", "catalogVersion"])
                || !IsUuid(root, "clientTurnId")
                || !IsUuid(root, "bootId")
                || !IsInteger(root, "generation")
                || !IsInteger(root, "readyMask")
                || !IsInteger(root, "deviceRevision"))
            {
                return ApiProblem.Create(context, 400, "invalid_request");
            }

            request = JsonSerializer.Deserialize<TextTurnRequest>(
                body,
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
        }
        catch (JsonException)
        {
            return ApiProblem.Create(context, 400, "invalid_request");
        }

        if (request is null
            || request.ClientTurnId == Guid.Empty
            || request.BootId == Guid.Empty
            || request.Generation < 0
            || request.Text is not { Length: > 0 and <= 1000 }
            || string.IsNullOrWhiteSpace(request.Text)
            || request.ReadyMask is < 0 or > 63
            || request.DeviceRevision < 0
            || request.CatalogVersion is not { Length: > 0 and <= 128 })
        {
            return ApiProblem.Create(context, 400, "invalid_request");
        }

        var result = conversations.SubmitTurn(deviceId, id, key, body, request);
        return result.OperationId is null
            ? ApiProblem.Create(context, result.Status, result.ErrorCode ?? "turn_rejected")
            : Results.Json(new { operationId = result.OperationId }, statusCode: result.Status);
    }

    private static IResult GetOperation(
        string id,
        HttpContext context,
        DeviceAccessStore access,
        ConversationStore conversations)
    {
        var deviceId = DeviceAccessEndpoints.Authenticate(context, access);
        if (deviceId is null)
        {
            return ApiProblem.Create(context, 401, "access_token_required");
        }

        var result = conversations.GetOperation(deviceId, id);
        return result.Operation is null
            ? ApiProblem.Create(
                context,
                result.Status,
                result.Status == 403 ? "device_scope_mismatch" : "operation_not_found")
            : Results.Json(result.Operation);
    }

    private static async Task<IResult> SubmitSpeechAsync(
        string id,
        HttpContext context,
        DeviceAccessStore access,
        ConversationStore conversations)
    {
        var deviceId = DeviceAccessEndpoints.Authenticate(context, access);
        if (deviceId is null)
        {
            return ApiProblem.Create(context, 401, "access_token_required");
        }

        if (!Guid.TryParseExact(context.Request.Headers["Idempotency-Key"], "D", out var key))
        {
            return ApiProblem.Create(context, 400, "invalid_idempotency_key");
        }

        byte[]? body;
        try
        {
            body = await ReadSmallBodyAsync(context);
        }
        catch (BadHttpRequestException exception) when (exception.StatusCode == 413)
        {
            return ApiProblem.Create(context, 413, "request_too_large");
        }

        if (body is null)
        {
            return ApiProblem.Create(context, 400, "invalid_request");
        }
        SpeechRequest? request;
        try
        {
            using var document = JsonDocument.Parse(body);
            if (!HasExpectedProperties(document.RootElement, ["turnId", "outputTarget"])
                || !IsUuid(document.RootElement, "turnId"))
            {
                return ApiProblem.Create(context, 400, "invalid_request");
            }

            request = JsonSerializer.Deserialize<SpeechRequest>(
                body,
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
        }
        catch (JsonException)
        {
            return ApiProblem.Create(context, 400, "invalid_request");
        }

        if (request is null
            || request.TurnId == Guid.Empty
            || request.OutputTarget is not ("device" or "phone"))
        {
            return ApiProblem.Create(context, 400, "invalid_request");
        }

        var result = conversations.SubmitSpeech(deviceId, id, key, body, request);
        return result.OperationId is null
            ? ApiProblem.Create(context, result.Status, result.ErrorCode ?? "speech_rejected")
            : Results.Json(new { operationId = result.OperationId }, statusCode: result.Status);
    }

    private static IResult GetAudio(
        string audioId,
        HttpContext context,
        DeviceAccessStore access,
        ConversationStore conversations)
    {
        var deviceId = DeviceAccessEndpoints.Authenticate(context, access);
        if (deviceId is null)
        {
            return ApiProblem.Create(context, 401, "access_token_required");
        }

        var result = conversations.GetAudio(deviceId, audioId);
        if (result.Wav is null)
        {
            var code = result.Status switch
            {
                403 => "device_scope_mismatch",
                410 => "audio_expired",
                _ => "audio_not_found"
            };
            return ApiProblem.Create(context, result.Status, code);
        }

        context.Response.Headers["X-Audio-Sample-Rate-Hz"] = "32000";
        context.Response.Headers["X-Audio-Channels"] = "1";
        return Results.File(result.Wav, "audio/wav");
    }

    private static async Task<IResult> SubmitTranscriptionAsync(
        string id,
        HttpContext context,
        DeviceAccessStore access,
        ConversationStore conversations)
    {
        var deviceId = DeviceAccessEndpoints.Authenticate(context, access);
        if (deviceId is null)
        {
            return ApiProblem.Create(context, 401, "access_token_required");
        }

        if (!Guid.TryParseExact(context.Request.Headers["Idempotency-Key"], "D", out var key))
        {
            return ApiProblem.Create(context, 400, "invalid_idempotency_key");
        }

        var sizeFeature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (sizeFeature is { IsReadOnly: false })
        {
            sizeFeature.MaxRequestBodySize = TranscriptionUploadReader.MaxRequestBytes;
        }

        if (context.Request.ContentLength is > TranscriptionUploadReader.MaxRequestBytes)
        {
            return ApiProblem.Create(context, 413, "request_too_large");
        }

        UploadReadResult read;
        try
        {
            read = await TranscriptionUploadReader.ReadAsync(context.Request, context.RequestAborted);
        }
        catch (BadHttpRequestException exception) when (exception.StatusCode == 413)
        {
            return ApiProblem.Create(context, 413, "request_too_large");
        }

        if (read.Upload is null)
        {
            return ApiProblem.Create(
                context,
                read.Status,
                read.Status == 413 ? "request_too_large" : "invalid_request");
        }

        try
        {
            var result = conversations.SubmitTranscription(deviceId, id, key, read.Upload);
            return result.OperationId is null
                ? ApiProblem.Create(context, result.Status, result.ErrorCode ?? "transcription_rejected")
                : Results.Json(new { operationId = result.OperationId }, statusCode: result.Status);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(read.Upload.Wav);
        }
    }

    private static async Task<IResult> CancelOperationAsync(
        string id,
        HttpContext context,
        DeviceAccessStore access,
        ConversationStore conversations)
    {
        var deviceId = DeviceAccessEndpoints.Authenticate(context, access);
        if (deviceId is null)
        {
            return ApiProblem.Create(context, 401, "access_token_required");
        }

        if (!Guid.TryParseExact(context.Request.Headers["Idempotency-Key"], "D", out var key)
            || !await HasEmptyBodyAsync(context))
        {
            return ApiProblem.Create(context, 400, "invalid_request");
        }

        var result = conversations.CancelOperation(deviceId, id, key);
        return result.Status == 204
            ? Results.NoContent()
            : ApiProblem.Create(context, result.Status, result.Status == 403
                ? "device_scope_mismatch"
                : "operation_not_found");
    }

    private static async Task<IResult> EndConversationAsync(
        string id,
        HttpContext context,
        DeviceAccessStore access,
        ConversationStore conversations)
    {
        var deviceId = DeviceAccessEndpoints.Authenticate(context, access);
        if (deviceId is null)
        {
            return ApiProblem.Create(context, 401, "access_token_required");
        }

        if (!Guid.TryParseExact(context.Request.Headers["Idempotency-Key"], "D", out var key)
            || !await HasEmptyBodyAsync(context))
        {
            return ApiProblem.Create(context, 400, "invalid_request");
        }

        var result = conversations.EndConversation(deviceId, id, key);
        return result.Status == 204
            ? Results.NoContent()
            : ApiProblem.Create(context, result.Status, result.Status == 403
                ? "device_scope_mismatch"
                : "conversation_not_found");
    }

    private static bool IsUuid(JsonElement root, string name)
    {
        var value = root.GetProperty(name);
        return value.ValueKind == JsonValueKind.String
            && Guid.TryParseExact(value.GetString(), "D", out _);
    }

    private static async Task<bool> HasEmptyBodyAsync(HttpContext context)
    {
        if (context.Request.ContentLength is > 0)
        {
            return false;
        }

        var prefix = new byte[1];
        return await context.Request.Body.ReadAsync(prefix, context.RequestAborted) == 0;
    }

    private static async Task<byte[]?> ReadSmallBodyAsync(HttpContext context)
    {
        const int maximum = 4096;
        if (context.Request.ContentLength is 0 or > maximum)
        {
            return null;
        }

        using var content = new MemoryStream();
        var buffer = new byte[1024];
        while (true)
        {
            var read = await context.Request.Body.ReadAsync(buffer, context.RequestAborted);
            if (read == 0)
            {
                return content.Length == 0 ? null : content.ToArray();
            }

            if (content.Length + read > maximum)
            {
                return null;
            }

            content.Write(buffer, 0, read);
        }
    }

    private static bool IsInteger(JsonElement root, string name)
    {
        var value = root.GetProperty(name);
        return value.ValueKind == JsonValueKind.Number
            && int.TryParse(value.GetRawText(), out _);
    }

    private static bool HasExpectedProperties(JsonElement element, string[] expected)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!expected.Contains(property.Name, StringComparer.Ordinal) || !seen.Add(property.Name))
            {
                return false;
            }
        }

        return seen.Count == expected.Length;
    }
}
