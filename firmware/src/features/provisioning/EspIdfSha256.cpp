#include "EspIdfSha256.h"

#include "psa/crypto.h"

EspIdfSha256::EspIdfSha256()
    : initialized_(psa_crypto_init() == PSA_SUCCESS) {}

bool EspIdfSha256::IsHealthy() const { return initialized_; }

bool EspIdfSha256::Hash(const uint8_t* data, size_t length,
                        std::array<uint8_t, 32>& digest) {
  digest.fill(0);
  if (!initialized_ || data == nullptr) {
    return false;
  }

  size_t digestLength = 0;
  const psa_status_t status =
      psa_hash_compute(PSA_ALG_SHA_256, data, length, digest.data(),
                       digest.size(), &digestLength);
  if (status != PSA_SUCCESS || digestLength != digest.size()) {
    digest.fill(0);
    return false;
  }
  return true;
}
