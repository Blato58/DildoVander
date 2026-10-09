#pragma once

#include <stddef.h>
#include <stdint.h>

#include <array>
#include <mutex>

#include "DeviceIdentity.h"
#include "ProvisioningStorage.h"

class Sha256Port {
 public:
  virtual ~Sha256Port() = default;
  virtual bool Hash(const uint8_t* data, size_t length,
                    std::array<uint8_t, 32>& digest) = 0;
};

class PairingRandomPort {
 public:
  virtual ~PairingRandomPort() = default;
  // Must fail unless a hardware entropy source is active.
  virtual bool Fill(uint8_t* output, size_t length) = 0;
};

// Kept separate from the backend device credential NVS record.
class LocalPairingStorage {
 public:
  virtual ~LocalPairingStorage() = default;
  virtual ProvisioningReadResult Read(uint8_t* data, size_t capacity,
                                      size_t& length) = 0;
  virtual bool Write(const uint8_t* data, size_t length) = 0;
};

// Holds only hashes. The bearer token itself belongs to the pairing transport
// and must never be written to NVS or a firmware image.
class LocalPairingCredential final {
 public:
  static constexpr size_t TokenSize = 32;
  static constexpr size_t DigestSize = 32;
  static constexpr size_t RecordSize = 70;

  LocalPairingCredential(LocalPairingStorage& storage, Sha256Port& hasher,
                         PairingRandomPort& random);
  ~LocalPairingCredential();
  LocalPairingCredential(const LocalPairingCredential&) = delete;
  LocalPairingCredential& operator=(const LocalPairingCredential&) = delete;

  bool IsHealthy() const;
  bool IsPairingReady() const;
  bool IsPaired() const;
  // Only the authenticated Security1 BLE endpoint may call this first-use API.
  // Existing or corrupt records are never replaced; rotation uses ServiceGate.
  bool InitializeFromAuthenticatedBle(std::array<uint8_t, TokenSize>& issuedToken);
  bool ProvisionPairingSecret(const uint8_t* secret, size_t length,
                              const ServiceGate& gate);
  bool Pair(const uint8_t* secret, size_t secretLength,
            std::array<uint8_t, TokenSize>& issuedToken);
  bool VerifyToken(const uint8_t* token, size_t length);

 private:
  enum class State : uint8_t {
    Missing = 0,
    AwaitingPairing = 1,
    Paired = 2,
  };

  bool WriteRecord(State state,
                   const std::array<uint8_t, DigestSize>& pairingDigest,
                   const std::array<uint8_t, DigestSize>& tokenDigest);
  void FailClosed();
  bool IssueToken(std::array<uint8_t, TokenSize>& issuedToken);

  LocalPairingStorage& storage_;
  Sha256Port& hasher_;
  PairingRandomPort& random_;
  State state_ = State::Missing;
  std::array<uint8_t, DigestSize> pairingDigest_{};
  std::array<uint8_t, DigestSize> tokenDigest_{};
  bool healthy_ = false;
  mutable std::mutex mutex_;
};
