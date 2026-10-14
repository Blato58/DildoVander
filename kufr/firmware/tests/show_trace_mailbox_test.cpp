#include <cassert>
#include <atomic>
#include <chrono>
#include <iostream>
#include <thread>

#include "../src/features/show_diagnostics/ShowTraceMailbox.h"

int main() {
  ShowTraceMailbox mailbox;
  assert(mailbox.ReadTrace(0).empty());
  PresentationDiagnostics diagnostics;
  diagnostics.bootId = std::string("synthetic-boot");
  diagnostics.operationId = std::string("synthetic-operation");
  diagnostics.state = "stopped";
  diagnostics.evidence = "unknown";
  diagnostics.slot = 1;
  for (unsigned index = 0; index < 100; ++index) {
    PresentationTraceEvent event;
    event.timeMs = 9007199254740993ULL + index;
    diagnostics.Append(event);
  }
  assert(diagnostics.count == 64 && diagnostics.droppedEvents == 36);
  assert(mailbox.Publish(diagnostics, 500));
  const auto current = mailbox.ReadTrace(600);
  assert(current.find("\"observerStale\":false") != std::string::npos);
  assert(current.find("\"droppedEvents\":36") != std::string::npos);
  assert(current.find("9007199254741029") != std::string::npos);
  assert(current.find("\"evidence\":\"unknown\"") != std::string::npos);
  assert(mailbox.ReadTrace(2501).find("\"observerStale\":true") != std::string::npos);
  assert(mailbox.ReadTrace(499).find("\"observerStale\":true") != std::string::npos);

  // A real concurrent subscriber allocates/serializes off the producer path.
  // Time is a host observation, not an ESP real-time performance acceptance.
  std::atomic<bool> done{false};
  std::thread subscriber([&]() {
    while (!done.load()) {
      const auto snapshot = mailbox.ReadTrace(1000);
      assert(!snapshot.empty() && snapshot.front() == '{' && snapshot.back() == '}');
    }
  });
  const auto start = std::chrono::steady_clock::now();
  unsigned accepted = 0;
  for (unsigned index = 0; index < 10000; ++index) {
    accepted += mailbox.Publish(diagnostics, 1000) ? 1 : 0;
  }
  done.store(true);
  subscriber.join();
  const auto us = std::chrono::duration_cast<std::chrono::microseconds>(
      std::chrono::steady_clock::now() - start).count();
  assert(accepted > 0);
  std::cout << "mailbox: 10000 attempts, accepted=" << accepted
            << ", elapsed_us=" << us << ", mailbox_bytes=" << sizeof(ShowTraceMailbox)
            << ", diagnostics_bytes=" << sizeof(PresentationDiagnostics) << '\n';
}
