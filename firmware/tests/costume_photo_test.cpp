#include <cassert>
#include <iostream>
#include "../src/features/costume_photo/CostumePhoto.h"
#include "../src/features/local_control/LocalControlCodec.h"

namespace {
const std::string Id = "c85743d5-1b72-41b8-8500-ae3c102b27d6";
std::vector<uint8_t> Jpeg() {
  return {255,216, 255,219,0,3,1, 255,196,0,3,1,
      255,192,0,11,8,0,2,0,2,1,1,17,0,
      255,218,0,8,1,1,0,0,63,0,1,255,217};
}
struct Camera : CostumeCameraPort {
  bool available = true, fail = false;
  int captures = 0;
  std::function<void()> after = []() {};
  bool Available() const override { return available; }
  bool Capture(std::vector<uint8_t>& bytes) override { ++captures; bytes = Jpeg(); after(); return !fail; }
};
struct FakeUpload : CostumeUploadPort {
  int uploads = 0, polls = 0;
  bool fail = false;
  std::function<void()> after = []() {};
  PhotoPoll next = PhotoPoll::Completed;
  bool Upload(const std::vector<uint8_t>& bytes, std::string& operation) override {
    ++uploads; assert(ValidCostumeJpeg(bytes.data(), bytes.size())); operation = Id; after(); return !fail;
  }
  PhotoPoll Poll(const std::string&) override { ++polls; return next; }
};
}
int main() {
  auto jpeg = Jpeg();
  assert(ValidCostumeJpeg(jpeg.data(), jpeg.size()));
  auto metadata = jpeg; metadata.insert(metadata.begin() + 2, {255,225,0,3,0});
  assert(!ValidCostumeJpeg(metadata.data(), metadata.size()));
  auto large = jpeg; large[19] = 3;
  assert(!ValidCostumeJpeg(large.data(), large.size()));
  jpeg.resize(MaximumCostumeJpegBytes + 1);
  assert(!ValidCostumeJpeg(jpeg.data(), jpeg.size()));
  WipeCostumeJpeg(jpeg); assert(jpeg.empty());
  Camera camera;
  FakeUpload upload;
  bool current = true;
  uint64_t clock = 0;
  auto run = [&](bool consent) { return PrepareCostumePhoto(consent, camera, upload,
      [&]() { return current; }, [&]() { return clock; }, [&]() { clock += 250; }); };
  assert(run(false) == PhotoOutcome::Skipped && camera.captures == 0 && upload.uploads == 0);
  camera.available = false;
  assert(run(true) == PhotoOutcome::Unavailable && camera.captures == 0);
  camera.available = true;
  assert(run(true) == PhotoOutcome::Completed && camera.captures == 1 && upload.uploads == 1 && upload.polls == 1);
  camera.after = [&]() { current = false; };
  assert(run(true) == PhotoOutcome::Cancelled && upload.uploads == 1);
  camera.after = []() {}; current = true;
  upload.after = [&]() { current = false; };
  assert(run(true) == PhotoOutcome::Cancelled && upload.polls == 1);
  upload.after = []() {}; current = true; upload.fail = true;
  const int before = upload.uploads;
  assert(run(true) == PhotoOutcome::Failed && upload.uploads == before + 1);
  upload.fail = false; upload.next = PhotoPoll::Pending;
  assert(run(true) == PhotoOutcome::Failed && clock == 120000);
  camera.fail = true;
  assert(run(true) == PhotoOutcome::Failed);
  std::string operation;
  assert(ParseImageAccepted("{\"operationId\":\"" + Id + "\",\"kind\":\"image\",\"status\":\"pending\"}", operation));
  assert(!ParseImageAccepted("{\"operationId\":\"" + Id + "\",\"kind\":\"image\",\"status\":\"pending\",\"status\":\"pending\"}", operation));
  TurnCorrelation correlation;
  correlation.deviceId = "kufr-001"; correlation.bootId = correlation.conversationId = correlation.clientTurnId = Id;
  correlation.generation = 2; correlation.deviceRevision = 3;
  const std::string envelope = "{\"operationId\":\"" + Id + "\",\"kind\":\"image\",\"status\":\"completed\",\"correlation\":{\"deviceId\":\"kufr-001\",\"bootId\":\"" + Id + "\",\"conversationId\":\"" + Id + "\",\"clientTurnId\":\"" + Id + "\",\"generation\":2,\"deviceRevision\":3},\"result\":{\"description\":\"Red cape\",\"possibleCharacter\":\"\",\"uncertainty\":\"high\",\"source\":\"photo\",\"expiresAt\":\"2026-10-09T18:00:00Z\"},\"error\":null}";
  assert(ParseImageOperation(envelope, Id, correlation) == PhotoPoll::Completed);
  correlation.generation = 3;
  assert(ParseImageOperation(envelope, Id, correlation) == PhotoPoll::Failed);
  correlation.generation = 2;
  auto invalid = envelope; invalid.replace(invalid.find("Red cape"), 8, std::string(241, 'x'));
  assert(ParseImageOperation(invalid, Id, correlation) == PhotoPoll::Failed);
  const std::string common = "{\"protocolVersion\":1,\"messageId\":\"" + Id + "\",\"deviceId\":\"kufr-001\",\"bootId\":\"" + Id + "\",\"type\":\"voice.start\",\"expectedRevision\":3";
  LocalControlMessage message;
  auto command = common + "}";
  assert(ParseLocalControlMessage(command.data(), command.size(), message) && !message.imageConsent);
  command = common + ",\"imageConsent\":true}";
  assert(ParseLocalControlMessage(command.data(), command.size(), message) && message.imageConsent);
  command = common + ",\"imageConsent\":false}";
  assert(ParseLocalControlMessage(command.data(), command.size(), message) && !message.imageConsent);
  for (const std::string suffix : {",\"imageConsent\":1}", ",\"imageConsent\":true,\"imageConsent\":false}"}) {
    command = common + suffix;
    assert(!ParseLocalControlMessage(command.data(), command.size(), message));
  }
  std::cout << "Costume photo consent, one-shot upload, cancellation, bounds and stale correlation passed\n";
}
