#pragma once
#include "voice_turn/RealtimeSessionEngine.h"
#include "voice_turn/VoiceWorkflow.h"
#include <condition_variable>
#include <deque>
#include <map>
#include <thread>
#include <memory>

struct MediaCompletion {
  ConversationRuntimeSnapshot snapshot;
  TurnOperationObservation operation;
  uint64_t epoch = 0;
};
// Private platform HAL/HTTP adapter. Only the Host owner takes completion and
// calls PendingProposal; the worker never touches physical runtime/effects.
class BrowserMedia final : public VoiceAudioPort, public CostumeUploadPort {
 public:
  BrowserMedia(const std::string& device, const std::string& boot, ConversationRuntimeState& runtime);
  ~BrowserMedia();
  bool Start(const std::string& session, const std::string& request, bool gptLive,
      const std::string& voice, const std::string& speed, bool imageConsent, bool cameraAvailable,
      const ConversationRuntimeSnapshot& snapshot);
  void Cancel();
  bool OwnsSession(const std::string& session);
  bool Busy() const { return busy_.load(); }
  bool Handle(const std::vector<std::string>& words, const std::vector<uint8_t>& body,
      std::string& extension, std::vector<uint8_t>& attachment, std::string* rejectionCode = nullptr);
  std::string StateHeader();
  std::string AudioObservation();
  bool TakeService(std::string& extension, std::vector<uint8_t>& bytes);
  bool TakeCompletion(MediaCompletion& completion);
  bool Available() const override { return true; }
  void Begin() override;
  void Listen(bool enabled) override;
  bool TakeInput(std::vector<uint8_t>& pcm) override;
  bool Play(const std::vector<uint8_t>& pcm) override;
  bool Drain() override;
  void Stop() override;
  bool CancelPlayback() override;
  bool Failed() const override { return failed_.load(); }
  bool Capture(std::vector<uint8_t>& jpeg);
  bool Upload(const std::vector<uint8_t>& jpeg, std::string& operationId) override;
  PhotoPoll Poll(const std::string& operationId) override;
 private:
  struct Output {
    std::string header;
    std::vector<uint8_t> bytes;
    bool service = false;
    std::string kind = "";
    uint64_t inputEpoch = 0, outputEpoch = 0;
  };
  struct Response { bool ready = false; int status = 0; std::string type; std::vector<uint8_t> bytes; };
  bool Current() const;
  bool TakeInputForEpoch(std::vector<uint8_t>& pcm, const RealtimeSessionEngine& engine, uint32_t engineEpoch);
  void Worker(VoiceWorkflowRequest request);
  void Phase(const char* phase);
  bool Emit(const std::string& kind, const std::vector<uint8_t>& bytes = {}, bool drain = false,
      const std::string& text = "");
  std::string HeaderLocked(const std::string& kind, size_t bytes, bool drain, const std::string& text);
  Response Request(const std::string& kind, const std::string& method, const std::string& path,
      const std::string& key, const std::string& headers, const char* contentType,
      const void* bytes, size_t size, unsigned timeoutMs = 5000);
  bool CloseConversation();
  void ClearLocked();
  void QueueDeleteLocked();
  std::string device_, boot_, session_, conversation_, phase_ = "idle", endpoint_, photoKey_, lastRequestId_, photoWarning_, audioObservation_;
  std::string lastPhase_;
  uint64_t lastPhaseInputEpoch_ = 0, lastPhaseOutputEpoch_ = 0;
  bool lastPhaseListening_ = false;
  TurnCorrelation photoCorrelation_;
  ConversationRuntimeSnapshot snapshot_;
  ConversationRuntimeState& runtime_;
  std::atomic<bool> busy_{false}, cancelled_{false}, failed_{false};
  std::mutex mutex_;
  std::condition_variable changed_;
  std::thread worker_;
  bool cameraAvailable_ = false, done_ = true, listening_ = false, photoRequested_ = false, photoReady_ = false, photoDeclined_ = false;
  uint64_t epoch_ = 0, inputEpoch_ = 0, outputEpoch_ = 0, sequence_ = 0,
      inputSequence_ = 0, jpegSequence_ = 0, pcmBytes_ = 0, drainSequence_ = 0;
  bool drainPending_ = false, drainAcknowledged_ = false, playbackPending_ = false;
  size_t inputBytes_ = 0, eventBytes_ = 0;
  std::deque<Output> output_;
  std::deque<std::vector<uint8_t>> input_, events_;
  std::vector<uint8_t> jpeg_;
  std::map<std::string, std::shared_ptr<Response>> requests_;
  std::unique_ptr<MediaCompletion> completion_;
};
uint64_t MediaWallMs();
std::string MediaUuid();
