#include "../voice_turn/LocalVoicePort.h"
#include "LocalControlOutput.h"
#include "LocalControlCapabilities.h"

#include "LocalControlTransport.h"
#include "../conversation/ConversationStartCodec.h"
#include "../device_control/PersistentCommandJournal.h"
#include "../safety/SafetyController.h"
#include "../../protocol/JsonWriter.h"

namespace {

constexpr uint64_t MaximumInteger = 9007199254740991ULL;

std::string DanceStatusJson(const kufr::dance::DanceStatus& status) {
  std::string json = "{\"available\":" + std::string(status.available ? "true" : "false") +
      ",\"phase\":\"" + status.phase + "\",\"track\":";
  if (status.track.empty()) json += "null";
  else AppendJsonString(status.track, json);
  json += ",\"positionMs\":" + std::to_string(status.positionMs) +
      ",\"durationMs\":" + std::to_string(status.durationMs) +
      ",\"bpm\":" + std::to_string(status.bpm) + ",\"error\":";
  if (status.error.empty()) json += "null";
  else AppendJsonString(status.error, json);
  return json + "}";
}

bool ValidDeviceId(const std::string& value) {
  if (value.empty() || value.size() > 64 || value[0] < 'a' || value[0] > 'z') {
    return false;
  }
  for (char character : value) {
    if (!((character >= 'a' && character <= 'z') ||
          (character >= '0' && character <= '9') || character == '-')) {
      return false;
    }
  }
  return true;
}

bool OptionalUuid(const std::string& value) {
  return value.empty() || IsConversationUuid(value);
}

std::string NullableUuid(const std::string& value) {
  return value.empty() ? "null" : "\"" + value + "\"";
}

const char* ErrorName(CommandError error) {
  switch (error) {
    case CommandError::None: return "none";
    case CommandError::InvalidMessage: return "invalid_message";
    case CommandError::Unauthorized: return "unauthorized";
    case CommandError::WrongBoot: return "wrong_boot";
    case CommandError::LeaseExpired: return "lease_expired";
    case CommandError::Expired: return "expired";
    case CommandError::StaleRevision: return "stale_revision";
    case CommandError::SequenceGap: return "sequence_gap";
    case CommandError::AlreadySeenResultUnavailable: return "already_seen_result_unavailable";
    case CommandError::IdConflict: return "id_conflict";
    case CommandError::Busy: return "busy";
    case CommandError::CompartmentUnavailable: return "compartment_unavailable";
    case CommandError::NotCalibrated: return "not_calibrated";
    case CommandError::SafetyLatched: return "safety_latched";
    case CommandError::JournalFailed: return "journal_failed";
  }
  return "invalid_message";
}

const char* ErrorName(ConversationCommandError error) {
  switch (error) {
    case ConversationCommandError::None: return "none";
    case ConversationCommandError::Unauthorized: return "unauthorized";
    case ConversationCommandError::WrongBoot: return "wrong_boot";
    case ConversationCommandError::WrongSession: return "wrong_session";
    case ConversationCommandError::WrongGeneration: return "wrong_generation";
    case ConversationCommandError::StaleRevision: return "stale_revision";
    case ConversationCommandError::InvalidText: return "invalid_message";
    case ConversationCommandError::Busy: return "busy";
    case ConversationCommandError::IdConflict: return "id_conflict";
    case ConversationCommandError::TurnAlreadySeen: return "already_seen_result_unavailable";
    case ConversationCommandError::TurnLimitReached: return "turn_limit_reached";
  }
  return "invalid_message";
}

}  // namespace

LocalControlOutput::LocalControlOutput(
    const std::string& deviceId, const std::string& bootId, DeviceControl& control,
    PersistentCommandJournal& journal, SafetyController& safety,
    SelectionSequence& selection, ConversationSession& conversation,
    LocalControlTransport& transport, LocalVoicePort* voice, kufr::dance::LocalDancePort* dance)
    : voice_(voice), dance_(dance), deviceId_(deviceId), bootId_(bootId), control_(control), journal_(journal),
      safety_(safety), selection_(selection), conversation_(conversation),
      transport_(transport) {}

bool LocalControlOutput::AdvanceEvent() {
  if (eventSequence_ >= MaximumInteger) {
    return false;
  }
  ++eventSequence_;
  return true;
}

