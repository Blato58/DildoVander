#include <algorithm>
#include <atomic>
#include <cassert>
#include <chrono>
#include <condition_variable>
#include <cstdio>
#include <functional>
#include <future>
#include <memory>
#include <mutex>
#include <string>
#include <thread>
#include <vector>

#include "../src/features/device_control/EspIdfDeviceStorageWorker.h"

struct FakeStorageSemaphore {
  std::mutex mutex;
  std::condition_variable changed;
  bool signaled = false, deleted = false;
};
struct FakeStorageTask { std::thread thread; };

namespace kernel {
// Host scheduling and metrics below are synthetic test controls, not ESP32
// latency, scheduler timing, stack consumption, or task-stack verification.
struct StopWorker {};
thread_local bool inWorker = false;
std::mutex mutex;
std::condition_variable changed;
std::vector<std::unique_ptr<FakeStorageTask>> tasks;
std::vector<std::unique_ptr<FakeStorageSemaphore>> semaphores;
bool failSemaphore = false, failTask = false, stop = false, unexpectedException = false;
unsigned semaphoreCreates = 0, semaphoreDeletes = 0, taskCreates = 0, taskDeletes = 0;
unsigned delayCalls = 0, releaseTicks = 0, exited = 0;
uint32_t requestedStack = 0;
UBaseType_t requestedPriority = 0;
TickType_t requestedDelay = 0;
std::string taskName;
std::atomic<int64_t> clockMicros{1000};
constexpr UBaseType_t SyntheticStackFree = 12345;
constexpr int64_t SyntheticOperationMicros = 11;

void StopAndJoin() {
  {
    std::lock_guard<std::mutex> lock(mutex);
    stop = true;
  }
  changed.notify_all();
  for (auto& task : tasks) if (task->thread.joinable()) task->thread.join();
  assert(!unexpectedException);
}
struct JoinBeforeStartupDestruction {
  ~JoinBeforeStartupDestruction() { StopAndJoin(); }
};
void Reset() {
  for (const auto& task : tasks) assert(!task->thread.joinable());
  tasks.clear(); semaphores.clear();
  failSemaphore = failTask = stop = unexpectedException = false;
  semaphoreCreates = semaphoreDeletes = taskCreates = taskDeletes = 0;
  delayCalls = releaseTicks = exited = 0;
  requestedStack = requestedPriority = requestedDelay = 0;
  taskName.clear(); clockMicros.store(1000);
}
void WaitForPark(unsigned minimumCalls = 1) {
  std::unique_lock<std::mutex> lock(mutex);
  assert(changed.wait_for(lock, std::chrono::seconds(3), [&]() { return delayCalls >= minimumCalls; }));
}
void ReleaseOneTick() {
  std::unique_lock<std::mutex> lock(mutex);
  const unsigned target = delayCalls + 1;
  ++releaseTicks;
  changed.notify_all();
  assert(changed.wait_for(lock, std::chrono::seconds(3), [&]() { return delayCalls >= target; }));
}
void WaitForExit() {
  std::unique_lock<std::mutex> lock(mutex);
  assert(changed.wait_for(lock, std::chrono::seconds(3), [&]() { return exited == tasks.size(); }));
}
} // namespace kernel

