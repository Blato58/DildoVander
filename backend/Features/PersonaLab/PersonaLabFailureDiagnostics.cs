using System.Text.Json;

namespace Kufr.Backend.Features.PersonaLab;

internal static class PersonaLabFailureDiagnostics
{
    // Match only application-owned literals, never expose arbitrary exception
    // messages, provider error bodies, tool arguments or visitor transcripts.
    internal static string RealtimeCode(Exception error) => error switch
    {
        LabException { Code: "lab_realtime_input_buffer_limit" } => "realtime_input_buffer_limit",
        JsonException or FormatException or KeyNotFoundException => "realtime_event_invalid",
        InvalidDataException data => data.Message switch
        {
            "Unexpected input transcription." or "Realtime input transcription failed."
                or "Realtime transcript acknowledgement missing." => "realtime_input_transcription_conflict",
            "Incomplete Realtime response." or "Incomplete Realtime interruption."
                or "Realtime reply is missing." or "Realtime turn did not complete."
                or "Realtime stream ended early." => "realtime_response_incomplete",
            "Unexpected Realtime session." or "Realtime configuration was not accepted."
                or "Realtime visual context update failed." => "realtime_session_invalid",
            "Unexpected Realtime response." or "Unexpected audio response identity."
                or "Invalid Realtime audio length." or "Too many Realtime audio parts."
                or "Unexpected Realtime output modality." or "Unsupported Realtime output."
                or "Realtime reply is too long." or "Output budget exceeded."
                or "Realtime interruption audio identity missing." => "realtime_output_invalid",
            "Invalid Realtime tool arguments." or "Realtime selection was rejected."
                or "Unexpected Realtime tool count." => "realtime_tool_invalid",
            "Invalid Realtime event field." or "Too many provider conversation items."
                or "Too many input events." or "Realtime input failed." => "realtime_event_invalid",
            _ => "provider_error"
        },
        _ => "provider_error"
    };
}
