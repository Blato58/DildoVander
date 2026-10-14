#include "SceneIntentBridge.h"

namespace {
void Mix(uint64_t& hash, const std::string& value) {
  for (unsigned char byte : value) { hash ^= byte; hash *= 1099511628211ull; }
  hash ^= 0xff; hash *= 1099511628211ull;
}
void Mix(uint64_t& hash, uint64_t value) {
  for (unsigned byte = 0; byte < 8; ++byte) {
    hash ^= static_cast<uint8_t>(value >> (byte * 8)); hash *= 1099511628211ull;
  }
}
}

uint64_t SceneIntentBridge::Fingerprint(const SceneIntent& intent) {
  uint64_t hash = 14695981039346656037ull;
  Mix(hash, intent.bootId); Mix(hash, intent.sessionId); Mix(hash, intent.responseId);
  Mix(hash, intent.decisionId); Mix(hash, static_cast<uint64_t>(intent.kind));
  Mix(hash, intent.sessionEpoch); Mix(hash, intent.responseGeneration);
  Mix(hash, intent.phaseRevision); Mix(hash, intent.issuedAtDeviceMs);
  Mix(hash, intent.validUntilDeviceMs);
  return hash;
}

void SceneIntentBridge::SetContext(const SceneIntentContext& context) {
  const bool newSession = context.bootId != context_.bootId ||
      context.sessionId != context_.sessionId || context.sessionEpoch != context_.sessionEpoch;
  if (newSession || context.responseId != context_.responseId ||
      context.responseGeneration != context_.responseGeneration ||
      context.phaseRevision != context_.phaseRevision) {
    owner_.CancelAccent(activeIntentId_, context_.deviceGeneration);
    activeIntentId_.clear();
  }
  if (newSession) { count_ = 0; seen_ = {}; }
  context_ = context;
}

SceneIntentResult SceneIntentBridge::Accept(const SceneIntent& intent, uint64_t nowMs) {
  if (intent.intentId.empty() || intent.intentId.size() > 96 || context_.sessionId.empty() ||
      intent.bootId != context_.bootId || intent.sessionId != context_.sessionId ||
      intent.sessionEpoch != context_.sessionEpoch || intent.responseId != context_.responseId ||
      intent.responseGeneration != context_.responseGeneration ||
      intent.phaseRevision != context_.phaseRevision) return SceneIntentResult::Stale;
  // Absolute device time only: a repeated delivery cannot restart its TTL.
  if (nowMs < intent.issuedAtDeviceMs || nowMs >= intent.validUntilDeviceMs ||
      intent.validUntilDeviceMs > context_.validUntilDeviceMs ||
      intent.validUntilDeviceMs - intent.issuedAtDeviceMs > 2000) return SceneIntentResult::Expired;
  const uint64_t fingerprint = Fingerprint(intent);
  for (size_t index = 0; index < count_; ++index)
    if (intent.intentId == seen_[index].id.data()) return seen_[index].fingerprint == fingerprint
        ? SceneIntentResult::Duplicate : SceneIntentResult::Conflict;
  if (count_ == seen_.size()) return SceneIntentResult::CapacityExceeded;
  seen_[count_].id = intent.intentId; seen_[count_++].fingerprint = fingerprint;
  if (intent.kind == SceneIntentKind::ChoiceAccent) {
    // Only the existing prepared selection authority can later issue Raise.
    // Its authored presentation already owns the matching decorative LEDs.
    if (context_.selectionOperationId.empty() || context_.decisionId.empty() ||
        intent.decisionId != context_.decisionId) return SceneIntentResult::Stale;
    return SceneIntentResult::AttachedToSelection;
  }
  const char* scene = nullptr;
  switch (intent.kind) {
    case SceneIntentKind::Wake: scene = "wake"; break;
    case SceneIntentKind::Consider: scene = "consider"; break;
    case SceneIntentKind::Farewell: scene = "farewell"; break;
    case SceneIntentKind::ChoiceAccent: return SceneIntentResult::Stale;
    default: return SceneIntentResult::BusyOrUnsupported;
  }
  if (!owner_.StartAccent(scene, intent.intentId, context_.deviceGeneration,
                          intent.validUntilDeviceMs, nowMs)) return SceneIntentResult::BusyOrUnsupported;
  activeIntentId_ = intent.intentId;
  return SceneIntentResult::Started;
}

void SceneIntentBridge::CancelResponse(const std::string& responseId, uint64_t responseGeneration) {
  if (responseId != context_.responseId || responseGeneration != context_.responseGeneration) return;
  owner_.CancelAccent(activeIntentId_, context_.deviceGeneration);
  activeIntentId_.clear();
  // Revocation invalidates later deliveries for this exact response as well.
  context_.validUntilDeviceMs = 0;
}
