#pragma once

#include <stddef.h>

#include "TurnPollGuard.h"

// Accepts bounded text-turn operation JSON, including typed proposals.
bool ParseTurnOperation(const char* data, size_t length,
                        TurnOperationObservation& operation);
