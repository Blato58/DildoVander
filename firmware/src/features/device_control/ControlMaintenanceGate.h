#pragma once

#include <atomic>
#include <cstdint>

// A control pass and a maintenance worker reserve ownership using bounded RAM
// operations. Neither owns a mutex across its work. A maintenance-held pass must
// still process Stop, output shutdown and status, while refusing new admission.
class ControlMaintenanceGate final {
 public:
  enum class Owner : uint32_t { None = 0, Network = 3, Ota = 4, Pairing = 5 };
  class Ticket final {
   public:
    Ticket() = default;
    Ticket(const Ticket&) = delete;
    Ticket& operator=(const Ticket&) = delete;
    bool Active() const { return value_ != 0; }
   private:
    friend class ControlMaintenanceGate;
    uint32_t value_ = 0;
    const ControlMaintenanceGate* gate_ = nullptr;
  };

  bool BeginControl(Ticket& ticket) {
    if (ticket.Active()) return false;
    uint32_t previous = state_.load();
    const uint32_t mode = previous & ModeMask;
    if (mode != Unsafe && mode != Safe) return false;
    return Reserve(previous, Control, ticket);
  }
  bool EndControl(Ticket& ticket, bool safeIdle) {
    return Release(ticket, Control, safeIdle ? Safe : Unsafe);
  }
  bool Acquire(Owner owner, Ticket& ticket) {
    if (ticket.Active() || (owner != Owner::Network && owner != Owner::Ota && owner != Owner::Pairing))
      return false;
    uint32_t previous = state_.load();
    if ((previous & ModeMask) != Safe) return false;
    return Reserve(previous, static_cast<uint32_t>(owner), ticket);
  }
  bool Release(Owner owner, Ticket& ticket) {
    if (owner != Owner::Network && owner != Owner::Ota && owner != Owner::Pairing) return false;
    // A fresh completed control pass must republish idle after maintenance.
    return Release(ticket, static_cast<uint32_t>(owner), Unsafe);
  }
  bool TransferControl(Ticket& control, Owner owner, Ticket& maintenance) {
    if (!control.Active() || control.gate_ != this || (control.value_ & ModeMask) != Control || maintenance.Active() ||
        (owner != Owner::Network && owner != Owner::Ota && owner != Owner::Pairing)) return false;
    uint32_t previous = control.value_;
    const uint32_t desired = (previous & ~ModeMask) | static_cast<uint32_t>(owner);
    if (!state_.compare_exchange_strong(previous, desired)) return false;
    control.value_ = 0;
    control.gate_ = nullptr;
    maintenance.value_ = desired;
    maintenance.gate_ = this;
    return true;
  }
  Owner HeldBy() const {
    const uint32_t mode = state_.load() & ModeMask;
    return mode >= static_cast<uint32_t>(Owner::Network) && mode <= static_cast<uint32_t>(Owner::Pairing)
        ? static_cast<Owner>(mode) : Owner::None;
  }

 private:
  static constexpr uint32_t ModeMask = 7, Unsafe = 0, Safe = 1, Control = 2;
  static constexpr uint32_t MaximumGeneration = UINT32_MAX >> 3;
  bool Reserve(uint32_t previous, uint32_t mode, Ticket& ticket) {
    const uint32_t generation = previous >> 3;
    if (generation == MaximumGeneration) return false;
    const uint32_t desired = ((generation + 1) << 3) | mode;
    if (!state_.compare_exchange_strong(previous, desired)) return false;
    ticket.value_ = desired;
    ticket.gate_ = this;
    return true;
  }
  bool Release(Ticket& ticket, uint32_t requiredMode, uint32_t nextMode) {
    if (!ticket.Active() || ticket.gate_ != this || (ticket.value_ & ModeMask) != requiredMode) return false;
    uint32_t previous = ticket.value_;
    ticket.value_ = 0;
    ticket.gate_ = nullptr;
    return state_.compare_exchange_strong(previous, (previous & ~ModeMask) | nextMode);
  }
  // Unsafe until an actual completed control pass publishes its full idle view.
  std::atomic<uint32_t> state_{Unsafe};
};