bool LocalControlOutput::Send(
    const LocalSocketAdmission::Handle& handle, const std::string& json) {
  return ValidDeviceId(deviceId_) && IsConversationUuid(bootId_) &&
         json.size() <= 2048 && transport_.Send(handle, json);
}

const char* LocalControlOutput::Phase(bool liveLease) const {
  if (!journal_.IsHealthy() || safety_.IsFaulted()) {
    return "fault";
  }
  if (safety_.IsStopped()) {
    return "stopped";
  }
  if (dance_ && kufr::dance::IsDanceActive(dance_->DanceState())) return "dancing";
  if (control_.HasManualControl() && !liveLease) {
    return "recoveryRequired";
  }
  switch (selection_.CurrentPhase()) {
    case SelectionPhase::Searching: return "searching";
    case SelectionPhase::Highlighting: return "highlighting";
    case SelectionPhase::Vibrating: return "vibrating";
    case SelectionPhase::Releasing: return "releasing";
    case SelectionPhase::Idle: break;
  }
  if (control_.HasAutonomousPresentation()) {
    return "presenting";
  }
  if ((control_.NeedsRecovery() && !control_.HasPendingSelection()) ||
      control_.CalibrationRevision() == 0) {
    return "recoveryRequired";
  }
  return "ready";
}

bool LocalControlOutput::ResultChanged() const {
  const auto& result = control_.LastResult();
  return !result.commandId.empty() &&
         (rememberedResult_.commandId != result.commandId ||
          rememberedResult_.outcome != result.outcome ||
          rememberedResult_.revision != result.revision);
}

void LocalControlOutput::RememberResult() {
  rememberedResult_ = control_.LastResult();
  resultEventSequence_ = eventSequence_;
}

bool LocalControlOutput::BuildResult(
    uint64_t eventSequence, std::string& json) const {
  const auto& result = control_.LastResult();
  if (!IsConversationUuid(result.commandId) || result.compartment < 1 ||
      result.compartment > 6 || result.revision > MaximumInteger ||
      (result.outcome != CommandOutcome::Completed &&
       result.outcome != CommandOutcome::OutcomeUnknown)) {
    return false;
  }
  json = "{\"protocolVersion\":1,\"type\":\"selection.result\",\"commandId\":\"" +
         result.commandId + "\",\"bootId\":\"" + bootId_ +
         "\",\"eventSequence\":" + std::to_string(eventSequence) +
         ",\"status\":\"" +
         (result.outcome == CommandOutcome::Completed ? "completed" : "outcome_unknown") +
         "\",\"revision\":" + std::to_string(result.revision) +
         ",\"compartment\":" + std::to_string(result.compartment) +
         ",\"physicalVerification\":\"unavailable\",\"requiresReload\":true}";
  return true;
}

