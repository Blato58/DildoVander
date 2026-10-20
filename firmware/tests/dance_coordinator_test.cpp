#include "../src/features/dance/DanceCoordinator.h"
#include <cassert>
#include <iostream>
#include <vector>

using namespace kufr::dance;
struct Player final : DancePlayerPort {
  DanceStatus state;
  unsigned starts = 0, stops = 0;
  Player() { state.available = true; state.phase = "idle"; state.bpm = 120; state.red = 255; state.vibrationEveryBeats = 2; }
  DanceStatus Status() const override { return state; }
  bool Start(const std::string&) override { ++starts; state.phase = "starting"; return true; }
  void Stop() override { ++stops; if (IsDanceActive(state)) state.phase = "stopping"; }
};
struct Io final : PhysicalIo {
  unsigned servos = 0, vibrations = 0, fullOffs = 0, lights = 0;
  bool failLights = false, enabled = false;
  std::array<uint8_t, 108> pixels{};
  void EnableOutputs(bool value) override { enabled = value; }
  bool FullOff() override { ++fullOffs; enabled = false; return true; }
  bool Servo(uint8_t, uint16_t) override { ++servos; return true; }
  bool Vibration(uint8_t, bool value) override { if (value) ++vibrations; return true; }
  bool Lights(const std::array<uint8_t, 108>& value) override { ++lights; pixels = value; return !failLights; }
  bool ReadInputs(PhysicalInputs&) override { return true; }
  unsigned LitSegments() const {
    unsigned count = 0;
    for (unsigned segment = 0; segment < 6; ++segment) {
      bool lit = false;
      for (unsigned i = segment * 18; i < (segment + 1) * 18; ++i) lit = lit || pixels[i];
      if (lit) ++count;
    }
    return count;
  }
};
int main() {
  {
    Player longPlayer; Io longIo; PhysicalEffects longEffects(longIo);
    DanceCoordinator longDance(longPlayer, longEffects);
    std::array<ServoCalibration, 6> values;
    values.fill({700, 1700, 20});
    assert(longEffects.Configure(values, 1)); longEffects.Tick(0);
    assert(longEffects.RecoverAfterInspection());
    longDance.BindBootIdentity("synthetic-dance");
    assert(longDance.StartDance("awkward-bpm"));
    assert(std::string(longDance.Diagnostics().sceneHash).empty());
    assert(std::string(longDance.Diagnostics().variant) == "delegated-owner");
    longPlayer.state.phase = "playing"; longPlayer.state.bpm = 137;
    longPlayer.state.vibrationEveryBeats = 0;
    // Absolute media arithmetic across one hour, never rounded beat periods.
    for (uint32_t position = 0; position < 3600000; position += 173) {
      longPlayer.state.positionMs = position;
      longEffects.Tick(position); longDance.Tick(position);
      assert(longDance.Diagnostics().beatIndex == static_cast<uint64_t>(position) * 137 / 60000);
      assert(longIo.servos == 0 && longIo.vibrations == 0);
    }
    assert(longDance.Diagnostics().droppedEvents > 0);
    const uint32_t generation = longDance.Diagnostics().audioGeneration;
    longPlayer.state.positionMs = 0; longDance.Tick(3600000);
    assert(longDance.Diagnostics().audioGeneration == generation + 1);
    assert(longDance.Diagnostics().beatIndex == 0);
    assert(longDance.Diagnostics().count <= 2);
    for (size_t index = 0; index < longDance.Diagnostics().count; ++index)
      assert(std::string(longDance.Diagnostics().events[index].clockDomain) == "audio-media");
    longDance.StopDance();
    assert(longPlayer.state.phase == "stopping" && longIo.LitSegments() == 0);
    assert(std::string(longDance.Diagnostics().state) == "running" && longDance.Reserved());
    longPlayer.state.phase = "idle"; longPlayer.state.positionMs = 0;
    longDance.Tick(3600010);
    assert(std::string(longDance.Diagnostics().state) == "cancelled" && !longDance.Reserved());
  }
  Player player; Io io; PhysicalEffects effects(io); DanceCoordinator dance(player, effects);
  assert(!dance.StartDance("first") && player.starts == 0);
  std::array<ServoCalibration, 6> calibration;
  calibration.fill({700, 1700, 20});
  assert(effects.Configure(calibration, 1)); effects.Tick(0); assert(effects.RecoverAfterInspection());
  assert(dance.StartDance("first") && dance.Reserved());
  player.state.phase = "playing";
  dance.Tick(0); assert(io.vibrations == 1 && io.LitSegments() == 1 && io.servos == 0);
  player.state.positionMs = 100; effects.Tick(100); dance.Tick(100);
  assert(io.vibrations == 1 && io.LitSegments() == 1);
  player.state.positionMs = 180; effects.Tick(180); dance.Tick(180);
  assert(!io.enabled && io.LitSegments() == 0);
  // A delayed observation lands on beat 6: one pulse, never beats 1..5.
  player.state.positionMs = 3000; dance.Tick(181);
  assert(io.vibrations == 2 && io.LitSegments() == 1 && io.servos == 0);
  player.state.positionMs = 4200; dance.Tick(182); // Late beat: lights and vibration are skipped.
  assert(io.vibrations == 2 && io.LitSegments() == 0);
  dance.StopDance(); assert(player.state.phase == "stopping" && !io.enabled && io.LitSegments() == 0 && dance.Reserved());
  player.state.phase = "idle"; assert(dance.Reserved() && dance.DanceState().phase == "stopping");
  dance.Tick(183); assert(!dance.Reserved());
  const unsigned idleLights = io.lights, idleOffs = io.fullOffs, idleStops = player.stops;
  dance.StopDance(); dance.Tick(184);
  assert(io.lights == idleLights && io.fullOffs == idleOffs && player.stops == idleStops);
  assert(dance.StartDance("second")); player.state.phase = "playing"; player.state.positionMs = 0;
  io.failLights = true; dance.Tick(185);
  assert(player.state.phase == "stopping" && dance.Reserved() && dance.DanceState().error == "dance_effects_failed");
  player.state.phase = "idle"; io.failLights = false; dance.Tick(186);
  assert(!dance.Reserved() && dance.DanceState().phase == "failed" && !dance.DanceState().available);
  assert(io.servos == 0);
  std::cout << "Dance beat/fade, no catchup, immediate Stop, fault, terminal cleanup and ownership tests passed.\n";
}
