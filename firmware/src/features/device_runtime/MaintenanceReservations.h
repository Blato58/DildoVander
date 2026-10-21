#pragma once

#include "../device_control/ControlMaintenanceGate.h"

namespace kufr { namespace runtime {

struct MaintenanceReservations {
  ControlMaintenanceGate ownership;
  ControlMaintenanceGate::Ticket network, ota;
  class PairingReservation final {
   public:
    explicit PairingReservation(MaintenanceReservations& gate) : gate_(gate) {
      acquired_ = gate_.ownership.Acquire(ControlMaintenanceGate::Owner::Pairing, ticket_);
    }
    ~PairingReservation() {
      if (acquired_) gate_.ownership.Release(ControlMaintenanceGate::Owner::Pairing, ticket_);
    }
    bool Acquired() const { return acquired_; }
   private:
    MaintenanceReservations& gate_;
    ControlMaintenanceGate::Ticket ticket_;
    bool acquired_ = false;
  };
  static bool AcquireNetwork(void* context) {
    auto& gate = *static_cast<MaintenanceReservations*>(context);
    return gate.ownership.Acquire(ControlMaintenanceGate::Owner::Network, gate.network);
  }
  static void ReleaseNetwork(void* context) {
    auto& gate = *static_cast<MaintenanceReservations*>(context);
    gate.ownership.Release(ControlMaintenanceGate::Owner::Network, gate.network);
  }
};

} }
