#pragma once

#include <string>

// Caller must provide an active hardware entropy source: Wi-Fi or the
// temporarily enabled bootloader RNG source during early startup.
std::string GenerateEspIdfConversationUuid();
