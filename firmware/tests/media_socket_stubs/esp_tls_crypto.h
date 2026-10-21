#pragma once
#include <cstddef>
extern "C" {
int esp_crypto_sha1(const unsigned char*, size_t, unsigned char[20]);
int esp_crypto_base64_encode(unsigned char*, size_t, size_t*, const unsigned char*, size_t);
}
