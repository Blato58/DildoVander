#pragma once

#include <cassert>
#include "../src/features/physical_effects/PhysicalPresentation.h"

class FakeIo final : public PhysicalIo {
 public:
  PhysicalInputs inputs{false};
  bool run = false, fail = false;
  int servoWrites = 0, vibrationWrites = 0, clears = 0, lightWrites = 0;
  uint8_t channel = 0;
  uint16_t pulse = 0;
  std::array<uint8_t, 108> pixels{};
  void (*lightsCallback)(void*) = nullptr;
  void* lightsContext = nullptr;
  void EnableOutputs(bool enabled) override { run = enabled; }
  bool FullOff() override { assert(!run); ++clears; return !fail; }
  bool Servo(uint8_t selected, uint16_t us) override {
    assert(!run); channel = selected; pulse = us; ++servoWrites; return !fail;
  }
  bool Vibration(uint8_t selected, bool enabled) override {
    assert(!run && enabled); channel = selected; ++vibrationWrites; return !fail;
  }
  bool Lights(const std::array<uint8_t, 108>& frame) override {
    unsigned litSegments = 0;
    for (unsigned first = 0; first < frame.size(); first += 18) {
      bool lit = false;
      for (unsigned i = first; i < first + 18; ++i) lit |= frame[i] != 0;
      if (lit) ++litSegments;
    }
    assert(litSegments <= 1);
    ++lightWrites;
    pixels = frame;
    if (lightsCallback) lightsCallback(lightsContext);
    return !fail;
  }
  bool ReadInputs(PhysicalInputs& value) override { value = inputs; return !fail; }
};

// The parity runner compiles this same fixture against the historical core.
// Optional async test hooks must retain that base's original surface too.
template <typename Base, typename = void> struct JournalStorageStateType {
  enum class Type { Rejected, Pending, Committed };
};
template <typename Base> struct JournalStorageStateType<Base,
    decltype(void(sizeof(typename Base::StorageState)))> {
  using Type = typename Base::StorageState;
};

class Journal final : public CommandJournal {
 public:
  using StorageState = typename JournalStorageStateType<CommandJournal>::Type;
  bool pending = false, fail = false;
  uint8_t selected = 0, mask = 63;
  int begins = 0, restores = 0;
  std::string operationId, completedId;
  uint8_t completedSlot = 0;
  bool holdClose = false, holdProof = false;
  bool closeOutstanding = false, proofOutstanding = false;
  StorageState storageState = StorageState::Rejected;
  StorageState LastStorageState() const { return storageState; }
  bool HasPendingStorage() const { return closeOutstanding || proofOutstanding; }
  bool HasPending() const override { return pending; }
  uint8_t PendingCompartment() const override { return selected; }
  uint8_t ReadyMask() const override { return mask; }
  bool Begin(const std::string& id, uint8_t compartment) override {
    if (fail) return false;
    ++begins; pending = true; selected = compartment; operationId = id; return true;
  }
  bool Resolve(uint8_t value) override { mask = value; pending = false; return !fail; }
  bool ResolveRestored(uint8_t value) override {
    if (fail || !pending || value != selected) return false;
    if (holdClose) {
      closeOutstanding = true;
      storageState = StorageState::Pending;
      return false;
    }
    closeOutstanding = false;
    storageState = StorageState::Committed;
    ++restores; pending = false; selected = 0; return true;
  }
  bool PersistReadyMask(uint8_t value) override { mask = value; return !fail; }
  // No override keyword for these two hooks: the unchanged legacy probe also
  // compiles this helper against the historical journal without these methods.
  // Their matching signatures still override the current virtual journal hooks.
  bool ConfirmTimedPresentation(const std::string& id, uint8_t slot, uint64_t) {
    if (fail || pending || operationId != id) return false;
    if (holdProof) {
      proofOutstanding = true;
      storageState = StorageState::Pending;
      return false;
    }
    proofOutstanding = false;
    storageState = StorageState::Committed;
    completedId = id; completedSlot = slot; return true;
  }
  bool WasTimedPresentationCompleted(const std::string& id, uint8_t slot) const {
    return completedId == id && completedSlot == slot;
  }
};

class SafetyAdapter final : public SafetyPort {
 public:
  explicit SafetyAdapter(PhysicalEffects& effects) : effects_(effects) {}
  void DisableActuators() override { effects_.Disable(); }
  void StartVibration(uint8_t n) override { if (!effects_.StartVibration(n)) effects_.Fault(); }
  void ReleaseLatch(uint8_t n) override { if (!effects_.Move(n, true)) effects_.Fault(); }
  void LowerProp(uint8_t n) override { if (!effects_.Move(n, false)) effects_.Fault(); }
  uint32_t MotionTimeoutMs(uint8_t n) const override { return effects_.MotionTimeoutMs(n); }
  bool PropRaised(uint8_t n) const override { return effects_.IsRaised(n); }
  bool PropResting(uint8_t n) const override { return effects_.IsResting(n); }
 private:
  PhysicalEffects& effects_;
};

struct Fixture {
  FakeIo io;
  PhysicalEffects effects{io};
  SafetyAdapter port{effects};
  SafetyController safety{port};
  SelectionSequence sequence{safety, true};
  Journal journal;
  DeviceControl control{"device", "boot", sequence, journal, 1};
  ConversationRuntimeState state{false, 0, ""};
  DeviceControlRuntime runtime{control, sequence, state, "catalog"};
  PhysicalPresentation adapter{effects, runtime};
  WandPresentation presentation{adapter};
  std::array<ServoCalibration, 6> calibration{};
  explicit Fixture(uint16_t travelMs = 500) {
    calibration.fill({1100, 1900, travelMs});
    assert(effects.Configure(calibration, 1));
    effects.Tick(0); effects.Tick(30);
    assert(effects.RecoverAfterInspection());
    assert(runtime.BindPresentation(presentation));
    assert(runtime.RecoverAfterInspection());
  }
  void Tick(uint64_t now) {
    presentation.CheckDeadline(now);
    effects.Tick(now);
    SelectionStep step;
    runtime.Tick(now, step);
    presentation.CheckDeadline(now);
    adapter.Tick(now);
  }
  bool Start(const std::string& id, uint64_t now = 30) {
    ConversationRuntimeSnapshot snapshot;
    assert(state.Read(snapshot));
    TurnProposal proposal;
    proposal.proposalId = id;
    proposal.compartment = 3;
    proposal.catalogVersion = snapshot.catalogVersion;
    proposal.generation = snapshot.generation;
    proposal.deviceRevision = snapshot.deviceRevision;
    PendingProposal pending;
    assert(pending.Store(proposal, snapshot, state, now, 30000));
    adapter.Tick(now);
    return presentation.Start(pending, state, now);
  }

};
