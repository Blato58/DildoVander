#pragma once

#include <stddef.h>

#include <string>

// Parses the bounded 202 response from POST /v1/conversations/{id}/turns.
bool ParseOperationAccepted(const char* data, size_t length,
                            std::string& operationId);
