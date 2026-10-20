#include "EspIdfLocalTlsGenerator.h"

#include <cstring>
#include "esp_random.h"
#include "mbedtls/pk.h"
#include "mbedtls/x509_crt.h"
#include "psa/crypto.h"

namespace {
void TrimPem(std::vector<uint8_t>& bytes) {
  const size_t length = std::strlen(reinterpret_cast<const char*>(bytes.data())) + 1;
  // PEM writers first place DER at the buffer's tail. Clear those private-key
  // bytes before shrinking; wiping only the final PEM length would miss them.
  volatile uint8_t* tail = bytes.data();
  for (size_t index = length; index < bytes.size(); ++index) { tail[index] = 0; }
  bytes.resize(length);
}

struct GeneratedIdentity {
  GeneratedIdentity() {
    mbedtls_pk_init(&key);
    mbedtls_x509write_crt_init(&certificate);
  }
  ~GeneratedIdentity() {
    mbedtls_x509write_crt_free(&certificate);
    mbedtls_pk_free(&key);
    if (keyId != 0) { psa_destroy_key(keyId); }
  }
  mbedtls_pk_context key;
  mbedtls_x509write_cert certificate;
  mbedtls_svc_key_id_t keyId = 0;
};
}

bool EspIdfLocalTlsGenerator::Generate(std::vector<uint8_t>& certificate,
                                      std::vector<uint8_t>& privateKey) {
  if (hostname_.empty() || psa_crypto_init() != PSA_SUCCESS) { return false; }
  GeneratedIdentity generated;
  psa_key_attributes_t attributes = PSA_KEY_ATTRIBUTES_INIT;
  psa_set_key_type(&attributes, PSA_KEY_TYPE_ECC_KEY_PAIR(PSA_ECC_FAMILY_SECP_R1));
  psa_set_key_bits(&attributes, 256);
  psa_set_key_usage_flags(&attributes, PSA_KEY_USAGE_EXPORT | PSA_KEY_USAGE_SIGN_HASH);
  psa_set_key_algorithm(&attributes, PSA_ALG_ECDSA(PSA_ALG_SHA_256));
  const auto result = psa_generate_key(&attributes, &generated.keyId);
  psa_reset_key_attributes(&attributes);
  if (result != PSA_SUCCESS || mbedtls_pk_copy_from_psa(generated.keyId, &generated.key) != 0) {
    return false;
  }
  mbedtls_x509write_crt_set_version(&generated.certificate, MBEDTLS_X509_CRT_VERSION_3);
  mbedtls_x509write_crt_set_md_alg(&generated.certificate, MBEDTLS_MD_SHA256);
  mbedtls_x509write_crt_set_subject_key(&generated.certificate, &generated.key);
  mbedtls_x509write_crt_set_issuer_key(&generated.certificate, &generated.key);
  std::array<uint8_t, 16> serial{};
  esp_fill_random(serial.data(), serial.size());
  serial[0] = static_cast<uint8_t>((serial[0] & 0x7f) | 1);
  const std::string subject = "CN=" + hostname_;
  mbedtls_x509_san_list san{};
  san.node.type = MBEDTLS_X509_SAN_DNS_NAME;
  san.node.san.unstructured_name.p = reinterpret_cast<unsigned char*>(&hostname_[0]);
  san.node.san.unstructured_name.len = hostname_.size();
  if (mbedtls_x509write_crt_set_serial_raw(&generated.certificate, serial.data(), serial.size()) != 0 ||
      mbedtls_x509write_crt_set_subject_name(&generated.certificate, subject.c_str()) != 0 ||
      mbedtls_x509write_crt_set_issuer_name(&generated.certificate, subject.c_str()) != 0 ||
      mbedtls_x509write_crt_set_subject_alternative_name(&generated.certificate, &san) != 0 ||
      mbedtls_x509write_crt_set_validity(&generated.certificate, "20200101000000", "20991231235959") != 0 ||
      mbedtls_x509write_crt_set_basic_constraints(&generated.certificate, 0, -1) != 0 ||
      mbedtls_x509write_crt_set_key_usage(&generated.certificate, MBEDTLS_X509_KU_DIGITAL_SIGNATURE) != 0) {
    return false;
  }
  certificate.assign(LocalTlsIdentity::MaximumCertificatePemSize, 0);
  privateKey.assign(LocalTlsIdentity::MaximumPrivateKeyPemSize, 0);
  if (mbedtls_x509write_crt_pem(&generated.certificate, certificate.data(), certificate.size()) != 0 ||
      mbedtls_pk_write_key_pem(&generated.key, privateKey.data(), privateKey.size()) != 0) {
    return false;
  }
  TrimPem(certificate);
  TrimPem(privateKey);
  return true;
}
