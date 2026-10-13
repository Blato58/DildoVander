#!/usr/bin/env bash
set -euo pipefail
cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.."
result_dir=$(mktemp -d "${TMPDIR:-/tmp}/kufr-dance.XXXXXXXX")
flags=(-std=c++14 -Wall -Wextra -Werror -pthread)
g++ "${flags[@]}" src/features/dance/DanceAssets.cpp tests/dance_core_test.cpp -o "$result_dir/core"
"$result_dir/core"
g++ "${flags[@]}" src/features/dance/DanceCoordinator.cpp src/features/physical_effects/PhysicalEffects.cpp \
  tests/dance_coordinator_test.cpp -o "$result_dir/coordinator"
"$result_dir/coordinator"
g++ "${flags[@]}" -DKUFR_DANCE_HOST_TEST -I tests/dance_stubs -I tests/sd_stubs \
  src/features/dance/DanceAssets.cpp src/features/dance/EspIdfDancePlayer.cpp \
  src/features/sd_assets/EspIdfSdAssets.cpp src/features/sd_assets/AssetReader.cpp \
  tests/dance_player_test.cpp -o "$result_dir/player"
(cd -- "$result_dir" && ./player)
g++ "${flags[@]}" -I tests/sd_stubs src/features/sd_assets/AssetReader.cpp \
  src/features/sd_assets/EspIdfSdAssets.cpp tests/espidf_sd_assets_test.cpp -o "$result_dir/sd"
"$result_dir/sd"
printf 'Dance core, coordinator, playback and SD reservation passed. Results: %s\n' "$result_dir"
