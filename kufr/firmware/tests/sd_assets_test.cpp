#include "../src/features/sd_assets/AssetReader.h"
#include <algorithm>
#include <cassert>
#include <cstring>
#include <iostream>
#include <vector>

using namespace kufr::assets;
class FakeFile final : public AssetFilePort {
 public:
  bool Open(const std::string&, AssetInfo& result) override { ++opens; result = info; return open; }
  int Read(uint8_t* bytes, size_t count) override {
    now += delay;
    if (failRead) return -1;
    const size_t size = std::min(count, data.size() - offset);
    std::memcpy(bytes, data.data() + offset, size);
    offset += size;
    return static_cast<int>(size);
  }
  bool Unchanged(const AssetInfo&) override { return unchanged; }
  void Close() override { ++closes; }
  uint64_t NowMs() const override { return now; }
  std::vector<uint8_t> data = {1, 2, 3};
  AssetInfo info{3, true, false};
  bool open = true, failRead = false, unchanged = true;
  size_t offset = 0;
  unsigned opens = 0, closes = 0;
  uint64_t now = 0, delay = 0;
};
int main() {
  AssetBytes bytes;
  std::string error;
  FakeFile good;
  assert(ReadAsset(good, "intro.wav", 16, bytes, error));
  assert(bytes.size == good.data.size() && std::equal(good.data.begin(), good.data.end(), bytes.data.get()) && error.empty() && good.closes == 1);
  for (const std::string& name : {"", "../secret", "dir/file", "dir\\file", ".hidden", "asset..wav", "asset.wav/", "a:secret", "percent%2ffile", "trailing.", "with space.wav"}) {
    FakeFile bad;
    assert(!ReadAsset(bad, name, 16, bytes, error) && bytes.size == 0 && bad.opens == 0);
  }
  FakeFile longName;
  assert(!ReadAsset(longName, std::string(65, 'a'), 16, bytes, error));
  FakeFile nul;
  assert(!ReadAsset(nul, std::string("a\0b", 3), 16, bytes, error));
  FakeFile large;
  assert(!ReadAsset(large, "intro.wav", 2, bytes, error) && large.closes == 1 && error == "asset-too-large");
  FakeFile badLimit;
  assert(!ReadAsset(badLimit, "intro.wav", MaximumAssetBytes + 1, bytes, error));
  assert(!ReadAsset(badLimit, "intro.wav", 0, bytes, error));
  FakeFile directory; directory.info.regular = false;
  assert(!ReadAsset(directory, "directory", 16, bytes, error) && directory.closes == 1);
  FakeFile link; link.info.symlink = true;
  assert(!ReadAsset(link, "linked.wav", 16, bytes, error) && link.closes == 1);
  FakeFile missing; missing.open = false;
  assert(!ReadAsset(missing, "missing.wav", 16, bytes, error) && missing.closes == 1);
  FakeFile readError; readError.failRead = true;
  assert(!ReadAsset(readError, "intro.wav", 16, bytes, error) && readError.closes == 1);
  FakeFile truncated; truncated.info.size = 4;
  assert(!ReadAsset(truncated, "intro.wav", 16, bytes, error) && truncated.closes == 1);
  FakeFile grown; grown.info.size = 2;
  assert(!ReadAsset(grown, "intro.wav", 16, bytes, error) && grown.closes == 1);
  FakeFile replaced; replaced.unchanged = false;
  assert(!ReadAsset(replaced, "intro.wav", 16, bytes, error) && replaced.closes == 1);
  FakeFile timeout; timeout.delay = 10000;
  assert(!ReadAsset(timeout, "intro.wav", 16, bytes, error) && timeout.closes == 1);
  FakeFile empty; empty.info.size = 0; empty.data.clear();
  assert(ReadAsset(empty, "empty.bin", 1, bytes, error) && bytes.size == 0 && empty.closes == 1);
  std::cout << "SD flat asset validation, bounds, symlink, timeout and cleanup tests passed.\n";
}