SemaphoreHandle_t xSemaphoreCreateBinary() {
  std::lock_guard<std::mutex> lock(kernel::mutex);
  ++kernel::semaphoreCreates;
  if (kernel::failSemaphore) return nullptr;
  kernel::semaphores.emplace_back(new FakeStorageSemaphore());
  return kernel::semaphores.back().get();
}
BaseType_t xSemaphoreTake(SemaphoreHandle_t handle, TickType_t timeout) {
  assert(handle && timeout == portMAX_DELAY);
  std::unique_lock<std::mutex> lock(handle->mutex);
  assert(handle->changed.wait_for(lock, std::chrono::seconds(3), [&]() { return handle->signaled; }));
  assert(!handle->deleted);
  handle->signaled = false;
  return pdTRUE;
}
BaseType_t xSemaphoreGive(SemaphoreHandle_t handle) {
  assert(kernel::inWorker && handle);
  {
    std::lock_guard<std::mutex> lock(handle->mutex);
    assert(!handle->deleted);
    handle->signaled = true;
  }
  handle->changed.notify_one();
  return pdTRUE;
}
void vSemaphoreDelete(SemaphoreHandle_t handle) {
  assert(handle);
  {
    std::lock_guard<std::mutex> lock(handle->mutex);
    assert(!handle->deleted);
    handle->deleted = true;
  }
  std::lock_guard<std::mutex> lock(kernel::mutex);
  ++kernel::semaphoreDeletes;
  // Keep fake kernel storage alive until all notifying task threads are joined.
}
BaseType_t xTaskCreate(TaskFunction_t entry, const char* name, uint32_t stackBytes,
    void* context, UBaseType_t priority, TaskHandle_t* handle) {
  std::lock_guard<std::mutex> lock(kernel::mutex);
  ++kernel::taskCreates;
  kernel::requestedStack = stackBytes; kernel::requestedPriority = priority; kernel::taskName = name;
  if (kernel::failTask) return pdFAIL;
  kernel::tasks.emplace_back(new FakeStorageTask());
  auto task = kernel::tasks.back().get();
  *handle = task;
  task->thread = std::thread([entry, context]() {
    kernel::inWorker = true;
    try { entry(context); }
    catch (const kernel::StopWorker&) {}
    catch (...) { std::lock_guard<std::mutex> errorLock(kernel::mutex); kernel::unexpectedException = true; }
    {
      std::lock_guard<std::mutex> exitLock(kernel::mutex);
      ++kernel::exited;
    }
    kernel::changed.notify_all();
    kernel::inWorker = false;
  });
  return pdPASS;
}
void vTaskDelete(TaskHandle_t handle) {
  assert(kernel::inWorker && handle == nullptr);
  {
    std::lock_guard<std::mutex> lock(kernel::mutex);
    ++kernel::taskDeletes;
  }
  throw kernel::StopWorker();
}
void vTaskDelay(TickType_t ticks) {
  assert(kernel::inWorker);
  std::unique_lock<std::mutex> lock(kernel::mutex);
  assert(ticks >= 1);
  kernel::requestedDelay = ticks;
  ++kernel::delayCalls;
  kernel::changed.notify_all();
  kernel::changed.wait(lock, [&]() { return kernel::stop || kernel::releaseTicks != 0; });
  if (kernel::stop) throw kernel::StopWorker();
  --kernel::releaseTicks;
}
UBaseType_t uxTaskGetStackHighWaterMark(TaskHandle_t handle) {
  assert(kernel::inWorker && handle == nullptr);
  return kernel::SyntheticStackFree;
}
int64_t esp_timer_get_time() {
  assert(kernel::inWorker);
  return kernel::clockMicros.fetch_add(kernel::SyntheticOperationMicros);
}

