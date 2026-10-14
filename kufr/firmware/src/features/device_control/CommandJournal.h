#pragma once

#include <stdint.h>

#include <string>

// The device adapter must persist Begin before any physical output is allowed.
class CommandJournal {
 public:
  enum class Lookup { Missing, Same, Conflict };
  virtual ~CommandJournal() {}

  virtual bool HasPending() const = 0;
  virtual uint8_t PendingCompartment() const = 0;
  virtual uint8_t ReadyMask() const = 0;
  virtual bool Begin(const std::string& commandId, uint8_t compartment) = 0;
  virtual bool BeginWithIdentity(const std::string& commandId, uint8_t compartment,
                                uint64_t) { return Begin(commandId, compartment); }
  virtual Lookup Find(const std::string&, uint8_t, uint64_t) const { return Lookup::Missing; }
  // Persist the new availability mask and close the record atomically.
  virtual bool Resolve(uint8_t readyMask) = 0;
  // Close a presentation only after its prop is confirmed back at rest.
  // Legacy selection journals fail closed until they support this transition.
  virtual bool ResolveRestored(uint8_t) { return false; }
  // Persist a confirmed reload without opening or closing a selection record.
  virtual bool PersistReadyMask(uint8_t readyMask) = 0;
};
