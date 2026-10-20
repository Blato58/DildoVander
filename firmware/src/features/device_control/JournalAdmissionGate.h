#pragma once

#include <cstdint>

// Runtime binding and committed-source authority, separate from physical
// inspection. Every method is a RAM-only query/request on the control path.
class JournalAdmissionGate {
 public:
  virtual ~JournalAdmissionGate() = default;
  virtual bool AllowsAdmission() const = 0;
  virtual bool AllowsRecovery() const = 0;
  virtual bool ReconcileBeforeResume(uint32_t cancellation) = 0;
};