bool LocalControlOutput::BuildSnapshot(
    const std::string& leaseId, uint64_t nowMs, std::string& json) {
  if (!ValidDeviceId(deviceId_) || !IsConversationUuid(bootId_) ||
      !IsConversationUuid(conversation_.ConversationId()) ||
      !OptionalUuid(conversation_.ActiveTurnId()) || !OptionalUuid(leaseId) ||
      !OptionalUuid(control_.PendingCommandId()) || nowMs > MaximumInteger ||
      control_.Revision() > MaximumInteger ||
      control_.CalibrationRevision() > MaximumInteger ||
      conversation_.Generation() > MaximumInteger) {
    return false;
  }
  const bool liveLease = control_.OwnsLiveManualLease(leaseId, nowMs);
  const bool recovery = !journal_.IsHealthy() || safety_.NeedsRecovery() ||
      (control_.NeedsRecovery() && !control_.HasPendingSelection() &&
       !control_.HasAutonomousPresentation()) ||
      (control_.HasManualControl() && !liveLease) ||
      control_.CalibrationRevision() == 0;
  std::string lastResult = "null";
  if (!control_.LastResult().commandId.empty()) {
    if (ResultChanged()) {
      RememberResult();
    }
    if (!BuildResult(resultEventSequence_, lastResult)) {
      return false;
    }
  }
  const LocalVoiceStatus voiceStatus = voice_ ? voice_->VoiceStatus() : LocalVoiceStatus{};
  const char* microphone = !voiceStatus.available ? "unavailable" : voiceStatus.phase == "listening" ? "recording" :
      voiceStatus.phase == "speaking" ? "playing" : "idle";
  json = "{\"protocolVersion\":1,\"type\":\"state.snapshot\",\"deviceId\":\"" +
      deviceId_ + "\",\"bootId\":\"" + bootId_ +
      "\",\"deviceUptimeMs\":" + std::to_string(nowMs) +
      ",\"eventSequence\":" + std::to_string(eventSequence_) +
      ",\"revision\":" + std::to_string(control_.Revision()) +
      ",\"controlMode\":\"" + (liveLease ? "manual" : recovery ? "recovery" : "standalone") +
      "\",\"controlLeaseId\":" + NullableUuid(liveLease ? leaseId : "") +
      ",\"phase\":\"" + Phase(liveLease) +
      "\",\"activeCommandId\":" + NullableUuid(control_.PendingCommandId()) +
      ",\"readyMask\":" + std::to_string(journal_.IsHealthy() ? control_.ReadyMask() : 0) +
      ",\"calibrationRevision\":" + std::to_string(control_.CalibrationRevision()) +
      ",\"faults\":" + (journal_.IsHealthy() ? "[]" : "[\"journal_failed\"]") +
      ",\"microphoneState\":\"" + microphone + "\",\"capabilities\":" +
      LocalControlCapabilities::Json +
      ",\"lastResult\":" + lastResult +
      ",\"conversation\":{\"conversationId\":\"" + conversation_.ConversationId() +
      "\",\"generation\":" + std::to_string(conversation_.Generation()) +
      ",\"activeTurnId\":" + NullableUuid(conversation_.ActiveTurnId()) + "}" +
      (voice_ ? ",\"voice\":" + VoiceStatusJson(voiceStatus) : "") +
      (dance_ ? ",\"dance\":" + DanceStatusJson(dance_->DanceState()) : "") + "}";
  return true;
}

bool LocalControlOutput::Welcome(
    const LocalSocketAdmission::Handle& handle, const std::string& requestId,
    uint64_t nowMs) {
  std::string snapshot;
  return IsConversationUuid(requestId) && AdvanceEvent() &&
      BuildSnapshot("", nowMs, snapshot) &&
      Send(handle, "{\"protocolVersion\":1,\"type\":\"welcome\",\"requestMessageId\":\"" +
                     requestId + "\",\"snapshot\":" + snapshot + "}");
}

bool LocalControlOutput::Snapshot(
    const LocalSocketAdmission::Handle& handle, const std::string& leaseId,
    uint64_t nowMs) {
  std::string json;
  if (ResultChanged()) {
    if (!AdvanceEvent() || !BuildResult(eventSequence_, json) || !Send(handle, json)) {
      return false;
    }
    RememberResult();
  }
  return AdvanceEvent() && BuildSnapshot(leaseId, nowMs, json) && Send(handle, json);
}

bool LocalControlOutput::Heartbeat(
    const LocalSocketAdmission::Handle& handle, const LocalControlMessage& request,
    uint64_t nowMs) {
  if (!IsConversationUuid(request.messageId) ||
      !IsConversationUuid(request.controlLeaseId) ||
      !control_.OwnsLiveManualLease(request.controlLeaseId, nowMs) ||
      nowMs > MaximumInteger || control_.Revision() > MaximumInteger) {
    return false;
  }
  return Send(handle,
      "{\"protocolVersion\":1,\"type\":\"heartbeat.ack\",\"requestMessageId\":\"" +
      request.messageId + "\",\"bootId\":\"" + bootId_ +
      "\",\"deviceUptimeMs\":" + std::to_string(nowMs) +
      ",\"revision\":" + std::to_string(control_.Revision()) +
      ",\"controlLeaseId\":\"" + request.controlLeaseId + "\"}");
}

bool LocalControlOutput::CommandAck(
    const LocalSocketAdmission::Handle& handle, const LocalControlMessage& request,
    const CommandReceipt& receipt) {
  if (!IsConversationUuid(request.commandId) || control_.Revision() > MaximumInteger ||
      !AdvanceEvent()) {
    return false;
  }
  const bool accepted = receipt.error == CommandError::None;
  return Send(handle,
      "{\"protocolVersion\":1,\"type\":\"command.ack\",\"commandId\":\"" +
      request.commandId + "\",\"bootId\":\"" + bootId_ +
      "\",\"eventSequence\":" + std::to_string(eventSequence_) +
      ",\"revision\":" + std::to_string(control_.Revision()) +
      ",\"status\":\"" + (accepted ? "accepted" : "rejected") +
      "\",\"errorCode\":" +
      (accepted ? "null" : "\"" + std::string(ErrorName(receipt.error)) + "\"") + "}");
}

