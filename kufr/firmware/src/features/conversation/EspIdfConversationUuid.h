#pragma once

#include <string>

// Call only after Wi-Fi starts, so the ESP-IDF hardware RNG has external entropy.
std::string GenerateEspIdfConversationUuid();
