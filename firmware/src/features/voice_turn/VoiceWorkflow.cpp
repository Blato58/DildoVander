#include "VoiceWorkflow.h"
#include "../conversation/ConversationStartCodec.h"

VoiceWorkflowResult RunVoiceWorkflow(const VoiceWorkflowRequest& request,
    const VoiceWorkflowPorts& ports) {
  VoiceWorkflowResult result;
  if (!ports.current() || !ports.close()) return result;
  TurnCorrelation correlation = request.correlation;
  if (!ports.create(request.imageConsent, request.requestId, correlation.conversationId) || !ports.current()) return result;
  correlation.clientTurnId = ports.uuid();
  bool ready = true;
  if (request.imageConsent) {
    result.photo = ports.photo(correlation);
    if (result.photo == PhotoOutcome::Failed) {
      ready = ports.current() && ports.close();
      if (ready) ready = ports.create(false, ports.uuid(), correlation.conversationId) && ports.current();
    }
  }
  if (!ports.current() || !ready) { (void)ports.close(); return result; }
  correlation.clientTurnId = ports.uuid();
  if (!IsConversationUuid(correlation.clientTurnId)) return result;
  ports.beginAudio();
  const auto& snapshot = request.snapshot;
  const std::string body = "{\"clientTurnId\":\"" + correlation.clientTurnId +
      "\",\"bootId\":\"" + correlation.bootId + "\",\"generation\":" + std::to_string(snapshot.generation) +
      ",\"readyMask\":" + std::to_string(snapshot.readyMask) +
      ",\"deviceRevision\":" + std::to_string(snapshot.deviceRevision) +
      ",\"catalogVersion\":\"" + snapshot.catalogVersion +
      "\",\"opening\":true,\"text\":\"\",\"wav\":null,\"voice\":\"" + request.voice + "\",\"speed\":" + request.speed + "}";
  result.succeeded = ports.realtime(body, ports.uuid(), correlation,
      [&result, &ports](const TurnOperationObservation& operation) {
        if (!ports.current()) return false;
        result.final = operation; return true;
      });
  return result;
}
