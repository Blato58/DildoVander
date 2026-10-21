#pragma once

#include <array>
#include <cstddef>
#include <cstdint>
#include <string>

struct JournalOperationIdentity {
  uint64_t epoch = 0;
  std::string operationId;
  uint8_t compartment = 0;
  uint64_t fingerprint = 0;
};

struct JournalHistoryEntry {
  JournalOperationIdentity identity;
  bool timedCompleted = false;
  bool evidencePinned = false;
  bool technicalTest = false;
};

inline char JournalIdentityLower(char value) {
  return value >= 'A' && value <= 'F' ? static_cast<char>(value + ('a' - 'A')) : value;
}

inline bool ValidJournalOperationIdentity(const JournalOperationIdentity& identity) {
  if (identity.operationId.size() != 36 || identity.compartment < 1 ||
      identity.compartment > 6 || (identity.epoch && !identity.fingerprint)) return false;
  for (size_t index = 0; index < identity.operationId.size(); ++index) {
    const char value = JournalIdentityLower(identity.operationId[index]);
    if (index == 8 || index == 13 || index == 18 || index == 23) {
      if (value != '-') return false;
    } else if (!((value >= '0' && value <= '9') || (value >= 'a' && value <= 'f'))) {
      return false;
    }
  }
  return true;
}

inline bool SameJournalOperationIdentity(const JournalOperationIdentity& left,
                                         const JournalOperationIdentity& right) {
  if (left.epoch != right.epoch || left.compartment != right.compartment ||
      left.fingerprint != right.fingerprint || left.operationId.size() != right.operationId.size()) return false;
  for (size_t index = 0; index < left.operationId.size(); ++index) {
    if (JournalIdentityLower(left.operationId[index]) != JournalIdentityLower(right.operationId[index])) return false;
  }
  return true;
}

enum class JournalInventoryOrigin : uint8_t { Missing, Valid, Unavailable };

// Only a committed, complete inventory read under the common owner barrier may
// authorize pin decisions. A missing blob and a RAM retirement request do not.
struct JournalEvidenceInventory {
  static constexpr size_t Capacity = 64;
  JournalInventoryOrigin origin = JournalInventoryOrigin::Unavailable;
  std::string deviceId;
  uint64_t generation = 0;
  bool complete = false;
  bool ambiguous = false;
  size_t count = 0;
  std::array<JournalOperationIdentity, Capacity> references{};
};

class JournalEvidenceSource {
 public:
  virtual ~JournalEvidenceSource() = default;
  virtual bool ReadCommitted(JournalEvidenceInventory& inventory) const = 0;
};

// Explicit service-image binding only. Runtime reads/commits do not infer this
// authority from a missing store or an unbound legacy payload.
class JournalServiceInventory : public JournalEvidenceSource {
 public:
  virtual bool EstablishBinding(const std::string& deviceId, bool serviceAuthorized) = 0;
};

// All producers, inventory commits and persisted-journal readers must share this
// barrier before production reclamation can be enabled. Late producers must
// revalidate their original epoch and pin evidence before committing a reference.
class JournalReconciliationBarrier {
 public:
  virtual ~JournalReconciliationBarrier() = default;
  virtual bool TryEnter() = 0;
  virtual void Leave() = 0;
};
