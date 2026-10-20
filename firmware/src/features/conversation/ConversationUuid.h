#pragma once

#include <stdint.h>

#include <string>

// Formats 16 entropy bytes as a canonical RFC 4122 version 4 UUID.
std::string FormatConversationUuidV4(const uint8_t entropy[16]);
