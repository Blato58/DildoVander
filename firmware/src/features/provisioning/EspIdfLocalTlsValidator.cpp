#include "EspIdfLocalTlsValidator.h"

#include "mbedtls/pk.h"
#include "mbedtls/x509_crt.h"
#include "psa/crypto.h"

namespace {

class ParsedIdentity final {
 public:
  ParsedIdentity() {
    mbedtls_x509_crt_init(&certificate);
    mbedtls_pk_init(&privateKey);
  }
  ~ParsedIdentity() {
    mbedtls_pk_free(&privateKey);
    mbedtls_x509_crt_free(&certificate);
  }

  mbedtls_x509_crt certificate;
  mbedtls_pk_context privateKey;
};

}  // namespace

bool EspIdfLocalTlsValidator::Validate(
    const uint8_t* certificate, size_t certificateLength,
    const uint8_t* privateKey, size_t privateKeyLength,
    std::array<uint8_t, 32>& publicKeyDigest) {
  publicKeyDigest.fill(0);
  if (certificate == nullptr || privateKey == nullptr ||
      certificateLength == 0 || privateKeyLength == 0 ||
      psa_crypto_init() != PSA_SUCCESS) {
    return false;
  }
  ParsedIdentity parsed;
  if (mbedtls_x509_crt_parse(&parsed.certificate, certificate,
                            certificateLength) != 0 ||
      parsed.certificate.next != nullptr ||
      mbedtls_pk_parse_key(&parsed.privateKey, privateKey, privateKeyLength,
                          nullptr, 0) != 0 ||
      mbedtls_pk_check_pair(&parsed.certificate.pk, &parsed.privateKey) != 0) {
    return false;
  }
  // pk_raw is the certificate's complete SubjectPublicKeyInfo DER, not the
  // certificate digest or a platform-dependent raw EC point.
  const mbedtls_x509_buf& publicKey = parsed.certificate.pk_raw;
  size_t digestLength = 0;
  const psa_status_t status =
      psa_hash_compute(PSA_ALG_SHA_256, publicKey.p, publicKey.len,
                       publicKeyDigest.data(), publicKeyDigest.size(),
                       &digestLength);
  if (status != PSA_SUCCESS || digestLength != publicKeyDigest.size()) {
    publicKeyDigest.fill(0);
    return false;
  }
  return true;
}
