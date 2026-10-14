#include "PresentationDiagnostics.h"
#include "PresentationScore.h"

#include <sstream>
#include <cstring>

namespace {
std::string Json(const std::string& value) {
  std::string result = "\"";
  constexpr char Hex[] = "0123456789abcdef";
  for (unsigned char byte : value) {
    if (byte == '"' || byte == '\\') { result += '\\'; result += byte; }
    else if (byte < 32 || byte >= 127) {
      // Diagnostic identity inputs are ASCII; never emit raw control bytes.
      result += "\\u00"; result += Hex[byte >> 4]; result += Hex[byte & 15];
    } else result += byte;
  }
  return result + '"';
}
}

std::string ExportPresentationTrace(const PresentationDiagnostics& d) {
  std::ostringstream out;
  out << "{\"schemaVersion\":1,\"source\":\"firmware-core\",\"coreVersion\":"
      << Json(PresentationScore::SemanticsVersion) << ",\"bundleHash\":" << Json(PresentationScore::BundleHash)
      << ",\"sourceHash\":" << Json(PresentationScore::SourceHash)
      << ",\"compiledHash\":" << Json(PresentationScore::CompiledHash)
      << ",\"sceneId\":" << Json(d.sceneId) << ",\"sceneVersion\":" << d.sceneVersion
      << ",\"sceneHash\":" << Json(d.sceneHash) << ",\"variant\":" << Json(d.variant)
      << ",\"runtimeProfile\":" << Json(std::strcmp(d.sceneId, "dance") == 0 ? "delegated-dance" : "calibrated-local")
      << ",\"calibrationId\":" << Json(std::to_string(d.calibrationId))
      << ",\"bootId\":" << Json(d.bootId.data()) << ",\"operationId\":" << Json(d.operationId.data())
      << ",\"generation\":" << Json(std::to_string(d.generation))
      << ",\"deviceRevision\":" << Json(std::to_string(d.deviceRevision))
      << ",\"slot\":" << unsigned(d.slot) << ",\"quality\":" << Json(d.none ? "none" : (d.reduced ? "reduced" : "full"))
      << ",\"state\":" << Json(d.state) << ",\"evidence\":" << Json(d.evidence)
      << ",\"firstSequence\":" << (d.count ? d.events[d.first].sequence : 0)
      << ",\"lastSequence\":" << d.lastSequence << ",\"droppedEvents\":" << d.droppedEvents;
  if (std::strcmp(d.sceneId, "dance") == 0) out << ",\"track\":" << Json(d.track.data())
      << ",\"audioGeneration\":" << d.audioGeneration << ",\"audioPositionMs\":" << d.audioPositionMs
      << ",\"beatIndex\":" << Json(std::to_string(d.beatIndex));
  out << ",\"events\":[";
  for (size_t index = 0; index < d.count; ++index) {
    const auto& e = d.events[(d.first + index) % d.Capacity];
    if (index) out << ',';
    out << "{\"sequence\":" << e.sequence << ",\"kind\":" << Json(e.kind)
        << ",\"phaseId\":" << Json(e.phaseId) << ",\"phaseIndex\":" << unsigned(e.phaseIndex)
        << ",\"clockDomain\":" << Json(e.clockDomain) << ",\"timeMs\":" << Json(std::to_string(e.timeMs))
        << ",\"dueAt\":" << Json(std::to_string(e.dueAt)) << ",\"latenessMs\":" << e.latenessMs
        << ",\"evidence\":" << Json(e.evidence) << ",\"reason\":" << Json(e.reason)
        << ",\"slot\":" << unsigned(e.slot) << ",\"mask\":" << unsigned(e.mask)
        << ",\"red\":" << unsigned(e.red) << ",\"green\":" << unsigned(e.green)
        << ",\"blue\":" << unsigned(e.blue) << ",\"level\":" << unsigned(e.level)
        << ",\"vibration\":" << (e.vibration ? "true" : "false")
        << ",\"toneHz\":" << e.toneHz << '}';
  }
  return out.str() + "]}";
}
