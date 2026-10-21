#include <algorithm>
#include <array>
#include <atomic>
#include <cassert>
#include <chrono>
#include <condition_variable>
#include <cstdio>
#include <cstring>
#include <limits>
#include <mutex>
#include <new>
#include <type_traits>
#include <thread>
#include <vector>

#include "../src/features/voice_turn/BoundedPlaybackOwner.h"

namespace {
using Owner = BoundedPlaybackOwner;
using Progress = Owner::Progress;
using Error = Owner::Error;
using Scope = Owner::Scope;
using Handle = Owner::Handle;
const Scope First{1, 0};
class Gate {
 public:
  void Arm() { std::lock_guard<std::mutex> lock(mutex); armed = true; entered = released = false; }
  void Enter() {
    std::unique_lock<std::mutex> lock(mutex);
    if (!armed) return;
    entered = true; condition.notify_all();
    condition.wait(lock, [&]() { return released; });
    armed = false;
  }
  void Await() {
    std::unique_lock<std::mutex> lock(mutex);
    assert(condition.wait_for(lock, std::chrono::seconds(3), [&]() { return entered; }));
  }
  void Release() { std::lock_guard<std::mutex> lock(mutex); released = true; condition.notify_all(); }
 private:
  std::mutex mutex;
  std::condition_variable condition;
  bool armed = false, entered = false, released = false;
};
class Port final : public Owner::Port {
 public:
  bool Begin(const Owner::Fence& fence) override {
    ++begins; begin.Enter();
    std::lock_guard<std::mutex> lock(hardwareGate);
    if (!fence.Current() || failBegin) return false;
    active = true;
    return true;
  }
  bool Play(const Owner::Fence& fence, const uint8_t* pcm, size_t length) override {
    assert(length && length <= Owner::SlotBytes && length % 2 == 0);
    std::array<uint8_t, Owner::SlotBytes> original{};
    std::copy(pcm, pcm + length, original.begin());
    ++plays;
    play.Enter();
    // Stop/cancel must not scrub a span still borrowed by an in-flight raw IO.
    assert(std::equal(pcm, pcm + length, original.begin()));
    std::lock_guard<std::mutex> lock(hardwareGate);
    if (!fence.Current() || !active) return false;
    muted = false;
    const size_t accepted = failPlay ? length / 2 : length;
    output.insert(output.end(), pcm, pcm + accepted);
    return !failPlay;
  }
  bool Drain(const Owner::Fence& fence) override {
    ++drains; drain.Enter();
    std::lock_guard<std::mutex> lock(hardwareGate);
    if (!fence.Current() || !active || failDrain) return false;
    muted = true;
    return true;
  }
  bool CancelPlayback(const Owner::Fence& fence) override {
    ++cancels; cancel.Enter();
    std::lock_guard<std::mutex> lock(hardwareGate);
    if (!fence.Current() || !active || failCancel) return false;
    muted = true;
    return true;
  }
  void ImmediateStop() { std::lock_guard<std::mutex> lock(hardwareGate); active = false; muted = true; }
  void ImmediateMute() { std::lock_guard<std::mutex> lock(hardwareGate); muted = true; }
  bool Active() { std::lock_guard<std::mutex> lock(hardwareGate); return active; }
  bool Muted() { std::lock_guard<std::mutex> lock(hardwareGate); return muted; }
  Gate begin, play, drain, cancel;
  std::atomic<unsigned> begins{0}, plays{0}, drains{0}, cancels{0};
  std::atomic<bool> failBegin{false}, failPlay{false}, failDrain{false}, failCancel{false};
  std::vector<uint8_t> output;
 private:
  std::mutex hardwareGate;
  bool active = false, muted = true;
};
struct Fixture {
  Fixture() : owner(port, workspace.data(), Owner::WorkspaceBytes, Stop, Mute, this) {
    std::fill(workspace.begin() + Owner::WorkspaceBytes, workspace.end(), static_cast<uint8_t>(0xA5));
  }
  static void Stop(void* context) {
    auto& fixture = *static_cast<Fixture*>(context);
    // Reentrant capacity inspection proves callbacks do not hold the RAM lock.
    fixture.owner.Inspect(); fixture.port.ImmediateStop(); ++fixture.stops;
    fixture.stop.Enter();
  }
  static bool Mute(void* context) {
    auto& fixture = *static_cast<Fixture*>(context);
    fixture.owner.Inspect(); fixture.port.ImmediateMute(); ++fixture.mutes;
    fixture.mute.Enter();
    return !fixture.failMute;
  }
  Handle Start(Scope scope = First) {
    Handle handle;
    assert(owner.RequestStart(scope, handle).progress == Progress::Pending);
    assert(owner.Poll(handle).progress == Progress::Pending);
    assert(owner.RunOne());
    assert(owner.Poll(handle).progress == Progress::Succeeded);
    return handle;
  }
  void Drain(Scope scope, uint64_t sequence, uint64_t samples) {
    Handle handle;
    assert(owner.RequestDrain(scope, sequence, samples, handle).progress == Progress::Pending);
    unsigned work = 0;
    while (owner.RunOne()) assert(++work <= Owner::SlotCount + 1);
    assert(owner.Poll(handle).progress == Progress::Succeeded);
    assert(owner.Poll(handle).error == Error::InvalidHandle);
  }
  void Scrubbed() const {
    assert(std::all_of(workspace.begin(), workspace.begin() + Owner::WorkspaceBytes,
        [](uint8_t byte) { return byte == 0; }));
    assert(std::all_of(workspace.begin() + Owner::WorkspaceBytes, workspace.end(),
        [](uint8_t byte) { return byte == 0xA5; }));
    assert(owner.Inspect().freeBytes == Owner::WorkspaceBytes);
  }
  Port port;
  std::array<uint8_t, Owner::WorkspaceBytes + 32> workspace{};
  std::atomic<unsigned> stops{0}, mutes{0};
  std::atomic<bool> failMute{false};
  Gate stop, mute;
  Owner owner;
};
void AtomicEnvelopeFrontiersAndSingleUseDrain() {
  Fixture fixture;
  const auto start = fixture.Start();
  assert(fixture.owner.Poll(start).error == Error::InvalidHandle);
  Handle empty;
  assert(fixture.owner.RequestDrain(First, 0, 0, empty).error == Error::FrontierMismatch);
  std::array<uint8_t, Owner::MaximumPcmBytes> pcm{};
  pcm.fill(0x11);
  assert(fixture.owner.Enqueue(First, 0, pcm.data(), pcm.size()).progress == Progress::Succeeded);
  pcm.fill(0x22);
  assert(fixture.owner.Enqueue(First, 1, pcm.data(), pcm.size()).progress == Progress::Succeeded);
  const auto full = fixture.owner.Inspect();
  assert(full.freeBytes == 0 && full.queuedBytes == Owner::WorkspaceBytes);
  assert(full.acceptedSequence == 1 && full.acceptedSamples == Owner::WorkspaceBytes / 2);
  Handle drain;
  assert(fixture.owner.RequestDrain(First, 0, full.acceptedSamples, drain).error == Error::FrontierMismatch);
  assert(fixture.owner.RequestDrain(First, 1, full.acceptedSamples - 1, drain).error == Error::FrontierMismatch);
  assert(fixture.owner.RequestDrain({2, 0}, 1, full.acceptedSamples, drain).error == Error::Stale);
  assert(fixture.owner.RequestDrain(First, 1, full.acceptedSamples, drain).progress == Progress::Pending);
  const auto copy = drain;
  assert(fixture.owner.Enqueue({2, 0}, 2, pcm.data(), 2).error == Error::Stale);
  assert(fixture.owner.RequestDrain(First, 1, full.acceptedSamples, drain).error == Error::Busy);
  for (size_t slot = 0; slot != Owner::SlotCount; ++slot) {
    assert(fixture.owner.RunOne());
    assert(fixture.owner.Poll(drain).progress == Progress::Pending);
    assert(fixture.port.drains == 0);
  }
  assert(fixture.owner.Inspect().playedSamples == full.acceptedSamples);
  assert(fixture.owner.RunOne() && fixture.port.drains == 1);
  assert(!fixture.owner.RunOne());
  assert(fixture.owner.Poll(copy).progress == Progress::Succeeded);
  assert(fixture.owner.Poll(drain).error == Error::InvalidHandle);
  assert(fixture.owner.RequestDrain(First, 1, full.acceptedSamples, drain).error == Error::FrontierMismatch);
  assert(fixture.port.output.size() == Owner::WorkspaceBytes);
  assert(std::all_of(fixture.port.output.begin(), fixture.port.output.begin() + pcm.size(), [](uint8_t b) { return b == 0x11; }));
  assert(std::all_of(fixture.port.output.begin() + pcm.size(), fixture.port.output.end(), [](uint8_t b) { return b == 0x22; }));
  fixture.Scrubbed();
  assert(fixture.owner.Enqueue(First, 2, pcm.data(), 2).progress == Progress::Succeeded);
  fixture.Drain(First, 2, full.acceptedSamples + 1);
  assert(fixture.port.begins == 1 && fixture.port.cancels == 0 && fixture.port.drains == 2);
}
void ForeignAndReincarnatedHandlesCannotConsumeAuthority() {
  Fixture a, b;
  Handle startA, startB;
  assert(a.owner.RequestStart(First, startA).progress == Progress::Pending);
  assert(b.owner.RequestStart(First, startB).progress == Progress::Pending);
  assert(b.owner.RunOne());
  assert(b.owner.Poll(startA).error == Error::InvalidHandle);
  assert(b.owner.Poll(startB).progress == Progress::Succeeded);
  assert(a.owner.RunOne() && a.owner.Poll(startA).progress == Progress::Succeeded);
  const uint8_t pcm[] = {0x21, 0x43};
  assert(a.owner.Enqueue(First, 0, pcm, 2).progress == Progress::Succeeded);
  assert(b.owner.Enqueue(First, 0, pcm, 2).progress == Progress::Succeeded);
  Handle drainA, drainB;
  assert(a.owner.RequestDrain(First, 0, 1, drainA).progress == Progress::Pending);
  assert(b.owner.RequestDrain(First, 0, 1, drainB).progress == Progress::Pending);
  assert(b.owner.RunOne() && b.owner.RunOne());
  assert(b.owner.Poll(drainA).error == Error::InvalidHandle);
  assert(b.owner.Inspect().state == Owner::State::Draining);
  assert(b.owner.Poll(drainB).progress == Progress::Succeeded);
  a.owner.Stop(); b.owner.Stop();

  Port port;
  std::array<uint8_t, Owner::WorkspaceBytes> workspace{};
  std::aligned_storage<sizeof(Owner), alignof(Owner)>::type storage;
  auto* first = new (&storage) Owner(port, workspace.data(), workspace.size(),
      [](void*) {}, [](void*) { return true; }, nullptr);
  Handle previous;
  assert(first->RequestStart(First, previous).progress == Progress::Pending);
  assert(first->RunOne());
  first->Stop(); first->~Owner();
  auto* next = new (&storage) Owner(port, workspace.data(), workspace.size(),
      [](void*) {}, [](void*) { return true; }, nullptr);
  Handle current;
  assert(next->RequestStart(First, current).progress == Progress::Pending && next->RunOne());
  assert(next->Poll(previous).error == Error::InvalidHandle);
  assert(next->Poll(current).progress == Progress::Succeeded);
  next->Stop(); next->~Owner();
}

void DrainBarrierRetainsOriginalProofWhileLaterPcmQueues() {
  Fixture fixture; fixture.Start();
  std::array<uint8_t, Owner::SlotBytes> pcm{}; pcm.fill(0x11);
  assert(fixture.owner.Enqueue(First, 0, pcm.data(), pcm.size()).progress == Progress::Succeeded);
  Handle barrier;
  assert(fixture.owner.RequestDrain(First, 0, pcm.size() / 2, barrier).progress == Progress::Pending);
  pcm.fill(0x22);
  assert(fixture.owner.Enqueue(First, 1, pcm.data(), pcm.size()).progress == Progress::Succeeded);
  assert(fixture.owner.RunOne() && fixture.port.plays == 1);
  assert(fixture.owner.Inspect().playedSamples == Owner::SlotBytes / 2);
  assert(fixture.owner.RunOne() && fixture.port.drains == 1 && fixture.port.plays == 1);
  assert(!fixture.owner.RunOne()); // Later PCM is held even after raw Drain success.
  pcm.fill(0x33);
  assert(fixture.owner.Enqueue(First, 2, pcm.data(), pcm.size()).progress == Progress::Succeeded);
  assert(!fixture.owner.RunOne() && fixture.owner.Inspect().queuedBytes == 2 * Owner::SlotBytes);
  assert(barrier.Sequence() == 0 && barrier.Samples() == Owner::SlotBytes / 2);
  assert(fixture.owner.Poll(barrier).progress == Progress::Succeeded);
  assert(fixture.owner.Poll(barrier).error == Error::InvalidHandle);
  assert(fixture.owner.RunOne() && fixture.port.plays == 2);
  assert(fixture.owner.RunOne() && fixture.port.plays == 3);
  fixture.Drain(First, 2, 3 * Owner::SlotBytes / 2);
  assert(fixture.port.output.size() == 3 * Owner::SlotBytes && fixture.port.drains == 2);
  for (size_t packet = 0; packet != 3; ++packet)
    assert(std::all_of(fixture.port.output.begin() + packet * Owner::SlotBytes,
        fixture.port.output.begin() + (packet + 1) * Owner::SlotBytes,
        [packet](uint8_t value) { return value == (packet + 1) * 0x11; }));
  fixture.Scrubbed();

  for (bool acknowledged : {false, true}) {
    Fixture cancelled; cancelled.Start();
    assert(cancelled.owner.Enqueue(First, 0, pcm.data(), pcm.size()).progress == Progress::Succeeded);
    Handle target;
    assert(cancelled.owner.RequestDrain(First, 0, pcm.size() / 2, target).progress == Progress::Pending);
    assert(cancelled.owner.Enqueue(First, 1, pcm.data(), pcm.size()).progress == Progress::Succeeded);
    assert(cancelled.owner.RunOne() && cancelled.owner.RunOne());
    if (acknowledged) assert(cancelled.owner.Poll(target).progress == Progress::Succeeded);
    Handle cancel;
    assert(cancelled.owner.RequestCancel(First, 4, cancel).progress == Progress::Pending);
    assert(cancelled.owner.Poll(target).error == Error::InvalidHandle);
    assert(cancelled.owner.RunOne() && cancelled.port.plays == 1);
    assert(cancelled.owner.Poll(cancel).progress == Progress::Succeeded);
    cancelled.Scrubbed();
  }
  Fixture overloaded; overloaded.Start();
  std::array<uint8_t, Owner::MaximumPcmBytes> whole{};
  assert(overloaded.owner.Enqueue(First, 0, whole.data(), whole.size()).progress == Progress::Succeeded);
  Handle target;
  assert(overloaded.owner.RequestDrain(First, 0, whole.size() / 2, target).progress == Progress::Pending);
  assert(overloaded.owner.Enqueue(First, 1, whole.data(), whole.size()).progress == Progress::Succeeded);
  assert(overloaded.owner.Enqueue(First, 2, whole.data(), 2).error == Error::Overload);
  assert(overloaded.owner.Poll(target).error == Error::Overload && !overloaded.owner.RunOne());
  overloaded.Scrubbed();
}

void BorrowedWorkspaceStartsScrubbedAndDuplicateSequenceFails() {
  Port port;
  std::array<uint8_t, Owner::WorkspaceBytes + 16> workspace;
  workspace.fill(0xBA);
  Owner owner(port, workspace.data(), Owner::WorkspaceBytes,
      [](void*) {}, [](void*) { return true; }, nullptr);
  assert(std::all_of(workspace.begin(), workspace.begin() + Owner::WorkspaceBytes, [](uint8_t byte) { return byte == 0; }));
  assert(std::all_of(workspace.begin() + Owner::WorkspaceBytes, workspace.end(), [](uint8_t byte) { return byte == 0xBA; }));
  Handle handle;
  assert(owner.RequestStart(First, handle).progress == Progress::Pending);
  assert(owner.RunOne() && owner.Poll(handle).progress == Progress::Succeeded);
  const uint8_t pcm[] = {0x12, 0x34};
  assert(owner.Enqueue(First, 0, pcm, sizeof(pcm)).progress == Progress::Succeeded);
  assert(owner.Enqueue(First, 0, pcm, sizeof(pcm)).error == Error::SequenceMismatch);
  assert(owner.Inspect().state == Owner::State::Failed && port.plays == 0);
  assert(std::all_of(workspace.begin(), workspace.begin() + Owner::WorkspaceBytes, [](uint8_t byte) { return byte == 0; }));
}

void QueueOverloadIsAtomicAndStops() {
  Fixture fixture; fixture.Start();
  std::array<uint8_t, Owner::MaximumPcmBytes> pcm{}; pcm.fill(0x33);
  assert(fixture.owner.Enqueue(First, 0, pcm.data(), pcm.size()).progress == Progress::Succeeded);
  assert(fixture.owner.Enqueue(First, 1, pcm.data(), Owner::SlotBytes * 4).progress == Progress::Succeeded);
  const auto before = fixture.owner.Inspect();
  assert(before.freeBytes == Owner::SlotBytes * 4);
  const auto overloaded = fixture.owner.Enqueue(First, 2, pcm.data(), pcm.size());
  assert(overloaded.progress == Progress::Failed && overloaded.error == Error::Overload);
  assert(fixture.owner.Inspect().acceptedSequence == before.acceptedSequence);
  assert(fixture.owner.Inspect().acceptedSamples == before.acceptedSamples);
  assert(fixture.owner.Inspect().state == Owner::State::Failed && fixture.stops == 2);
  assert(!fixture.owner.RunOne() && fixture.port.plays == 0);
  fixture.Scrubbed();
}
void PartialIoCannotYieldDrainAcknowledgement() {
  for (unsigned failure = 0; failure != 4; ++failure) {
    Fixture fixture;
    if (failure == 0) {
      Handle start; fixture.port.failBegin = true;
      assert(fixture.owner.RequestStart(First, start).progress == Progress::Pending);
      assert(fixture.owner.RunOne());
      assert(fixture.owner.Poll(start).error == Error::IoFailure);
    } else {
      fixture.Start();
      std::array<uint8_t, 100> pcm{}; pcm.fill(0x44);
      assert(fixture.owner.Enqueue(First, 0, pcm.data(), pcm.size()).progress == Progress::Succeeded);
      Handle operation;
      if (failure == 3) {
        fixture.port.failCancel = true;
        assert(fixture.owner.RequestCancel(First, 2, operation).progress == Progress::Pending);
      } else {
        fixture.port.failPlay = failure == 1; fixture.port.failDrain = failure == 2;
        assert(fixture.owner.RequestDrain(First, 0, pcm.size() / 2, operation).progress == Progress::Pending);
        assert(fixture.owner.RunOne());
        if (failure == 2) assert(fixture.owner.RunOne());
      }
      if (failure == 3) assert(fixture.owner.RunOne());
      assert(fixture.owner.Poll(operation).error == Error::IoFailure);
      assert(fixture.owner.Poll(operation).error == Error::InvalidHandle);
      if (failure == 1) assert(fixture.port.output.size() == pcm.size() / 2 && fixture.port.drains == 0);
    }
    assert(fixture.owner.Inspect().state == Owner::State::Failed);
    fixture.Scrubbed();
  }
}
void StalledPlayCannotBlockCancelOrReopenItsSuccessor() {
  for (bool stopped : {false, true}) {
    Fixture fixture; fixture.Start();
    std::array<uint8_t, Owner::MaximumPcmBytes> pcm{}; pcm.fill(0x55);
    assert(fixture.owner.Enqueue(First, 0, pcm.data(), pcm.size()).progress == Progress::Succeeded);
    assert(fixture.owner.Enqueue(First, 1, pcm.data(), pcm.size()).progress == Progress::Succeeded);
    Handle oldDrain;
    assert(fixture.owner.RequestDrain(First, 1, Owner::WorkspaceBytes / 2, oldDrain).progress == Progress::Pending);
    fixture.port.play.Arm(); fixture.port.failPlay = stopped;
    std::thread worker([&]() { assert(fixture.owner.RunOne()); }); fixture.port.play.Await();
    assert(fixture.owner.Inspect().workerActive);
    Handle cancelled;
    if (stopped) {
      fixture.owner.Stop();
      assert(fixture.owner.Poll(oldDrain).error == Error::Stopped);
    } else {
      assert(fixture.owner.RequestCancel(First, 2, cancelled).progress == Progress::Pending);
      assert(fixture.mutes == 1 && fixture.port.cancels == 0);
      assert(fixture.owner.Poll(oldDrain).error == Error::InvalidHandle);
      assert(fixture.owner.Enqueue({1, 2}, 0, pcm.data(), 2).progress == Progress::Succeeded);
    }
    assert(fixture.owner.Inspect().freeBytes == Owner::WorkspaceBytes - Owner::SlotBytes * (stopped ? 1 : 2));
    Handle successor;
    assert(fixture.owner.RequestStart({2, 0}, successor).progress == Progress::Pending);
    assert(fixture.owner.Poll(cancelled).error == Error::InvalidHandle);
    assert(!fixture.owner.RunOne() && fixture.port.begins == 1);
    fixture.port.play.Release(); worker.join();
    fixture.port.failPlay = false;
    assert(fixture.owner.Inspect().playedSamples == 0);
    assert(fixture.owner.Poll(successor).progress == Progress::Pending);
    assert(fixture.owner.RunOne());
    assert(fixture.owner.Poll(successor).progress == Progress::Succeeded && fixture.port.begins == 2);
    assert(fixture.port.cancels == 0 && fixture.port.drains == 0);
    fixture.Scrubbed();
    assert(fixture.owner.Enqueue({2, 0}, 0, pcm.data(), 2).progress == Progress::Succeeded);
    fixture.Drain({2, 0}, 0, 1);
  }
}
void StalledDrainCannotAcknowledgeNewGeneration() {
  Fixture fixture; fixture.Start();
  const uint8_t pcm[] = {0x66, 0x66};
  assert(fixture.owner.Enqueue(First, 0, pcm, sizeof(pcm)).progress == Progress::Succeeded);
  assert(fixture.owner.RunOne());
  Handle drain;
  assert(fixture.owner.RequestDrain(First, 0, 1, drain).progress == Progress::Pending);
  fixture.port.drain.Arm();
  std::thread worker([&]() { assert(fixture.owner.RunOne()); }); fixture.port.drain.Await();
  Handle cancelled;
  assert(fixture.owner.RequestCancel(First, 2, cancelled).progress == Progress::Pending);
  assert(fixture.owner.Poll(drain).error == Error::InvalidHandle);
  assert(!fixture.owner.RunOne() && fixture.port.cancels == 0);
  fixture.port.drain.Release(); worker.join();
  assert(fixture.owner.Poll(cancelled).progress == Progress::Pending);
  assert(fixture.owner.RunOne() && fixture.port.cancels == 1);
  assert(fixture.owner.Poll(cancelled).progress == Progress::Succeeded);
  assert(fixture.port.begins == 1 && fixture.mutes == 1);
  assert(fixture.owner.Enqueue(First, 1, pcm, sizeof(pcm)).error == Error::Stale);
  assert(fixture.owner.Enqueue({1, 2}, 0, pcm, sizeof(pcm)).progress == Progress::Succeeded);
  fixture.Drain({1, 2}, 0, 1);
  Handle skipped;
  assert(fixture.owner.RequestCancel({1, 2}, 2, skipped).error == Error::Stale);
  assert(fixture.owner.RequestCancel({1, 2}, 1, skipped).error == Error::Stale);
  assert(fixture.owner.RequestCancel({1, 2}, 7, skipped).progress == Progress::Pending);
  assert(fixture.owner.RunOne());
  assert(fixture.owner.Poll(skipped).progress == Progress::Succeeded);
  assert(fixture.owner.Inspect().scope.generation == 7 && fixture.port.begins == 1);
}
void SessionScopedCleanupCannotStopSuccessorIo() {
  Fixture fixture; fixture.Start(); fixture.Start({2, 0});
  const unsigned stops = fixture.stops;
  const auto before = fixture.owner.Inspect();
  assert(!fixture.owner.StopSession(1) && !fixture.owner.StopSession(0));
  assert(fixture.stops == stops && fixture.owner.Inspect().state == before.state);
  const uint8_t pcm[] = {0x12, 0x34};
  assert(fixture.owner.Enqueue({2, 0}, 0, pcm, sizeof(pcm)).progress == Progress::Succeeded);
  assert(fixture.owner.RunOne() && !fixture.port.Muted());
  assert(fixture.owner.Enqueue({2, 0}, 1, pcm, sizeof(pcm)).progress == Progress::Succeeded);
  fixture.port.play.Arm();
  std::thread worker([&]() { assert(fixture.owner.RunOne()); }); fixture.port.play.Await();
  assert(!fixture.owner.StopSession(1));
  assert(fixture.stops == stops && fixture.port.Active() && !fixture.port.Muted());
  fixture.port.play.Release(); worker.join();
  fixture.Drain({2, 0}, 1, 2);

  assert(fixture.owner.Enqueue({2, 0}, 2, pcm, sizeof(pcm)).progress == Progress::Succeeded);
  fixture.port.play.Arm();
  std::thread stopped([&]() { assert(fixture.owner.RunOne()); }); fixture.port.play.Await();
  assert(fixture.owner.StopSession(2));
  assert(fixture.stops == stops + 1 && !fixture.port.Active() && fixture.port.Muted());
  fixture.port.play.Release(); stopped.join();
  assert(fixture.owner.Inspect().state == Owner::State::Stopped);
  assert(fixture.owner.Inspect().playedSamples == 2); // Late third sample never regains authority.
  fixture.Scrubbed();
  fixture.Start({3, 0});
  Handle cancelled;
  assert(fixture.owner.RequestCancel({3, 0}, 9, cancelled).progress == Progress::Pending);
  assert(fixture.owner.StopSession(3));
  assert(fixture.owner.Poll(cancelled).error == Error::Stopped && !fixture.owner.RunOne());
}

void NewerCancelSupersedesStalledFlushAndQueuesItsOwnPcm() {
  Fixture fixture; fixture.Start();
  Handle old;
  assert(fixture.owner.RequestCancel(First, 2, old).progress == Progress::Pending);
  fixture.port.cancel.Arm();
  std::thread worker([&]() { assert(fixture.owner.RunOne()); }); fixture.port.cancel.Await();
  std::array<uint8_t, Owner::MaximumPcmBytes> pcm{}; pcm.fill(0x51);
  assert(fixture.owner.Enqueue({1, 2}, 0, pcm.data(), pcm.size()).progress == Progress::Succeeded);
  Handle drain;
  assert(fixture.owner.RequestDrain({1, 2}, 0, pcm.size() / 2, drain).error == Error::Busy);
  Handle current;
  assert(fixture.owner.RequestCancel({1, 2}, 7, current).progress == Progress::Pending);
  assert(fixture.owner.Poll(old).error == Error::InvalidHandle);
  pcm.fill(0x73);
  assert(fixture.owner.Enqueue({1, 7}, 0, pcm.data(), pcm.size()).progress == Progress::Succeeded);
  assert(fixture.owner.Enqueue({1, 7}, 1, pcm.data(), pcm.size()).progress == Progress::Succeeded);
  assert(!fixture.owner.RunOne() && fixture.port.plays == 0);
  fixture.port.cancel.Release(); worker.join();
  assert(fixture.owner.Poll(current).progress == Progress::Pending);
  assert(fixture.owner.RunOne() && fixture.port.cancels == 2 && fixture.port.plays == 0);
  assert(fixture.owner.RequestDrain({1, 7}, 1, Owner::WorkspaceBytes / 2, drain).error == Error::Busy);
  assert(fixture.owner.Poll(current).progress == Progress::Succeeded);
  fixture.Drain({1, 7}, 1, Owner::WorkspaceBytes / 2);
  assert(fixture.port.output.size() == Owner::WorkspaceBytes);
  assert(std::all_of(fixture.port.output.begin(), fixture.port.output.end(), [](uint8_t b) { return b == 0x73; }));
  fixture.Scrubbed();

  Fixture overloaded; overloaded.Start();
  Handle cancellation;
  assert(overloaded.owner.RequestCancel(First, 3, cancellation).progress == Progress::Pending);
  assert(overloaded.owner.Enqueue({1, 3}, 0, pcm.data(), pcm.size()).progress == Progress::Succeeded);
  assert(overloaded.owner.Enqueue({1, 3}, 1, pcm.data(), pcm.size()).progress == Progress::Succeeded);
  assert(overloaded.owner.Enqueue({1, 3}, 2, pcm.data(), 2).error == Error::Overload);
  assert(overloaded.owner.Poll(cancellation).error == Error::Overload);
  assert(!overloaded.owner.RunOne() && overloaded.port.plays == 0 && overloaded.port.cancels == 0);
  overloaded.Scrubbed();
}

void StalledOldIoCannotResetOrFailNewSession() {
  for (bool cancel : {false, true}) {
    Fixture fixture;
    Handle old;
    if (cancel) {
      fixture.Start(); fixture.port.cancel.Arm(); fixture.port.failCancel = true;
      assert(fixture.owner.RequestCancel(First, 2, old).progress == Progress::Pending);
    } else {
      fixture.port.begin.Arm();
      assert(fixture.owner.RequestStart(First, old).progress == Progress::Pending);
    }
    auto& gate = cancel ? fixture.port.cancel : fixture.port.begin;
    std::thread worker([&]() { assert(fixture.owner.RunOne()); }); gate.Await();
    fixture.owner.Stop();
    assert(!fixture.port.Active() && fixture.port.Muted());
    assert(fixture.owner.Poll(old).error == Error::Stopped);
    Handle next;
    assert(fixture.owner.RequestStart({2, 0}, next).progress == Progress::Pending);
    for (unsigned query = 0; query != 1000; ++query) assert(fixture.owner.Inspect().workerActive);
    assert(!fixture.owner.RunOne());
    gate.Release(); worker.join();
    assert(!fixture.port.Active() && fixture.port.Muted()); // Stale Begin cannot reopen.
    fixture.port.failBegin = false;
    assert(fixture.owner.Poll(next).progress == Progress::Pending);
    assert(fixture.owner.RunOne());
    assert(fixture.owner.Poll(next).progress == Progress::Succeeded);
    assert(fixture.owner.Inspect().state == Owner::State::Ready && fixture.port.Active());
  }
}
void LateMuteFailureCannotStopNewSession() {
  Fixture fixture; fixture.Start(); fixture.mute.Arm(); fixture.failMute = true;
  Handle old;
  Owner::Result cancelled{Progress::Pending};
  std::thread reader([&]() { cancelled = fixture.owner.RequestCancel(First, 2, old); }); fixture.mute.Await();
  Handle next;
  assert(fixture.owner.RequestStart({2, 0}, next).progress == Progress::Pending);
  assert(!fixture.owner.RunOne() && fixture.port.begins == 1);
  fixture.mute.Release(); reader.join();
  assert(cancelled.progress == Progress::Rejected && cancelled.error == Error::Stale);
  assert(fixture.owner.RunOne());
  assert(fixture.owner.Poll(next).progress == Progress::Succeeded);
  assert(fixture.owner.Inspect().state == Owner::State::Ready && fixture.stops == 2);
}
void MuteFailureAndCounterExhaustionRemainClosed() {
  const auto maximum = std::numeric_limits<uint64_t>::max();
  for (unsigned counter = 0; counter != 4; ++counter) {
    Fixture fixture;
    Handle operation;
    if (counter == 0) {
      assert(fixture.owner.RequestStart({maximum, 1}, operation).error == Error::CounterExhausted);
      assert(fixture.owner.RequestStart(First, operation).error == Error::Stale);
    } else if (counter == 1) {
      assert(fixture.owner.RequestStart({1, maximum}, operation).error == Error::CounterExhausted);
      assert(fixture.owner.RequestStart(First, operation).error == Error::Stale);
    } else if (counter == 2) {
      fixture.Start({1, maximum - 1});
      assert(fixture.owner.RequestCancel({1, maximum - 1}, maximum, operation).error == Error::CounterExhausted);
    } else {
      fixture.Start();
      const uint8_t pcm[] = {0x77, 0x77};
      assert(fixture.owner.Enqueue(First, maximum, pcm, sizeof(pcm)).error == Error::CounterExhausted);
    }
    assert(fixture.owner.Inspect().state == Owner::State::Failed && fixture.port.plays == 0);
    assert(!fixture.owner.RunOne()); fixture.Scrubbed();
  }
  Fixture fixture; fixture.Start(); fixture.failMute = true;
  Handle operation;
  assert(fixture.owner.RequestCancel(First, 2, operation).error == Error::FenceFailure);
  assert(fixture.owner.Poll(operation).error == Error::FenceFailure);
  assert(!fixture.owner.RunOne() && fixture.port.cancels == 0);
  fixture.Scrubbed();
  Owner invalid(fixture.port, nullptr, 0, Fixture::Stop, Fixture::Mute, &fixture);
  assert(invalid.RequestStart(First, operation).error == Error::InvalidConfiguration);
}
void FullQueueCancellationHasFixedMemoryAndFiniteHostCost() {
  Fixture fixture; fixture.Start();
  std::array<uint8_t, Owner::MaximumPcmBytes> pcm{}; pcm.fill(0xA3);
  long long maximumMicros = 0;
  for (uint64_t generation = 0; generation != 2000; ++generation) {
    const Scope scope{1, generation};
    assert(fixture.owner.Enqueue(scope, 0, pcm.data(), pcm.size()).progress == Progress::Succeeded);
    assert(fixture.owner.Enqueue(scope, 1, pcm.data(), pcm.size()).progress == Progress::Succeeded);
    assert(fixture.owner.Inspect().freeBytes == 0);
    Handle cancelled;
    const auto before = std::chrono::steady_clock::now();
    assert(fixture.owner.RequestCancel(scope, generation + 1, cancelled).progress == Progress::Pending);
    const auto micros = std::chrono::duration_cast<std::chrono::microseconds>(std::chrono::steady_clock::now() - before).count();
    maximumMicros = std::max(maximumMicros, static_cast<long long>(micros));
    assert(fixture.owner.RunOne());
    assert(fixture.owner.Poll(cancelled).progress == Progress::Succeeded);
    fixture.Scrubbed();
  }
  assert(fixture.port.begins == 1 && fixture.port.plays == 0 && fixture.port.cancels == 2000);
  assert(fixture.stops == 1 && fixture.mutes == 2000);
  std::printf("bounded playback cancellation: 2000 full queues, maximum %lld host microseconds including 65536-byte scrub; host timing only\n", maximumMicros);
}

void InvalidOrderingAndBoundedSyntheticWorkload() {
  Fixture fixture; fixture.Start();
  const uint8_t pcm[] = {0x12, 0x34};
  assert(fixture.owner.Enqueue(First, 0, pcm, std::numeric_limits<size_t>::max()).error == Error::InvalidPcm);
  assert(fixture.owner.Enqueue(First, 0, pcm, 1).error == Error::InvalidPcm);
  assert(fixture.owner.Enqueue(First, 0, nullptr, 2).error == Error::InvalidPcm);
  assert(fixture.owner.Enqueue(First, 1, pcm, 2).error == Error::SequenceMismatch);
  assert(!fixture.owner.RunOne());
  fixture.Start({2, 0});
  const auto started = std::chrono::steady_clock::now();
  for (uint64_t sequence = 0; sequence < 10000; ++sequence) {
    assert(fixture.owner.Enqueue({2, 0}, sequence, pcm, sizeof(pcm)).progress == Progress::Succeeded);
    fixture.Drain({2, 0}, sequence, sequence + 1);
  }
  const auto elapsed = std::chrono::duration_cast<std::chrono::microseconds>(std::chrono::steady_clock::now() - started).count();
  assert(fixture.owner.Inspect().acceptedSamples == 10000 && fixture.port.drains == 10000);
  fixture.Scrubbed();
  fixture.owner.Stop();
  Handle repeated;
  assert(fixture.owner.RequestStart({2, 2}, repeated).error == Error::Stale);
  std::printf("bounded playback: 10000 enqueue/play/drain cycles, %lld host microseconds, workspace=%zu metadata=%zu bytes; host timing only\n",
      static_cast<long long>(elapsed), Owner::WorkspaceBytes, sizeof(Owner));
}
} // namespace
int main() {
  AtomicEnvelopeFrontiersAndSingleUseDrain();
  ForeignAndReincarnatedHandlesCannotConsumeAuthority();
  DrainBarrierRetainsOriginalProofWhileLaterPcmQueues();
  BorrowedWorkspaceStartsScrubbedAndDuplicateSequenceFails();
  QueueOverloadIsAtomicAndStops();
  PartialIoCannotYieldDrainAcknowledgement();
  StalledPlayCannotBlockCancelOrReopenItsSuccessor();
  StalledDrainCannotAcknowledgeNewGeneration();
  SessionScopedCleanupCannotStopSuccessorIo();
  NewerCancelSupersedesStalledFlushAndQueuesItsOwnPcm();
  StalledOldIoCannotResetOrFailNewSession();
  LateMuteFailureCannotStopNewSession();
  MuteFailureAndCounterExhaustionRemainClosed();
  FullQueueCancellationHasFixedMemoryAndFiniteHostCost();
  InvalidOrderingAndBoundedSyntheticWorkload();
  std::puts("PASS bounded playback owner checks; no hardware/acoustic claim");
}
