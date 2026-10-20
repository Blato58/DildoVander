#pragma once

#include "NetworkSettings.h"

enum class NetworkSettingsLoadResult {
  Missing,
  Loaded,
  Failed,
};

NetworkSettingsLoadResult LoadEspIdfNetworkSettings(NetworkSettings& settings,
                                                    bool nvsInitialized);
