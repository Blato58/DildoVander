#include "OtaManifest.h"

#include <algorithm>
#include <map>
#include <limits>

namespace kufr { namespace ota {
namespace {

struct Value { std::string text; uint32_t number = 0; bool numeric = false; };
class Json final {
 public:
  explicit Json(const std::string& text) : text_(text) {}
  bool Object(std::map<std::string, Value>& fields) {
    if (!Consume('{')) return false;
    do {
      std::string key;
      Value value;
      if (!String(key, 32) || !Consume(':')) return false;
      Skip();
      if (position_ < text_.size() && text_[position_] == '"') {
        if (!String(value.text, MaximumManifestBytes)) return false;
      } else {
        value.numeric = true;
        if (!Number(value.number)) return false;
      }
      if (!fields.emplace(key, value).second) return false;
      if (Consume('}')) { Skip(); return position_ == text_.size(); }
    } while (Consume(','));
    return false;
  }
 private:
  void Skip() {
    while (position_ < text_.size() &&
        (text_[position_] == ' ' || text_[position_] == '\r' ||
         text_[position_] == '\n' || text_[position_] == '\t')) ++position_;
  }
  bool Consume(char expected) {
    Skip();
    if (position_ == text_.size() || text_[position_] != expected) return false;
    ++position_;
    return true;
  }
  bool String(std::string& value, size_t maximum) {
    if (!Consume('"')) return false;
    while (position_ < text_.size()) {
      unsigned char c = static_cast<unsigned char>(text_[position_++]);
      if (c == '"') return !value.empty();
      // The wire contract deliberately uses ASCII fields and unescaped JSON.
      if (c < 0x20 || c > 0x7e || c == '\\' || value.size() == maximum) return false;
      value.push_back(static_cast<char>(c));
    }
    return false;
  }
  bool Number(uint32_t& value) {
    Skip();
    const size_t start = position_;
    while (position_ < text_.size() && text_[position_] >= '0' && text_[position_] <= '9') {
      const unsigned digit = text_[position_++] - '0';
      if (value > (std::numeric_limits<uint32_t>::max() - digit) / 10) return false;
      value = value * 10 + digit;
    }
    return position_ != start && (position_ - start == 1 || text_[start] != '0');
  }
  const std::string& text_;
  size_t position_ = 0;
};

bool Number(const std::map<std::string, Value>& fields, const char* key, uint32_t& out) {
  auto found = fields.find(key);
  if (found == fields.end() || !found->second.numeric) return false;
  out = found->second.number;
  return true;
}
bool Text(const std::map<std::string, Value>& fields, const char* key, std::string& out) {
  auto found = fields.find(key);
  if (found == fields.end() || found->second.numeric) return false;
  out = found->second.text;
  return true;
}
bool Hex(const std::string& value, size_t length) {
  return value.size() == length && value.find_first_not_of("0123456789abcdef") == std::string::npos;
}
int Base64Digit(char c) {
  const std::string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
  auto found = alphabet.find(c);
  return found == std::string::npos ? -1 : static_cast<int>(found);
}
bool DecodeBase64(const std::string& text, std::vector<uint8_t>& out, size_t maximum) {
  if (text.empty() || text.size() % 4 || text.size() > (maximum + 2) / 3 * 4) return false;
  for (size_t i = 0; i < text.size(); i += 4) {
    const int a = Base64Digit(text[i]), b = Base64Digit(text[i + 1]);
    const int c = text[i + 2] == '=' ? 0 : Base64Digit(text[i + 2]);
    const int d = text[i + 3] == '=' ? 0 : Base64Digit(text[i + 3]);
    const bool pad2 = text[i + 2] == '=', pad1 = text[i + 3] == '=';
    if (a < 0 || b < 0 || c < 0 || d < 0 ||
        (pad2 && !pad1) || ((pad1 || pad2) && i + 4 != text.size()) ||
        (pad2 && (b & 15)) || (pad1 && !pad2 && (c & 3))) return false;
    out.push_back(static_cast<uint8_t>((a << 2) | (b >> 4)));
    if (!pad2) out.push_back(static_cast<uint8_t>((b << 4) | (c >> 2)));
    if (!pad1) out.push_back(static_cast<uint8_t>((c << 6) | d));
  }
  return out.size() <= maximum;
}
std::vector<std::string> Split(const std::string& text, char separator) {
  std::vector<std::string> result;
  size_t from = 0;
  do {
    size_t end = text.find(separator, from);
    result.push_back(text.substr(from, end == std::string::npos ? end : end - from));
    if (end == std::string::npos) break;
    from = end + 1;
  } while (true);
  return result;
}
bool Digits(const std::string& text) {
  return !text.empty() && text.find_first_not_of("0123456789") == std::string::npos;
}
int NumericCompare(const std::string& left, const std::string& right) {
  if (left.size() != right.size()) return left.size() < right.size() ? -1 : 1;
  return left == right ? 0 : left < right ? -1 : 1;
}
std::string WithoutMetadata(const std::string& version) { return version.substr(0, version.find('+')); }
}  // namespace

bool ParseEnvelope(const std::string& json, Envelope& envelope) {
  envelope = {};
  if (json.empty() || json.size() > MaximumManifestBytes) return false;
  std::map<std::string, Value> fields;
  uint32_t schema = 0;
  std::string payload, signature;
  std::vector<uint8_t> decoded;
  Envelope candidate;
  if (!Json(json).Object(fields) || fields.size() != 3 ||
      !Number(fields, "schemaVersion", schema) || schema != 1 ||
      !Text(fields, "payload", payload) || !Text(fields, "signature", signature) ||
      !DecodeBase64(payload, decoded, 2048) ||
      !DecodeBase64(signature, candidate.signature, 80) || candidate.signature.size() < 8) return false;
  candidate.payload.assign(decoded.begin(), decoded.end());
  envelope = std::move(candidate);
  return true;
}

bool ParseManifest(const std::string& payload, Manifest& manifest) {
  manifest = {};
  if (payload.empty() || payload.size() > 2048) return false;
  std::map<std::string, Value> fields;
  uint32_t schema = 0;
  std::string algorithm;
  Manifest candidate;
  if (!Json(payload).Object(fields) || fields.size() != 12 ||
      !Number(fields, "schemaVersion", schema) || schema != 1 ||
      !Text(fields, "version", candidate.version) || !IsSemanticVersion(candidate.version) ||
      !Text(fields, "buildId", candidate.buildId) || !Hex(candidate.buildId, 40) ||
      !Text(fields, "hardware", candidate.hardware) || candidate.hardware.size() > 32 ||
      !Text(fields, "profile", candidate.profile) || candidate.profile.size() > 32 ||
      !Text(fields, "channel", candidate.channel) ||
      (candidate.channel != "stable" && candidate.channel != "development") ||
      !Text(fields, "url", candidate.url) ||
      !Number(fields, "sizeBytes", candidate.sizeBytes) || candidate.sizeBytes < 1024 ||
      !Text(fields, "sha256", candidate.sha256) || !Hex(candidate.sha256, 64) ||
      !Number(fields, "updaterVersion", candidate.updaterVersion) || candidate.updaterVersion == 0 ||
      !Number(fields, "minUpdaterVersion", candidate.minUpdaterVersion) || candidate.minUpdaterVersion == 0 ||
      candidate.minUpdaterVersion > candidate.updaterVersion ||
      !Text(fields, "signatureAlgorithm", algorithm) || algorithm != "ecdsa-p256-sha256" ||
      candidate.url != "/v1/firmware/artifacts/" + candidate.sha256 + ".bin") return false;
  manifest = std::move(candidate);
  return true;
}

bool IsApprovedOrigin(const std::string& origin) {
  if (origin.compare(0, 8, "https://") != 0 || origin.size() > 267) return false;
  std::string host = origin.substr(8);
  if (host.size() > 4 && host.substr(host.size() - 4) == ":443") host.resize(host.size() - 4);
  if (host.empty() || host.size() > 253 || host.front() == '.' || host.back() == '.') return false;
  for (const auto& label : Split(host, '.')) {
    if (label.empty() || label.size() > 63 || label.front() == '-' || label.back() == '-' ||
        label.find_first_not_of("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-") != std::string::npos) return false;
  }
  return true;
}

bool IsSemanticVersion(const std::string& version) {
  if (version.empty() || version.size() > 31) return false;
  auto metadata = Split(version, '+');
  if (metadata.size() > 2) return false;
  if (metadata.size() == 2) {
    for (const auto& part : Split(metadata[1], '.'))
      if (part.empty() || part.find_first_not_of("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-") != std::string::npos) return false;
  }
  const auto dash = metadata[0].find('-');
  auto core = Split(metadata[0].substr(0, dash), '.');
  if (core.size() != 3) return false;
  for (const auto& part : core) if (!Digits(part) || (part.size() > 1 && part[0] == '0') ||
      part.size() > 5 || (part.size() == 5 && part > "65535")) return false;
  if (dash != std::string::npos) {
    for (const auto& part : Split(metadata[0].substr(dash + 1), '.')) {
      if (part.empty() || part.find_first_not_of("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-") != std::string::npos ||
          (Digits(part) && part.size() > 1 && part[0] == '0')) return false;
    }
  }
  return true;
}

int CompareVersions(const std::string& left, const std::string& right) {
  const auto l = WithoutMetadata(left), r = WithoutMetadata(right);
  const auto ld = l.find('-'), rd = r.find('-');
  const auto lc = Split(l.substr(0, ld), '.'), rc = Split(r.substr(0, rd), '.');
  for (size_t i = 0; i < 3; ++i) { int n = NumericCompare(lc[i], rc[i]); if (n) return n; }
  if (ld == std::string::npos || rd == std::string::npos)
    return ld == rd ? 0 : ld == std::string::npos ? 1 : -1;
  const auto lp = Split(l.substr(ld + 1), '.'), rp = Split(r.substr(rd + 1), '.');
  for (size_t i = 0; i < std::min(lp.size(), rp.size()); ++i) {
    const bool ln = Digits(lp[i]), rn = Digits(rp[i]);
    int result = ln && rn ? NumericCompare(lp[i], rp[i]) : ln != rn ? (ln ? -1 : 1) : lp[i] == rp[i] ? 0 : lp[i] < rp[i] ? -1 : 1;
    if (result) return result;
  }
  return lp.size() == rp.size() ? 0 : lp.size() < rp.size() ? -1 : 1;
}

std::string ValidateUpgrade(const Manifest& manifest, const std::string& installedVersion,
                           const std::string& installedBuild, const std::string& channel,
                           uint32_t partitionBytes) {
  if (manifest.hardware != "esp32s3-n16r8" || manifest.profile != "B-effects") return "incompatible-hardware";
  if (manifest.channel != channel || (channel == "stable" && WithoutMetadata(manifest.version).find('-') != std::string::npos)) return "incompatible-channel";
  if (manifest.minUpdaterVersion > UpdaterVersion) return "updater-too-old";
  if (manifest.sizeBytes > partitionBytes || partitionBytes == 0) return "image-too-large";
  if (!IsSemanticVersion(installedVersion)) return "installed-version-invalid";
  if (manifest.buildId == installedBuild || CompareVersions(manifest.version, installedVersion) <= 0) return "no-update";
  return {};
}
} }  // namespace kufr::ota
