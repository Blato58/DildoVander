#include <cassert>
#include <iostream>
#include <vector>
#include "../src/features/voice_turn/VoiceWorkflow.h"

int main() {
  const std::string id = "ac665612-864c-4482-bb68-7415921119ae";
  const std::string fresh = "3d6104de-98c6-40b5-b10d-4f5675a63c51";
  VoiceWorkflowRequest request;
  request.requestId = id; request.voice = "cedar"; request.imageConsent = true;
  request.correlation.bootId = id; request.correlation.deviceId = "workflow-test";
  request.snapshot.catalogVersion = "ollivander-private-party-v1";
  request.snapshot.generation = 4; request.snapshot.deviceRevision = 37; request.snapshot.readyMask = 63;
  std::vector<std::string> order;
  bool current = true;
  unsigned creates = 0;
  VoiceWorkflowPorts ports;
  ports.current = [&]() { return current; };
  ports.uuid = [&]() { return fresh; };
  ports.close = [&]() { order.push_back("close"); return true; };
  ports.create = [&](bool photo, const std::string& key, std::string& conversation) {
    ++creates; order.push_back(photo ? "create-photo" : "create-no-photo");
    assert(key == (creates == 1 ? id : fresh));
    conversation = creates == 1 ? id : fresh; return true;
  };
  ports.photo = [&](const TurnCorrelation& correlation) {
    assert(correlation.conversationId == id && correlation.clientTurnId == fresh);
    order.push_back("photo-once"); return PhotoOutcome::Failed;
  };
  ports.beginAudio = [&]() { order.push_back("audio"); };
  ports.realtime = [&](const std::string& body, const std::string& key, const TurnCorrelation& correlation,
      const std::function<bool(const TurnOperationObservation&)>& completed) {
    order.push_back("realtime"); assert(key == fresh && correlation.conversationId == fresh);
    assert(body.find("\"opening\":true,\"text\":\"\",\"wav\":null,\"voice\":\"cedar\",\"speed\":1}") != std::string::npos);
    assert(body.find("\"readyMask\":63,\"deviceRevision\":37") != std::string::npos);
    TurnOperationObservation operation;
    operation.decision = "no_selection";
    return completed(operation);
  };
  const auto result = RunVoiceWorkflow(request, ports);
  assert(result.succeeded && result.photo == PhotoOutcome::Failed && result.final.decision == "no_selection");
  assert(order == std::vector<std::string>({"close", "create-photo", "photo-once", "close", "create-no-photo", "audio", "realtime"}));
  creates = 0; order.clear();
  ports.photo = [&](const TurnCorrelation&) { current = false; return PhotoOutcome::Cancelled; };
  assert(!RunVoiceWorkflow(request, ports).succeeded);
  assert(order == std::vector<std::string>({"close", "create-photo", "close"}));
  order.clear();
  assert(!RunVoiceWorkflow(request, ports).succeeded && order.empty());
  std::cout << "Shared voice workflow photo fallback, ordering and cancellation passed\n";
}
