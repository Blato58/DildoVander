#include "AssetReader.h"
#include <algorithm>
#include <new>

namespace kufr { namespace assets {
namespace {
class CloseFile final {
 public:
  explicit CloseFile(AssetFilePort& file) : file_(file) {}
  ~CloseFile() { file_.Close(); }
 private:
  AssetFilePort& file_;
};
}
bool IsAssetFilename(const std::string& name) {
  return !name.empty() && name.size() <= 64 && name.front() != '.' && name.back() != '.' &&
      name.find("..") == std::string::npos &&
      name.find_first_not_of("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-_.") == std::string::npos;
}
bool ReadAsset(AssetFilePort& file, const std::string& name, size_t maximumBytes,
               AssetBytes& output, std::string& error) {
  output = {}; error.clear();
  if (!IsAssetFilename(name) || maximumBytes == 0 || maximumBytes > MaximumAssetBytes) { error = "asset-request-invalid"; return false; }
  CloseFile close(file);
  AssetInfo info;
  if (!file.Open(name, info)) { error = "asset-open-failed"; return false; }
  if (!info.regular || info.symlink) { error = "asset-not-regular"; return false; }
  if (info.size > maximumBytes) { error = "asset-too-large"; return false; }
  AssetBytes bytes;
  bytes.size = static_cast<size_t>(info.size);
  if (bytes.size) {
    bytes.data.reset(new (std::nothrow) uint8_t[bytes.size]);
    if (!bytes.data) { error = "asset-memory-unavailable"; return false; }
  }
  const uint64_t deadline = file.NowMs() + 10000;
  size_t offset = 0;
  while (offset < bytes.size) {
    if (file.NowMs() >= deadline) { error = "asset-read-timeout"; return false; }
    const size_t count = std::min(static_cast<size_t>(1024), bytes.size - offset);
    const int received = file.Read(bytes.data.get() + offset, count);
    if (received <= 0 || static_cast<size_t>(received) > count) { error = "asset-read-failed"; return false; }
    offset += static_cast<size_t>(received);
  }
  uint8_t extra = 0;
  if (file.NowMs() >= deadline || file.Read(&extra, 1) != 0 || !file.Unchanged(info)) { error = "asset-changed-during-read"; return false; }
  output = std::move(bytes);
  return true;
}
} }
