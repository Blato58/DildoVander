#include <cassert>
#include <atomic>
#include <chrono>
#include <condition_variable>
#include <mutex>
#include <thread>
#include <functional>
#include <future>
#include <iostream>
#include <cstring>
#include "../src/features/voice_turn/EspIdfVoiceAudio.h"
#include "nvs.h"
namespace {
bool verified=false, profileB=false;
std::atomic<int> mute{0}, writes{0}, gpioWrites{0};
std::atomic<bool> partialWrite{false};
std::mutex gpioSignalMutex;
std::condition_variable gpioSignal;
unsigned muteSignals = 0;
std::function<void()> writing;
int captureIterations = 0;
struct CaptureFinished {};
void PumpOnce() {
 captureIterations = 1;
 try { CapturedAudioTask().run(CapturedAudioTask().context); }
 catch (const CaptureFinished&) {}
}
}
int gpio_set_direction(int pin,int mode) { assert(pin==16 && mode==GPIO_MODE_OUTPUT); return 0; }
int gpio_set_level(int pin,int level) {
 assert(pin==16); ++gpioWrites; mute=level;
 if (!level) { std::lock_guard<std::mutex> lock(gpioSignalMutex); ++muteSignals; gpioSignal.notify_all(); }
 return 0;
}
int nvs_open(const char* space,int,nvs_handle_t* handle) { assert(std::strcmp(space,"wand_audio")==0); *handle=1; return ESP_OK; }
int nvs_get_u8(nvs_handle_t,const char* key,uint8_t* value) { assert(std::strcmp(key,"profile_a_ok")==0); *value=verified; return ESP_OK; }
void nvs_close(nvs_handle_t) {}
int i2s_new_channel(const i2s_chan_config_t* config,i2s_chan_handle_t* tx,i2s_chan_handle_t* rx) {
 assert(config->dma_desc_num==8 && config->dma_frame_num==320 && config->auto_clear);
 *tx=reinterpret_cast<void*>(1); *rx=reinterpret_cast<void*>(2); return ESP_OK;
}
int i2s_channel_init_std_mode(i2s_chan_handle_t,const i2s_std_config_t* config) {
 assert(config->clk_cfg.rate==32000 && config->slot_cfg.bits==32 && config->slot_cfg.mode==2);
 if (profileB) assert(config->gpio_cfg.bclk==21 && config->gpio_cfg.ws==41 && config->gpio_cfg.din==42 && config->gpio_cfg.dout==47);
 else assert(config->gpio_cfg.bclk==4 && config->gpio_cfg.ws==5 && config->gpio_cfg.din==6 && config->gpio_cfg.dout==7);
 return ESP_OK;
}
int i2s_channel_enable(i2s_chan_handle_t) { return ESP_OK; }
int i2s_channel_read(i2s_chan_handle_t,void*,size_t,size_t*,unsigned) {
 if (captureIterations-- == 0) throw CaptureFinished{};
 return ESP_FAIL;
}
int i2s_channel_write(i2s_chan_handle_t,const void*,size_t size,size_t* used,unsigned) {
 ++writes; *used=partialWrite ? size - 8 : size; if(writing)writing(); return ESP_OK;
}
class SoftwareAudioBoard final : public AudioBoardPort {
 public:
  bool ready = false;
  bool AudioReady() const override { return ready; }
};
namespace {
class Gate {
 public:
  void Enter() {
    std::unique_lock<std::mutex> lock(mutex);
    entered = true; condition.notify_all();
    condition.wait(lock, [&]() { return released; });
  }
  void Await() {
    std::unique_lock<std::mutex> lock(mutex);
    assert(condition.wait_for(lock, std::chrono::seconds(3), [&]() { return entered; }));
  }
  void Release() { std::lock_guard<std::mutex> lock(mutex); released = true; condition.notify_all(); }
 private:
  std::mutex mutex;
  std::condition_variable condition;
  bool entered = false, released = false;
};
template <typename Action> void MustCompleteWhileIoIsBlocked(Action action) {
  std::packaged_task<void()> task(action);
  auto completed = task.get_future();
  std::thread control(std::move(task));
  assert(completed.wait_for(std::chrono::seconds(3)) == std::future_status::ready);
  control.join(); completed.get();
}
class ScopedFence {
 public:
  BoundedPlaybackOwner::Fence View(uint64_t generation = 0) {
    return {{1, generation}, this, Current};
  }
  void AwaitCheck() {
    std::unique_lock<std::mutex> lock(mutex);
    assert(condition.wait_for(lock, std::chrono::seconds(3), [&]() { return checked; }));
  }
  std::atomic<bool> current{true};
 private:
  static bool Current(void* context, BoundedPlaybackOwner::Scope) {
    auto& fence = *static_cast<ScopedFence*>(context);
    std::lock_guard<std::mutex> lock(fence.mutex);
    fence.checked = true; fence.condition.notify_all();
    return fence.current;
  }
  std::mutex mutex;
  std::condition_variable condition;
  bool checked = false;
};
void BeginCannotReviveAfterOutputLockWait() {
  // The actual raw HAL waits behind an admitted DMA write. Stop and a revoked
  // owner fence must both remain sticky when that wait eventually completes.
  for (unsigned boundary = 0; boundary != 3; ++boundary) {
    EspIdfVoiceAudio audio;
    assert(audio.Initialize(true)); audio.Begin();
    const std::vector<uint8_t> pcm(4096, 32);
    Gate io;
    writing = [&]() { io.Enter(); };
    bool played = true, began = true;
    std::thread old([&]() { played = audio.Play(pcm); }); io.Await();
    ScopedFence scope;
    const auto fence = scope.View();
    unsigned mutedBefore;
    { std::lock_guard<std::mutex> lock(gpioSignalMutex); mutedBefore = muteSignals; }
    std::thread starter([&]() { if (boundary == 0) audio.Begin(); else began = audio.Begin(fence); });
    if (boundary == 0) {
      std::unique_lock<std::mutex> lock(gpioSignalMutex);
      assert(gpioSignal.wait_for(lock, std::chrono::seconds(3), [&]() { return muteSignals > mutedBefore; }));
    } else scope.AwaitCheck();
    if (boundary == 2) scope.current = false;
    else MustCompleteWhileIoIsBlocked([&]() { audio.Stop(); });
    io.Release(); old.join(); starter.join(); writing = {};
    assert(!played && (boundary == 0 || !began));
    assert(!audio.Play(pcm) && mute == 0);
    audio.Begin(); assert(audio.Play(pcm) && audio.Drain()); audio.Stop();
  }
}
void FencedPlaybackMutePreservesSessionAcrossBlockedIo() {
  for (unsigned operation = 0; operation != 3; ++operation) {
    EspIdfVoiceAudio audio;
    assert(audio.Initialize(true));
    ScopedFence oldScope, nextScope;
    const auto oldFence = oldScope.View(), nextFence = nextScope.View(2);
    assert(audio.Begin(oldFence));
    const std::vector<uint8_t> pcm(4096, 32);
    if (operation != 0) assert(audio.Play(oldFence, pcm.data(), pcm.size()));
    Gate io;
    writing = [&]() { io.Enter(); };
    bool completed = true;
    const int before = writes;
    std::thread worker([&]() {
      if (operation == 0) completed = audio.Play(oldFence, pcm.data(), pcm.size());
      else if (operation == 1) completed = audio.Drain(oldFence);
      else completed = audio.CancelPlayback(oldFence);
    });
    io.Await(); oldScope.current = false;
    MustCompleteWhileIoIsBlocked([&]() { assert(audio.MutePlayback()); });
    assert(mute == 0);
    io.Release(); worker.join(); writing = {};
    assert(!completed && writes == before + 1);
    const int flushBefore = writes;
    assert(audio.CancelPlayback(nextFence) && writes == flushBefore + 9);
    assert(audio.Play(nextFence, pcm.data(), pcm.size()) && audio.Drain(nextFence));
    assert(!audio.Play(oldFence, pcm.data(), pcm.size()));
    audio.Stop(); assert(!audio.Play(nextFence, pcm.data(), pcm.size()));
  }
}
void FencedStopAndPartialIoCannotYieldSuccess() {
  for (unsigned operation = 0; operation != 3; ++operation) {
    EspIdfVoiceAudio audio;
    assert(audio.Initialize(true));
    ScopedFence scope; const auto fence = scope.View();
    assert(audio.Begin(fence));
    const std::vector<uint8_t> pcm(4096, 32);
    if (operation != 0) assert(audio.Play(fence, pcm.data(), pcm.size()));
    Gate io; writing = [&]() { io.Enter(); };
    bool completed = true;
    std::thread worker([&]() {
      if (operation == 0) completed = audio.Play(fence, pcm.data(), pcm.size());
      else if (operation == 1) completed = audio.Drain(fence);
      else completed = audio.CancelPlayback(fence);
    });
    io.Await(); MustCompleteWhileIoIsBlocked([&]() { audio.Stop(); });
    io.Release(); worker.join(); writing = {};
    assert(!completed && mute == 0 && !audio.Play(fence, pcm.data(), pcm.size()));
    assert(audio.Begin(fence));
    if (operation != 0) assert(audio.Play(fence, pcm.data(), pcm.size()));
    partialWrite = true;
    bool partial = operation == 0 ? audio.Play(fence, pcm.data(), pcm.size()) :
        operation == 1 ? audio.Drain(fence) : audio.CancelPlayback(fence);
    partialWrite = false;
    assert(!partial && audio.Failed() && mute == 0);
    assert(!audio.Play(fence, pcm.data(), pcm.size()));
  }
}
void ProfileBSoftwareFenceKeepsFactoryMutePinsUntouched() {
  profileB = true;
  const int beforeGpio = gpioWrites;
  SoftwareAudioBoard board; board.ready = true;
  EspIdfVoiceAudio audio;
  assert(audio.Initialize(true, &board));
  ScopedFence oldScope, nextScope;
  const auto old = oldScope.View(), next = nextScope.View(7);
  assert(audio.Begin(old));
  const std::vector<uint8_t> pcm(4096, 32);
  Gate io; writing = [&]() { io.Enter(); };
  bool played = true;
  std::thread worker([&]() { played = audio.Play(old, pcm.data(), pcm.size()); }); io.Await();
  oldScope.current = false;
  MustCompleteWhileIoIsBlocked([&]() { assert(audio.MutePlayback()); });
  io.Release(); worker.join(); writing = {};
  assert(!played);
  assert(audio.CancelPlayback(next));
  assert(audio.Play(next, pcm.data(), pcm.size()) && audio.Drain(next));
  audio.Stop(); assert(gpioWrites == beforeGpio);
  // Software fences and silence writes do not measure profile B's DMA/acoustic tail.
}
}
int main() {
 EspIdfVoiceAudio audio;
 assert(!audio.Initialize(false)); assert(!audio.Initialize(true));
 verified=true; assert(audio.Initialize(true)); assert(mute==0);
 const std::vector<uint8_t> pcm(2048,32);
 assert(!audio.Play(pcm));
 audio.Begin(); audio.Listen(false); assert(audio.Play(pcm)); assert(mute==1);
 assert(audio.Drain()); assert(mute==0);
 audio.Stop(); const int stoppedWrites=writes;
 audio.Listen(true); assert(!audio.Play(pcm)); assert(writes==stoppedWrites && mute==0);
 audio.Begin(); writing=[&]{audio.Stop();};
 assert(!audio.Play(pcm)); assert(mute==0); assert(!audio.Drain());
 writing={}; assert(!audio.Play(pcm)); // Late blocks never reopen this session.
 audio.Begin(); assert(audio.Play(pcm)); assert(audio.Drain()); assert(mute==0);
 audio.Begin(); assert(audio.Play(pcm));
 const int beforeCancel=writes;
 assert(audio.CancelPlayback()); assert(writes==beforeCancel+9 && mute==0);
 assert(audio.Play(pcm)); assert(audio.Drain()); // Response cancellation keeps the session usable.
 audio.Stop(); assert(!audio.CancelPlayback() && !audio.Play(pcm));
 audio.Begin(); writing=[&]{audio.Stop();};
 assert(!audio.CancelPlayback()); writing={}; assert(!audio.Play(pcm));
 const int legacyGpioWrites = gpioWrites;
 profileB=true;
 SoftwareAudioBoard expander;
 EspIdfVoiceAudio audioB;
 assert(!audioB.Initialize(true, &expander)); // Never falls back onto camera pins.
 expander.ready=true;
 assert(audioB.Initialize(true, &expander)); const int hardwareMute = mute;
 assert(audioB.TryCue(220, 100) == PresentationAudioPort::CueResult::Requested);
 const int beforeCue = writes;
 PumpOnce(); assert(writes == beforeCue + 1);
 // Stop during an admitted <=20ms block revokes future blocks. Identical new
 // requests cannot enter until this old worker releases its ownership gate.
 writing = [&] {
   audioB.StopCues();
   assert(audioB.TryCue(220, 100) == PresentationAudioPort::CueResult::Unavailable);
 };
 PumpOnce(); writing = {};
 const int afterStopCue = writes;
 PumpOnce(); assert(writes == afterStopCue);
 assert(audioB.TryCue(220, 100) == PresentationAudioPort::CueResult::Requested);
 PumpOnce(); assert(writes == afterStopCue + 1);
 audioB.Begin();
 assert(audioB.TryCue(220, 100) == PresentationAudioPort::CueResult::Unavailable);
 const int speakingWrites = writes;
 PumpOnce(); assert(writes == speakingWrites); // Refusal cannot cancel speech.
 audioB.Begin(); assert(audioB.Play(pcm)); assert(mute==hardwareMute);
 audioB.Stop(); assert(mute==hardwareMute && !audioB.Play(pcm));
 audioB.Begin(); expander.ready=false; assert(!audioB.Play(pcm) && audioB.Failed());
 assert(gpioWrites == legacyGpioWrites); // Factory SD stays untouched throughout B.
 profileB=false;
 BeginCannotReviveAfterOutputLockWait();
 FencedPlaybackMutePreservesSessionAcrossBlockedIo();
 FencedStopAndPartialIoCannotYieldSuccess();
 ProfileBSoftwareFenceKeepsFactoryMutePinsUntouched();
 std::cout<<"ESP audio profiles, owner fences, stalled IO, sticky Begin/Stop and partial DMA checks passed; host only\n";
}
