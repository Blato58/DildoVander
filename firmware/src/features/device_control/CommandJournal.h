#pragma once

#include <stdint.h>

#include <string>

// The device adapter must persist Begin before any physical output is allowed.
class CommandJournal {
 public:
  enum class Lookup { Missing, Same, Conflict, StaleEpoch, Unavailable };
  virtual ~CommandJournal() {}

  virtual bool HasPending() const = 0;
  virtual bool CanBeginOperation() const { return !HasPending(); }
  virtual uint8_t PendingCompartment() const = 0;
  virtual uint8_t ReadyMask() const = 0;
  virtual bool Begin(const std::string& commandId, uint8_t compartment) = 0;
  virtual bool BeginWithIdentity(const std::string& commandId, uint8_t compartment,
                                uint64_t) { return Begin(commandId, compartment); }
  virtual Lookup Find(const std::string&, uint8_t, uint64_t) const { return Lookup::Missing; }
  // Epoch zero is legacy-only. Producers must retain the original envelope's
  // epoch on every retry; observing a new epoch never authorizes restamping it.
  virtual uint64_t CurrentEpoch() const { return 0; }
  virtual bool BeginScoped(uint64_t, const std::string&, uint8_t, uint64_t) { return false; }
  virtual Lookup FindScoped(uint64_t, const std::string&, uint8_t, uint64_t) const {
    return Lookup::StaleEpoch;
  }
  // Historical evidence lookup does not authorize admission in the current epoch.
  virtual Lookup FindHistorical(uint64_t, const std::string&, uint8_t, uint64_t) const {
    return Lookup::Unavailable;
  }
  virtual bool ConfirmTimedPresentationScoped(uint64_t, const std::string&, uint8_t, uint64_t) { return false; }
  virtual bool WasTimedPresentationCompletedScoped(uint64_t, const std::string&, uint8_t, uint64_t) const { return false; }
  virtual bool WasTimedPresentationCompletedHistorical(uint64_t, const std::string&, uint8_t, uint64_t) const { return false; }
  // Persist the new availability mask and close the record atomically.
  virtual bool Resolve(uint8_t readyMask) = 0;
  // Close a presentation only after its prop is confirmed back at rest.
  // Legacy selection journals fail closed until they support this transition.
  virtual bool ResolveRestored(uint8_t) { return false; }
  // DV10 historical evidence: record only after autonomous completion and its
  // existing post-persist timing validation. Legacy admission/ready state is not proof.
  virtual bool ConfirmTimedPresentation(const std::string&, uint8_t, uint64_t) { return false; }
  virtual bool WasTimedPresentationCompleted(const std::string&, uint8_t) const { return false; }
  // Persist a confirmed reload without opening or closing a selection record.
  virtual bool PersistReadyMask(uint8_t readyMask) = 0;
};
