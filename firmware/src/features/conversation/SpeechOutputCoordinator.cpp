#include "SpeechOutputCoordinator.h"

#include "ConversationStartCodec.h"
#include "TurnPollSchedule.h"

namespace {
bool Matches(const TurnCorrelation& expected, const TurnCorrelation& actual) {
  return actual.deviceId == expected.deviceId && actual.bootId == expected.bootId &&
         actual.conversationId == expected.conversationId && actual.clientTurnId == expected.clientTurnId &&
         actual.generation == expected.generation && actual.deviceRevision == expected.deviceRevision;
}
}  // namespace

SpeechOutputOutcome PrepareSpeechOutput(
    const std::string& sourceOperationId, const TurnCorrelation& expected,
    const TurnOperationObservation& source,
    const ConversationRuntimeSnapshot& snapshot,
    const ConversationRuntimeState& runtime, bool audioConsented,
    uint64_t turnStartedAtMs, const std::string& requestKey,
    const std::string& releaseKey, const char* token,
    SpeechTransportPort& transport, SpeechClockPort& clock,
    SpeechAudioBuffer& buffer) {
  buffer.Clear();
  const auto stillCurrent = [&]() {
    return runtime.Matches(snapshot) && snapshot.generation == expected.generation &&
           snapshot.deviceRevision == expected.deviceRevision;
  };
  const auto withinDeadline = [&]() {
    const uint64_t now = clock.NowMs();
    return now >= turnStartedAtMs && now - turnStartedAtMs < TurnPollSchedule::DeadlineMs;
  };
  const auto decision = EvaluateTurnOperationWithProposal(
      sourceOperationId, expected, snapshot.generation, snapshot.deviceRevision,
      snapshot.catalogVersion, snapshot.readyMask, clock.UtcSeconds(), source);
  if (!audioConsented || !stillCurrent() || token == nullptr ||
      !IsConversationUuid(requestKey) || !IsConversationUuid(releaseKey) ||
      clock.UtcSeconds() == 0 ||
      (decision != TurnPollDecision::ReplyReady && decision != TurnPollDecision::ProposalReady)) {
    return SpeechOutputOutcome::Rejected;
  }
  if (!withinDeadline()) { return SpeechOutputOutcome::Expired; }

  std::string operationId;
  int status = transport.Submit(expected.conversationId, expected.clientTurnId,
                                requestKey, token, operationId);
  if ((status == 0 || status >= 500) && stillCurrent() && withinDeadline()) {
    // Lost backend acceptance can be retried with exactly the same key/body.
    status = transport.Submit(expected.conversationId, expected.clientTurnId,
                              requestKey, token, operationId);
  }
  if (status != 202 || !IsConversationUuid(operationId)) { return SpeechOutputOutcome::Failed; }

  const auto finish = [&](SpeechOutputOutcome outcome) {
    if (outcome != SpeechOutputOutcome::Ready) { buffer.Clear(); }
    // Release remote audio immediately after full download; local bytes retain
    // their original TTL/snapshot. A lost cleanup response cannot trigger TTS.
    transport.Release(operationId, releaseKey, token);
    if (outcome == SpeechOutputOutcome::Ready &&
        (!stillCurrent() || !withinDeadline() || !buffer.HasReadyPcm(clock.NowMs(), runtime))) {
      buffer.Clear();
      return stillCurrent() ? SpeechOutputOutcome::Expired : SpeechOutputOutcome::Cancelled;
    }
    return outcome;
  };
  TurnPollSchedule schedule;
  schedule.Begin(turnStartedAtMs, clock.NowMs());
  while (withinDeadline()) {
    if (!stillCurrent()) { return finish(SpeechOutputOutcome::Cancelled); }
    if (!schedule.MarkStarted(clock.NowMs())) {
      clock.WaitMs(10);
      continue;
    }
    SpeechOperationObservation result;
    status = transport.Poll(operationId, token, result);
    if (!stillCurrent()) { return finish(SpeechOutputOutcome::Cancelled); }
    if (!withinDeadline()) { break; }
    if (status == 200) {
      if (result.operationId != operationId || !Matches(expected, result.correlation)) {
        return finish(SpeechOutputOutcome::Failed);
      }
      if (result.status == TurnOperationStatus::Completed) {
        if (SpeechRemainingMs(operationId, expected, clock.UtcSeconds(), result) == 0) {
          return finish(SpeechOutputOutcome::Failed);
        }
        status = transport.Download(operationId, expected, result, snapshot, runtime,
                                    clock.UtcSeconds(), token, buffer);
        if (!stillCurrent()) { return finish(SpeechOutputOutcome::Cancelled); }
        if (!withinDeadline()) { return finish(SpeechOutputOutcome::Expired); }
        if (status != 200) { return finish(SpeechOutputOutcome::Failed); }
        return finish(SpeechOutputOutcome::Ready);
      }
      if (result.status != TurnOperationStatus::Pending && result.status != TurnOperationStatus::Running) {
        return finish(SpeechOutputOutcome::Failed);
      }
    } else if (status != 0 && status != 429 && status < 500) {
      return finish(SpeechOutputOutcome::Failed);
    }
    schedule.MarkCompleted(clock.NowMs(), true);
  }
  return finish(SpeechOutputOutcome::Expired);
}
