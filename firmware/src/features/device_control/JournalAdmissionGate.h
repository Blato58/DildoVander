#pragma once

#include <cstdint>

enum class InspectedRecoveryProgress : uint8_t { Pending, Succeeded, Rejected, Unknown, Cancelled };

// Runtime binding and committed-source authority, separate from physical
// inspection. Every method is a RAM-only query/request on the control path.
class JournalAdmissionGate {
 public:
  virtual ~JournalAdmissionGate() = default;
  virtual bool AllowsAdmission() const = 0;
  virtual bool AllowsRecovery() const = 0;
  virtual bool ReconcileBeforeResume(uint32_t cancellation) = 0;
  virtual InspectedRecoveryProgress AdvanceRecoveryResume(uint32_t cancellation) {
    return ReconcileBeforeResume(cancellation) ? InspectedRecoveryProgress::Succeeded : InspectedRecoveryProgress::Pending;
  }
};