namespace {
class MemoryStorage final : public JournalStorage {
 public:
  JournalReadResult Read(uint8_t* output, size_t capacity, size_t& length) override {
    assert(explicitServiceSetup || kernel::inWorker);
    if (beforeRead) beforeRead();
    ++reads;
    length = bytes.size();
    if (length > capacity) return JournalReadResult::Failed;
    if (bytes.empty()) return JournalReadResult::Missing;
    std::copy(bytes.begin(), bytes.end(), output);
    return JournalReadResult::Found;
  }
  bool Write(const uint8_t* input, size_t length) override {
    assert(explicitServiceSetup || kernel::inWorker);
    ++writes;
    bytes.assign(input, input + length);
    return true;
  }
  std::vector<uint8_t> bytes;
  std::atomic<unsigned> reads{0}, writes{0};
  bool explicitServiceSetup = true;
  std::function<void()> beforeRead;
};
JournalIdentityView Identity(bool provisioned = true) {
  JournalIdentityView identity;
  identity.healthy = true; identity.provisioned = provisioned;
  if (provisioned) identity.deviceId = "worker-device";
  return identity;
}
constexpr char Id[] = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa";
struct Fixture {
  void Establish() {
    PersistentCommandJournal core(journal);
    JournalIdentityOwner identity(marker, core);
    KeepsakeCommittedInventory onlineOwner(online);
    kufr::offline::OfflineCommittedInventory offlineOwner(offline);
    assert(identity.BeginFactory("worker-device", Identity(false), true));
    assert(core.EstablishScopedIdentity("worker-device", true));
    assert(onlineOwner.Establish("worker-device", true));
    assert(offlineOwner.EstablishBinding("worker-device", true));
    JournalEvidenceInventory a, b;
    assert(onlineOwner.ReadCommitted(a) && offlineOwner.ReadCommitted(b));
    assert(identity.Finalize(Identity(), a, b, true));
    assert(core.PersistReadyMask(63));
  }
  void RequireWorkerIo() {
    journal.explicitServiceSetup = marker.explicitServiceSetup = online.explicitServiceSetup = offline.explicitServiceSetup = false;
  }
  unsigned Io() const { return journal.reads + marker.reads + online.reads + offline.reads + Writes(); }
  unsigned Writes() const { return journal.writes + marker.writes + online.writes + offline.writes; }
  MemoryStorage journal, marker, online, offline;
};

void CreationFailureNeverRunsBootOrRetries() {
  for (bool semaphoreFailure : {false, true}) {
    kernel::Reset();
    Fixture fixture; fixture.Establish(); fixture.RequireWorkerIo();
    const auto before = fixture.Io();
    DeviceStorageStartup startup(fixture.journal, fixture.marker, fixture.online, fixture.offline, Identity());
    EspIdfDeviceStorageWorker worker(startup);
    kernel::JoinBeforeStartupDestruction cleanup;
    kernel::failSemaphore = semaphoreFailure; kernel::failTask = !semaphoreFailure;
    assert(!worker.Start());
    assert(startup.CurrentState() == DeviceStorageStartup::State::Uninitialized && !startup.Runtime());
    assert(fixture.Io() == before && kernel::tasks.empty());
    assert(kernel::semaphoreCreates == 1 && kernel::taskCreates == (semaphoreFailure ? 0u : 1u));
    assert(kernel::semaphoreDeletes == (semaphoreFailure ? 0u : 1u));
    kernel::failSemaphore = kernel::failTask = false;
    assert(!worker.Start());
    assert(kernel::semaphoreCreates == 1 && fixture.Io() == before);
    assert(kernel::taskCreates == (semaphoreFailure ? 0u : 1u) && kernel::tasks.empty());
    assert(worker.MinimumStackFreeBytes() == 0 && worker.MaximumOperationMicros() == 0);
    auto& journal = startup.ControlJournal();
    assert(journal.HasStorageFault() && !journal.Begin(Id, 1));
    assert(fixture.Io() == before);
  }
}

void LegacyServiceBootStaysOnWorkerAndRetainsBytes() {
  kernel::Reset();
  Fixture fixture;
  {
    PersistentCommandJournal legacy(fixture.journal);
    assert(legacy.PersistReadyMask(63));
    assert(legacy.BeginWithIdentity(Id, 2, 99));
    assert(legacy.ResolveRestored(2));
  }
  const auto original = fixture.journal.bytes;
  const auto writes = fixture.Writes();
  fixture.RequireWorkerIo();
  DeviceStorageStartup startup(fixture.journal, fixture.marker, fixture.online, fixture.offline, Identity());
  EspIdfDeviceStorageWorker worker(startup);
  kernel::JoinBeforeStartupDestruction cleanup;
  std::promise<void> bootEntered, releaseBoot, startReturned;
  auto bootEnteredFuture = bootEntered.get_future();
  auto releaseBootFuture = releaseBoot.get_future();
  auto startReturnedFuture = startReturned.get_future();
  std::atomic<bool> blockedFirstRead{false};
  fixture.journal.beforeRead = [&]() {
    if (blockedFirstRead.exchange(true)) return;
    bootEntered.set_value();
    releaseBootFuture.wait();
  };
  std::thread controlStartup([&]() { assert(worker.Start()); startReturned.set_value(); });
  assert(bootEnteredFuture.wait_for(std::chrono::seconds(3)) == std::future_status::ready);
  assert(startReturnedFuture.wait_for(std::chrono::milliseconds(20)) == std::future_status::timeout);
  assert(startup.CurrentState() == DeviceStorageStartup::State::Uninitialized && !startup.Runtime());
  releaseBoot.set_value();
  controlStartup.join();
  assert(startup.CurrentState() == DeviceStorageStartup::State::ServiceRequired);
  assert(startup.RequiresService() && startup.ImageStorageHealthy() && !startup.Runtime());
  kernel::WaitForExit();
  fixture.journal.beforeRead = nullptr;
  assert(kernel::taskDeletes == 1 && kernel::delayCalls == 0);
  assert(fixture.journal.bytes == original && fixture.Writes() == writes);
  assert(fixture.marker.bytes.empty() && fixture.online.bytes.empty() && fixture.offline.bytes.empty());
  const auto beforeReads = fixture.Io();
  auto& journal = startup.ControlJournal();
  assert(journal.ReadyMask() == 63 && journal.HasPending() && journal.HasStorageFault());
  assert(!journal.BeginScoped(1, Id, 2, 99) && !journal.PersistReadyMask(0));
  assert(fixture.Io() == beforeReads && !worker.Start());
}

void ScopedBootPublishesBeforeStartAndRunsTypedJobsOnWorker() {
  kernel::Reset();
  Fixture fixture; fixture.Establish(); fixture.RequireWorkerIo();
  DeviceStorageStartup startup(fixture.journal, fixture.marker, fixture.online, fixture.offline, Identity());
  EspIdfDeviceStorageWorker worker(startup);
  kernel::JoinBeforeStartupDestruction cleanup;
  std::promise<void> bootEntered, releaseBoot, startReturned;
  auto bootEnteredFuture = bootEntered.get_future();
  auto releaseBootFuture = releaseBoot.get_future();
  auto startReturnedFuture = startReturned.get_future();
  std::atomic<bool> blockedFirstRead{false};
  fixture.journal.beforeRead = [&]() {
    if (blockedFirstRead.exchange(true)) return;
    bootEntered.set_value();
    releaseBootFuture.wait();
  };
  std::thread controlStartup([&]() { assert(worker.Start()); startReturned.set_value(); });
  assert(bootEnteredFuture.wait_for(std::chrono::seconds(3)) == std::future_status::ready);
  assert(startReturnedFuture.wait_for(std::chrono::milliseconds(20)) == std::future_status::timeout);
  assert(startup.CurrentState() == DeviceStorageStartup::State::Uninitialized && !startup.Runtime());
  releaseBoot.set_value();
  controlStartup.join();
  // Start's semaphore handoff must already expose the actual ready publication.
  assert(startup.CurrentState() == DeviceStorageStartup::State::Established && startup.Runtime());
  assert(startup.Runtime()->CurrentState() == DeviceStorageRuntime::State::Ready);
  assert(startup.ControlJournal().CurrentEpoch() == 1 && startup.ControlJournal().ReadyMask() == 63);
  kernel::WaitForPark();
  fixture.journal.beforeRead = nullptr;
  assert(kernel::requestedStack == 32768 && kernel::requestedPriority == 2 && kernel::taskName == "device_storage");
  assert(kernel::semaphoreCreates == 1 && kernel::semaphoreDeletes == 1 && kernel::taskCreates == 1);
  assert(pdMS_TO_TICKS(2) == 0 && kernel::requestedDelay == 1);
  assert(worker.MinimumStackFreeBytes() == kernel::SyntheticStackFree);
  assert(worker.MaximumOperationMicros() == kernel::SyntheticOperationMicros);
  const auto io = fixture.Io();
  auto& facade = startup.ControlJournal();
  facade.PollStorage();
  assert(!facade.HasPending() && !facade.HasStorageFault() && facade.CurrentEpoch() == 1);
  assert(facade.FindScoped(1, Id, 3, 123) == CommandJournal::Lookup::Missing);
  assert(fixture.Io() == io && !worker.Start());

  EvidenceStorageOwner::Request request;
  request.operation = EvidenceStorageOwner::Operation::OfflineSettings;
  request.setQuiet = request.quiet = true;
  EvidenceStorageOwner::Token token;
  assert(startup.Runtime()->EvidenceRequests().TrySubmit(request, token));
  assert(fixture.Io() == io); // Source intent submission is RAM-only on control.
  kernel::ReleaseOneTick();
  EvidenceStorageOwner::Completion completion;
  assert(startup.Runtime()->EvidenceRequests().TryReadCompletion(token, completion));
  assert(completion.status == EvidenceStorageOwner::Status::Succeeded);
  assert(startup.Runtime()->EvidenceRequests().ReleaseCompletion(token));
  kufr::offline::Outbox publication;
  assert(startup.Runtime()->EvidenceRequests().TryReadOffline(publication) && publication.quiet);
  assert(fixture.Io() > io);
  assert(kernel::requestedDelay == 1 && kernel::taskCreates == 1);
}

void PendingScopedBootNeverResolvesWithoutInspection() {
  kernel::Reset();
  Fixture fixture; fixture.Establish();
  {
    PersistentCommandJournal core(fixture.journal);
    assert(core.BeginScoped(1, Id, 2, 99));
  }
  const auto bytes = fixture.journal.bytes;
  const auto writes = fixture.Writes();
  fixture.RequireWorkerIo();
  DeviceStorageStartup startup(fixture.journal, fixture.marker, fixture.online, fixture.offline, Identity());
  EspIdfDeviceStorageWorker worker(startup);
  kernel::JoinBeforeStartupDestruction cleanup;
  assert(worker.Start());
  assert(startup.Runtime()->CurrentState() == DeviceStorageRuntime::State::InspectionRequired);
  assert(startup.ControlJournal().HasPending() && startup.ControlJournal().PendingCompartment() == 2);
  kernel::WaitForPark();
  assert(fixture.journal.bytes == bytes && fixture.Writes() == writes);
  assert(!startup.Runtime()->AllowsAdmission());
}
} // namespace

int main() {
  CreationFailureNeverRunsBootOrRetries();
  LegacyServiceBootStaysOnWorkerAndRetainsBytes();
  ScopedBootPublishesBeforeStartAndRunsTypedJobsOnWorker();
  PendingScopedBootNeverResolvesWithoutInspection();
  kernel::Reset();
  std::puts("PASS actual storage worker adapter (synthetic host kernel/clock/HWM only)");
}
