#pragma once

#include "../physical_effects/PhysicalPresentation.h"

// This is a local, opt-in DV01 boundary, not a second director or a wire API.
// The existing session owner supplies its current trusted correlation context.
enum class SceneIntentKind { Wake, Consider, ChoiceAccent, Farewell };
struct SceneIntentContext {
  std::string bootId, sessionId, responseId, decisionId, selectionOperationId;
  uint64_t sessionEpoch = 0, responseGeneration = 0, phaseRevision = 0;
  uint64_t deviceGeneration = 0, validUntilDeviceMs = 0;
};
struct SceneIntent {
  std::string intentId, bootId, sessionId, responseId, decisionId;
  SceneIntentKind kind = SceneIntentKind::Consider;
  uint64_t sessionEpoch = 0, responseGeneration = 0, phaseRevision = 0;
  uint64_t issuedAtDeviceMs = 0, validUntilDeviceMs = 0;
};
enum class SceneIntentResult {
  Started, AttachedToSelection, Duplicate, Conflict, Stale, Expired,
  BusyOrUnsupported, CapacityExceeded
};

class SceneIntentBridge final {
 public:
  explicit SceneIntentBridge(PhysicalPresentation& owner) : owner_(owner) {}
  void SetContext(const SceneIntentContext& context);
  SceneIntentResult Accept(const SceneIntent& intent, uint64_t nowMs);
  void CancelResponse(const std::string& responseId, uint64_t responseGeneration);
  static constexpr bool Dv01IntegrationPresent = false;
 private:
  struct Seen { PresentationIdentity<97> id; uint64_t fingerprint = 0; };
  static uint64_t Fingerprint(const SceneIntent& intent);
  PhysicalPresentation& owner_;
  SceneIntentContext context_;
  std::array<Seen, 16> seen_{};
  size_t count_ = 0;
  std::string activeIntentId_;
};
