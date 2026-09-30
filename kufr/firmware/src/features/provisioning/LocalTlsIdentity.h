#pragma once

#include <array>
#include <vector>

#include "DeviceIdentity.h"

class LocalTlsValidator {
 public:
  virtual ~LocalTlsValidator() = default;
  // Validate the key pair and return SHA-256 of the certificate's DER SPKI.
  virtual bool Validate(const uint8_t* certificate, size_t certificateLength,
                        const uint8_t* privateKey, size_t privateKeyLength,
                        std::array<uint8_t, 32>& publicKeyDigest) = 0;
};

// One bounded record makes certificate/key rotation atomic at the storage port.
// The private key is available only while this non-copyable owner is alive.
class LocalTlsIdentity final {
 public:
  // Keep one identity below a single NVS data page so replacement can coexist
  // with the old blob in the small shared prototype partition. Use an EC key.
  static constexpr size_t MaximumCertificatePemSize = 2048;
  static constexpr size_t MaximumPrivateKeyPemSize = 1024;
  static constexpr size_t HeaderSize = 8;
  static constexpr size_t MaximumRecordSize =
      HeaderSize + MaximumCertificatePemSize + MaximumPrivateKeyPemSize;

  LocalTlsIdentity(ProvisioningStorage& storage, LocalTlsValidator& validator);
  ~LocalTlsIdentity();
  LocalTlsIdentity(const LocalTlsIdentity&) = delete;
  LocalTlsIdentity& operator=(const LocalTlsIdentity&) = delete;

  bool IsHealthy() const;
  bool IsProvisioned() const;
  const uint8_t* Certificate() const;
  size_t CertificateLength() const;
  const uint8_t* PrivateKey() const;
  size_t PrivateKeyLength() const;
  const std::array<uint8_t, 32>& PublicKeyDigest() const;
  // PEM lengths include exactly one terminating NUL, with none embedded.
  // A failed write has indeterminate durable state; this instance is disabled.
  // Service recovery must reload/verify the stored SPKI before issuing a QR.
  bool Provision(const uint8_t* certificate, size_t certificateLength,
                 const uint8_t* privateKey, size_t privateKeyLength,
                 const ServiceGate& gate);

 private:
  bool ValidateRecord(std::vector<uint8_t>& record,
                      std::array<uint8_t, 32>& publicKeyDigest) const;
  void Clear();

  ProvisioningStorage& storage_;
  LocalTlsValidator& validator_;
  std::vector<uint8_t> record_;
  std::array<uint8_t, 32> publicKeyDigest_{};
  bool healthy_ = false;
};
