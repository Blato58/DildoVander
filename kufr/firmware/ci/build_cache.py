"""Keep compiler-date and ESP-IDF image metadata fresh when reusing objects."""

from pathlib import Path

Import("env")

# SCons hashes source, headers and compiler flags, but not the expansion of
# __DATE__/__TIME__. These pinned ESP-IDF 6.1 / PlatformIO 7.1.3 objects must
# always compile, including on incremental builds, to refresh their timestamps.
nodes = [
    node for node in env.Flatten(env.get("PIOBUILDFILES", []))
    if str(node).replace("\\", "/").endswith("/EspIdfBackendNetwork.cpp.o")
]
network_source = Path(env.subst("$PROJECT_SRC_DIR")) / "features/backend_access/EspIdfBackendNetwork.cpp"
if network_source.is_file() and len(nodes) != 1:
    raise ValueError("Backend network timestamp object is missing from the build graph")
for path in (
    "$BUILD_DIR/esp_app_format/esp_app_desc.c.o",
    "$BUILD_DIR/bootloader/esp_bootloader_format/esp_bootloader_desc.c.o",
):
    node = env.File(path)
    if not node.has_builder():
        raise ValueError(f"Timestamp object is missing from the build graph: {node}")
    nodes.append(node)
for node in nodes:
    env.NoCache(node)
    env.AlwaysBuild(node)
