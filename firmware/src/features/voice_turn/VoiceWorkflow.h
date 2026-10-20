#pragma once
#include <functional>
#include <string>
#include "../conversation/ConversationRuntimeState.h"
#include "../conversation/TurnPollGuard.h"
#include "../costume_photo/CostumePhoto.h"

struct VoiceWorkflowRequest {
  TurnCorrelation correlation;
  ConversationRuntimeSnapshot snapshot;
  std::string requestId, voice, speed = "1";
  bool imageConsent = false;
};
struct VoiceWorkflowPorts {
  std::function<bool()> current, close;
  std::function<std::string()> uuid;
  std::function<bool(bool, const std::string&, std::string&)> create;
  std::function<PhotoOutcome(const TurnCorrelation&)> photo;
  std::function<void()> beginAudio;
  std::function<bool(const std::string&, const std::string&, const TurnCorrelation&,
      const std::function<bool(const TurnOperationObservation&)>&)> realtime;
};
struct VoiceWorkflowResult {
  bool succeeded = false;
  PhotoOutcome photo = PhotoOutcome::Skipped;
  TurnOperationObservation final;
};
// Shared production ordering: close/create, one photo, fresh no-photo fallback,
// then microphone/output, then a correlated terminal operation. Admission and
// PendingProposal remain exclusively with the control owner.
VoiceWorkflowResult RunVoiceWorkflow(const VoiceWorkflowRequest& request,
    const VoiceWorkflowPorts& ports);
