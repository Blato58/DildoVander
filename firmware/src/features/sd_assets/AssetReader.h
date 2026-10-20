#pragma once
#include <cstddef>
#include <cstdint>
#include <string>
#include <memory>

namespace kufr { namespace assets {
constexpr size_t MaximumAssetBytes = 262144;
struct AssetInfo { uint64_t size = 0; bool regular = false; bool symlink = false; };
struct AssetBytes { std::unique_ptr<uint8_t[]> data; size_t size = 0; };
class AssetFilePort {
 public:
  virtual ~AssetFilePort() = default;
  virtual bool Open(const std::string& name, AssetInfo& info) = 0;
  virtual int Read(uint8_t* bytes, size_t count) = 0;
  virtual bool Unchanged(const AssetInfo& original) = 0;
  virtual void Close() = 0;
  virtual uint64_t NowMs() const = 0;
};
bool IsAssetFilename(const std::string& name);
bool ReadAsset(AssetFilePort& file, const std::string& name, size_t maximumBytes,
               AssetBytes& output, std::string& error);
} }
