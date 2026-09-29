#pragma once

#include <stddef.h>

#include "TurnPollGuard.h"

// Accepts bounded text-turn operation JSON. Proposals remain unsupported.
bool ParseTurnOperation(const char* data, size_t length,
                        TurnOperationObservation& operation);
