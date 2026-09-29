#pragma once

#include <stddef.h>

#include "TokenExchangePort.h"

// Parses the fixed token endpoint response. Escaped strings are rejected safely.
bool ParseTokenResponse(const char* data, size_t length,
                        TokenExchangeResponse& response);
