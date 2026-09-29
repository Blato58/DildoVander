#pragma once

#include <stdint.h>

#include <string>

// The device adapter must persist Begin before any physical output is allowed.
class CommandJournal {
 public:
  virtual ~CommandJournal() {}

  virtual bool HasPending() const = 0;
  virtual uint8_t PendingCompartment() const = 0;
  virtual uint8_t ReadyMask() const = 0;
  virtual bool Begin(const std::string& commandId, uint8_t compartment) = 0;
  // Persist the new availability mask and close the record atomically.
  virtual bool Resolve(uint8_t readyMask) = 0;
};
