#include <cassert>
#include <string>

#include "../src/features/conversation/OperationAcceptedCodec.h"

int main() {
  const std::string id = "8475532e-92b2-487e-b476-7f98f0030e68";
  std::string parsed;
  const std::string valid = " { \"operationId\" : \"" + id + "\" } ";
  assert(ParseOperationAccepted(valid.data(), valid.size(), parsed));
  assert(parsed == id);
  const std::string duplicate =
      "{\"operationId\":\"" + id + "\",\"operationId\":\"" + id + "\"}";
  assert(!ParseOperationAccepted(duplicate.data(), duplicate.size(), parsed));
  assert(parsed.empty());
  const std::string unknown = "{\"operationId\":\"" + id +
                              "\",\"extra\":true}";
  assert(!ParseOperationAccepted(unknown.data(), unknown.size(), parsed));
  const std::string invalid = "{\"operationId\":\"bad\"}";
  assert(!ParseOperationAccepted(invalid.data(), invalid.size(), parsed));
  const std::string escaped = "{\"operationId\":\"bad\\u0022\"}";
  assert(!ParseOperationAccepted(escaped.data(), escaped.size(), parsed));
  assert(!ParseOperationAccepted(nullptr, 0, parsed));
  assert(!ParseOperationAccepted(std::string(129, 'x').data(), 129, parsed));
}
