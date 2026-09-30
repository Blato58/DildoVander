#include "LocalPairingCredential.h"

#include <algorithm>

namespace {

constexpr uint8_t Magic[] = {'K', 'L', 'C', '1'};
constexpr size_t StateOffset = 4;
constexpr size_t ReservedOffset = 5;
constexpr size_t PairingDigestOffset = 6;
constexpr size_t TokenDigestOffset = PairingDigestOffset + 32;
constexpr size_t MinimumSecretSize = 16;
constexpr size_t MaximumSecretSize = 64;

template <size_t Size>
class WipeArray final {
 public:
  explicit WipeArray(std::array<uint8_t, Size>& bytes) : bytes_(bytes) {}

  ~WipeArray() {
    volatile uint8_t* output = bytes_.data();
    for (size_t index = 0; index < Size; ++index) {
      output[index] = 0;
    }
  }

 private:
  std::array<uint8_t, Size>& bytes_;
};

template <size_t Size>
bool IsZero(const std::array<uint8_t, Size>& bytes) {
  return std::all_of(bytes.begin(), bytes.end(),
                     [](uint8_t value) { return value == 0; });
}

bool SameDigest(const std::array<uint8_t, 32>& left,
                const std::array<uint8_t, 32>& right) {
  volatile uint8_t difference = 0;
  for (size_t index = 0; index < left.size(); ++index) {
    difference = static_cast<uint8_t>(difference | (left[index] ^ right[index]));
  }
  return difference == 0;
}

}  // namespace

LocalPairingCredential::LocalPairingCredential(
    LocalPairingStorage& storage, Sha256Port& hasher,
    PairingRandomPort& random)
    : storage_(storage), hasher_(hasher), random_(random) {
  std::array<uint8_t, RecordSize> record{};
  WipeArray<RecordSize> wipe(record);
  size_t length = 0;
  const ProvisioningReadResult result =
      storage_.Read(record.data(), record.size(), length);
  if (result == ProvisioningReadResult::Missing) {
    healthy_ = true;
    return;
  }
  if (result != ProvisioningReadResult::Found || length != RecordSize ||
      !std::equal(Magic, Magic + sizeof(Magic), record.begin()) ||
      record[ReservedOffset] != 0) {
    return;
  }
  if (record[StateOffset] == static_cast<uint8_t>(State::AwaitingPairing)) {
    state_ = State::AwaitingPairing;
  } else if (record[StateOffset] == static_cast<uint8_t>(State::Paired)) {
    state_ = State::Paired;
  } else {
    return;
  }
  std::copy(record.begin() + PairingDigestOffset,
            record.begin() + TokenDigestOffset,
            pairingDigest_.begin());
  std::copy(record.begin() + TokenDigestOffset, record.end(),
            tokenDigest_.begin());
  if ((state_ == State::AwaitingPairing &&
       (IsZero(pairingDigest_) || !IsZero(tokenDigest_))) ||
      (state_ == State::Paired &&
       (!IsZero(pairingDigest_) || IsZero(tokenDigest_)))) {
    FailClosed();
    return;
  }
  healthy_ = true;
}

LocalPairingCredential::~LocalPairingCredential() {
  FailClosed();
}

bool LocalPairingCredential::IsHealthy() const { return healthy_; }

bool LocalPairingCredential::IsPairingReady() const {
  return healthy_ && state_ == State::AwaitingPairing;
}

bool LocalPairingCredential::IsPaired() const {
  return healthy_ && state_ == State::Paired;
}

bool LocalPairingCredential::ProvisionPairingSecret(
    const uint8_t* secret, size_t length, const ServiceGate& gate) {
  if (!healthy_ || !gate.ServiceModeActive() ||
      !gate.ActuatorSupplyDisconnected() || secret == nullptr ||
      length < MinimumSecretSize || length > MaximumSecretSize) {
    return false;
  }
  std::array<uint8_t, DigestSize> pairingDigest{};
  WipeArray<DigestSize> wipe(pairingDigest);
  if (!hasher_.Hash(secret, length, pairingDigest) ||
      IsZero(pairingDigest)) {
    return false;
  }
  const std::array<uint8_t, DigestSize> emptyDigest{};
  return WriteRecord(State::AwaitingPairing, pairingDigest, emptyDigest);
}

bool LocalPairingCredential::Pair(
    const uint8_t* secret, size_t secretLength,
    std::array<uint8_t, TokenSize>& issuedToken) {
  issuedToken.fill(0);
  if (!IsPairingReady() || secret == nullptr ||
      secretLength < MinimumSecretSize ||
      secretLength > MaximumSecretSize) {
    return false;
  }
  std::array<uint8_t, DigestSize> suppliedDigest{};
  WipeArray<DigestSize> wipeSupplied(suppliedDigest);
  if (!hasher_.Hash(secret, secretLength, suppliedDigest) ||
      !SameDigest(suppliedDigest, pairingDigest_) ||
      !random_.Fill(issuedToken.data(), issuedToken.size()) ||
      IsZero(issuedToken)) {
    issuedToken.fill(0);
    return false;
  }
  std::array<uint8_t, DigestSize> tokenDigest{};
  WipeArray<DigestSize> wipeTokenDigest(tokenDigest);
  if (!hasher_.Hash(issuedToken.data(), issuedToken.size(), tokenDigest) ||
      IsZero(tokenDigest)) {
    issuedToken.fill(0);
    return false;
  }
  const std::array<uint8_t, DigestSize> emptyDigest{};
  if (!WriteRecord(State::Paired, emptyDigest, tokenDigest)) {
    issuedToken.fill(0);
    return false;
  }
  return true;
}

bool LocalPairingCredential::VerifyToken(const uint8_t* token, size_t length) {
  if (!IsPaired() || token == nullptr || length != TokenSize) {
    return false;
  }
  std::array<uint8_t, DigestSize> suppliedDigest{};
  WipeArray<DigestSize> wipe(suppliedDigest);
  return hasher_.Hash(token, length, suppliedDigest) &&
         SameDigest(suppliedDigest, tokenDigest_);
}

bool LocalPairingCredential::WriteRecord(
    State state, const std::array<uint8_t, DigestSize>& pairingDigest,
    const std::array<uint8_t, DigestSize>& tokenDigest) {
  std::array<uint8_t, RecordSize> record{};
  WipeArray<RecordSize> wipe(record);
  std::copy(Magic, Magic + sizeof(Magic), record.begin());
  record[StateOffset] = static_cast<uint8_t>(state);
  std::copy(pairingDigest.begin(), pairingDigest.end(),
            record.begin() + PairingDigestOffset);
  std::copy(tokenDigest.begin(), tokenDigest.end(),
            record.begin() + TokenDigestOffset);
  if (!storage_.Write(record.data(), record.size())) {
    FailClosed();
    return false;
  }
  state_ = state;
  pairingDigest_ = pairingDigest;
  tokenDigest_ = tokenDigest;
  return true;
}

void LocalPairingCredential::FailClosed() {
  volatile uint8_t* pairingBytes = pairingDigest_.data();
  volatile uint8_t* tokenBytes = tokenDigest_.data();
  for (size_t index = 0; index < DigestSize; ++index) {
    pairingBytes[index] = 0;
    tokenBytes[index] = 0;
  }
  state_ = State::Missing;
  healthy_ = false;
}
