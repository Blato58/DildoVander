#include "LocalTlsIdentity.h"

#include <algorithm>

namespace {

constexpr uint8_t Magic[] = {'K', 'T', 'L', '1'};

void Wipe(std::vector<uint8_t>& bytes) {
  volatile uint8_t* data = bytes.data();
  for (size_t index = 0; index < bytes.size(); ++index) {
    data[index] = 0;
  }
  bytes.clear();
}

size_t ReadLength(const std::vector<uint8_t>& record, size_t offset) {
  return static_cast<size_t>(record[offset]) |
         (static_cast<size_t>(record[offset + 1]) << 8);
}

void WriteLength(std::vector<uint8_t>& record, size_t offset, size_t length) {
  record[offset] = static_cast<uint8_t>(length & 0xff);
  record[offset + 1] = static_cast<uint8_t>(length >> 8);
}

bool IsBoundedPem(const uint8_t* bytes, size_t length, size_t maximumLength) {
  constexpr char Prefix[] = "-----BEGIN ";
  return bytes != nullptr && length > sizeof(Prefix) &&
         length <= maximumLength &&
         bytes[length - 1] == 0 &&
         std::find(bytes, bytes + length - 1, uint8_t{0}) ==
             bytes + length - 1 &&
         std::equal(Prefix, Prefix + sizeof(Prefix) - 1, bytes);
}

}  // namespace

LocalTlsIdentity::LocalTlsIdentity(
    ProvisioningStorage& storage, LocalTlsValidator& validator)
    : storage_(storage), validator_(validator), record_(MaximumRecordSize, 0) {
  size_t length = 0;
  const ProvisioningReadResult result =
      storage_.Read(record_.data(), record_.size(), length);
  if (result == ProvisioningReadResult::Missing) {
    Clear();
    healthy_ = true;
    return;
  }
  if (result != ProvisioningReadResult::Found || length < HeaderSize ||
      length > record_.size()) {
    Clear();
    return;
  }
  // The unread tail is initialized to zero and contains no key material.
  record_.resize(length);
  if (!ValidateRecord(record_, publicKeyDigest_)) {
    Clear();
    return;
  }
  healthy_ = true;
}

LocalTlsIdentity::~LocalTlsIdentity() { Clear(); }

bool LocalTlsIdentity::IsHealthy() const { return healthy_; }

bool LocalTlsIdentity::IsProvisioned() const {
  return healthy_ && !record_.empty();
}

const uint8_t* LocalTlsIdentity::Certificate() const {
  return IsProvisioned() ? record_.data() + HeaderSize : nullptr;
}

size_t LocalTlsIdentity::CertificateLength() const {
  return IsProvisioned() ? ReadLength(record_, 4) : 0;
}

const uint8_t* LocalTlsIdentity::PrivateKey() const {
  return IsProvisioned()
             ? record_.data() + HeaderSize + CertificateLength()
             : nullptr;
}

size_t LocalTlsIdentity::PrivateKeyLength() const {
  return IsProvisioned() ? ReadLength(record_, 6) : 0;
}

const std::array<uint8_t, 32>& LocalTlsIdentity::PublicKeyDigest() const {
  return publicKeyDigest_;
}

bool LocalTlsIdentity::Provision(
    const uint8_t* certificate, size_t certificateLength,
    const uint8_t* privateKey, size_t privateKeyLength,
    const ServiceGate& gate) {
  if (!healthy_ || !gate.ServiceModeActive() ||
      !gate.ActuatorSupplyDisconnected() ||
      !IsBoundedPem(certificate, certificateLength,
                    MaximumCertificatePemSize) ||
      !IsBoundedPem(privateKey, privateKeyLength, MaximumPrivateKeyPemSize)) {
    return false;
  }
  std::vector<uint8_t> candidate(
      HeaderSize + certificateLength + privateKeyLength, 0);
  std::copy(Magic, Magic + sizeof(Magic), candidate.begin());
  WriteLength(candidate, 4, certificateLength);
  WriteLength(candidate, 6, privateKeyLength);
  std::copy(certificate, certificate + certificateLength,
            candidate.begin() + HeaderSize);
  std::copy(privateKey, privateKey + privateKeyLength,
            candidate.begin() + HeaderSize + certificateLength);
  std::array<uint8_t, 32> digest{};
  if (!ValidateRecord(candidate, digest)) {
    Wipe(candidate);
    return false;
  }
  if (!storage_.Write(candidate.data(), candidate.size())) {
    Wipe(candidate);
    Clear();
    healthy_ = false;
    return false;
  }
  Clear();
  record_.swap(candidate);
  publicKeyDigest_ = digest;
  return true;
}

bool LocalTlsIdentity::ValidateRecord(
    std::vector<uint8_t>& record,
    std::array<uint8_t, 32>& publicKeyDigest) const {
  publicKeyDigest.fill(0);
  if (record.size() < HeaderSize ||
      !std::equal(Magic, Magic + sizeof(Magic), record.begin())) {
    return false;
  }
  const size_t certificateLength = ReadLength(record, 4);
  const size_t privateKeyLength = ReadLength(record, 6);
  if (certificateLength > MaximumCertificatePemSize ||
      privateKeyLength > MaximumPrivateKeyPemSize ||
      record.size() != HeaderSize + certificateLength + privateKeyLength) {
    return false;
  }
  const uint8_t* certificate = record.data() + HeaderSize;
  const uint8_t* privateKey = certificate + certificateLength;
  return IsBoundedPem(certificate, certificateLength,
                      MaximumCertificatePemSize) &&
         IsBoundedPem(privateKey, privateKeyLength, MaximumPrivateKeyPemSize) &&
         validator_.Validate(certificate, certificateLength,
                             privateKey, privateKeyLength, publicKeyDigest) &&
         std::any_of(publicKeyDigest.begin(), publicKeyDigest.end(),
                     [](uint8_t value) { return value != 0; });
}

void LocalTlsIdentity::Clear() {
  Wipe(record_);
  publicKeyDigest_.fill(0);
}