bool LocalControlOutput::ConversationAck(
    const LocalSocketAdmission::Handle& handle, const LocalControlMessage& request,
    const ConversationCommandReceipt& receipt) {
  if (!IsConversationUuid(request.messageId) ||
      !IsConversationUuid(conversation_.ConversationId()) ||
      (request.kind != LocalControlMessageKind::Cancel &&
       !IsConversationUuid(request.clientTurnId)) ||
      conversation_.Generation() > MaximumInteger || !AdvanceEvent()) {
    return false;
  }
  const bool accepted = receipt.error == ConversationCommandError::None;
  return Send(handle,
      "{\"protocolVersion\":1,\"type\":\"conversation.ack\",\"requestMessageId\":\"" +
      request.messageId + "\",\"bootId\":\"" + bootId_ +
      "\",\"conversationId\":\"" + conversation_.ConversationId() +
      "\",\"clientTurnId\":" + NullableUuid(
          request.kind == LocalControlMessageKind::Cancel ? "" : request.clientTurnId) +
      ",\"generation\":" + std::to_string(conversation_.Generation()) +
      ",\"eventSequence\":" + std::to_string(eventSequence_) +
      ",\"status\":\"" + (accepted ? "accepted" : "rejected") +
      "\",\"duplicate\":" + (receipt.duplicate ? "true" : "false") +
      ",\"errorCode\":" +
      (accepted ? "null" : "\"" + std::string(ErrorName(receipt.error)) + "\"") + "}");
}

bool LocalControlOutput::ConversationResult(
    const LocalSocketAdmission::Handle& handle, const std::string& clientTurnId,
    uint64_t generation, const std::string& reply) {
  if (!ValidDeviceId(deviceId_) || !IsConversationUuid(bootId_) ||
      !IsConversationUuid(conversation_.ConversationId()) ||
      !IsConversationUuid(clientTurnId) ||
      clientTurnId != conversation_.ActiveTurnId() ||
      generation != conversation_.Generation() || generation > MaximumInteger ||
      reply.size() > 2000 || !IsValidConversationUtf8(reply, 500, false) ||
      !AdvanceEvent()) {
    return false;
  }
  std::string json =
      "{\"protocolVersion\":1,\"type\":\"conversation.result\",\"bootId\":\"" +
      bootId_ + "\",\"conversationId\":\"" + conversation_.ConversationId() +
      "\",\"clientTurnId\":\"" + clientTurnId + "\",\"generation\":" +
      std::to_string(generation) + ",\"eventSequence\":" +
      std::to_string(eventSequence_) + ",\"reply\":";
  AppendJsonString(reply, json);
  json += ",\"simulated\":false}";
  // Escaping 500 codepoints can exceed the ordinary 2048-byte envelope.
  return json.size() <= 8192 && transport_.Send(handle, json);
}

bool LocalControlOutput::VoiceAck(const LocalSocketAdmission::Handle& handle,
                                 const LocalControlMessage& request, bool accepted) {
  if (!IsConversationUuid(request.messageId) || !AdvanceEvent()) return false;
  return Send(handle, "{\"protocolVersion\":1,\"type\":\"voice.ack\",\"bootId\":\"" + bootId_ +
      "\",\"requestMessageId\":\"" + request.messageId + "\",\"accepted\":" +
      (accepted ? "true" : "false") + ",\"voice\":" + VoiceStatusJson(voice_ ? voice_->VoiceStatus() : LocalVoiceStatus{}) + "}");
}

bool LocalControlOutput::DanceAck(const LocalSocketAdmission::Handle& handle,
                                 const LocalControlMessage& request, bool accepted) {
  if (!IsConversationUuid(request.messageId) || !AdvanceEvent()) return false;
  return Send(handle, "{\"protocolVersion\":1,\"type\":\"dance.ack\",\"bootId\":\"" + bootId_ +
      "\",\"requestMessageId\":\"" + request.messageId + "\",\"accepted\":" +
      (accepted ? "true" : "false") + ",\"dance\":" +
      DanceStatusJson(dance_ ? dance_->DanceState() : kufr::dance::DanceStatus{}) + "}");
}
