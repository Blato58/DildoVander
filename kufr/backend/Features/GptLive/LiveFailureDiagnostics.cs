using System.ClientModel;
using System.Net.WebSockets;
using System.Text.Json;
using Kufr.Backend.Features.Conversations;
using Microsoft.Data.Sqlite;

namespace Kufr.Backend.Features.GptLive;

// Persist only application-owned categories. Provider messages, arbitrary codes,
// HTTP bodies and user text must never enter the technical log.
internal sealed class LiveProviderFailure : Exception
{
    internal string Code { get; }
    private LiveProviderFailure(string code) : base("Live provider reported an error.") => Code = code;

    internal static LiveProviderFailure Read(JsonElement value)
    {
        static string? Field(JsonElement error, string name) => error.ValueKind == JsonValueKind.Object
            && error.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.String ? field.GetString() : null;
        if (!value.TryGetProperty("error", out var error)) return new("provider_invalid_error_event");
        return new(Field(error, "code") switch
        {
            "unknown_parameter" => "provider_unknown_parameter",
            "immutable_field_update" => "provider_immutable_field",
            "rate_limit_exceeded" => "provider_capacity",
            "invalid_delegation_id" => "provider_invalid_delegation",
            _ => Field(error, "type") switch
            {
                "invalid_request_error" => "provider_rejected_command",
                "server_error" => "provider_server_error",
                "rate_limit_error" => "provider_capacity",
                "moderation_error" => "provider_moderation",
                _ => "provider_error_unknown"
            }
        });
    }
}

internal static class LiveFailureDiagnostics
{
    internal static string Code(Exception error) => error switch
    {
        LiveProviderFailure provider => provider.Code,
        OperationCanceledException => "live_cancelled",
        ClientResultException provider => provider.Status switch
        {
            429 => "decision_provider_capacity",
            401 or 403 => "decision_provider_auth",
            >= 500 => "decision_provider_server",
            _ => "decision_provider_rejected"
        },
        TextDialogUnavailableException => "decision_usage_unconfirmed",
        WebSocketException => "live_socket_failed",
        SqliteException => "live_memory_storage_failed",
        JsonException => "live_event_json_invalid",
        FormatException => "live_event_format_invalid",
        KeyNotFoundException => "live_event_field_missing",
        InvalidDataException data => data.Message switch
        {
            "Decision is not grounded in the current valid input." => "decision_invalid_evidence",
            "Incomplete decision." => "decision_incomplete",
            "Ambiguous decision." => "decision_ambiguous",
            "Invalid decision fields." or "Invalid decision evidence fields." or "Invalid decision reasons." or "Invalid decision status." => "decision_invalid_fields",
            "Invalid transcript time." or "Invalid Live timeline value." => "live_event_timing_invalid",
            "Live playback/finalization unverified." => "live_finalization_timeout",
            "Live usage regressed." => "live_usage_regressed",
            "Unsupported Live event." => "live_event_unsupported",
            "Live context correlation mismatch." => "live_context_correlation",
            "Unacknowledged Live context limit." => "live_context_limit",
            "Terminal speech was interrupted during close." => "live_close_interrupted",
            "Unexpected Live close." => "live_close_unexpected",
            "Live closed without final usage." => "live_final_usage_missing",
            "Live decision budget exhausted." => "live_decision_budget",
            "Invalid Live audio." => "live_audio_invalid",
            _ => "live_data_invalid"
        },
        _ => "live_internal_error"
    };
}
