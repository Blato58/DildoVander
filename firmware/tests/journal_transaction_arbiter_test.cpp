#include <array>
#include <cassert>
#include <future>
#include <chrono>

#include "../src/features/device_control/JournalTransactionArbiter.h"

int main() {
  using Arbiter = JournalTransactionArbiter;
  Arbiter arbiter;
  std::array<Arbiter::Token, Arbiter::Capacity> tokens{};
  for (auto& token : tokens) assert(arbiter.TryBegin(Arbiter::Purpose::Admission, token));
  Arbiter::Token extra;
  assert(!arbiter.TryBegin(Arbiter::Purpose::Evidence, extra));
  assert(!arbiter.TryEnter());
  auto forged = tokens[0];
  ++forged.cancellation;
  assert(!arbiter.Release(forged));
  const auto before = arbiter.Cancellation();
  arbiter.CancelAdmissions();
  assert(arbiter.Cancellation() != before && arbiter.AdmissionsStopped());
  for (const auto& token : tokens) {
    assert(!arbiter.IsCurrent(token));
    assert(arbiter.Release(token));
    assert(!arbiter.Release(token));
  }
  assert(arbiter.TryEnter());
  // An exclusive maintenance token can cover blocked NVS; its short mutex is
  // already released, so urgent invalidation remains independently executable.
  auto cancellation = std::async(std::launch::async, [&]() { arbiter.CancelAdmissions(); });
  const bool immediate = cancellation.wait_for(std::chrono::seconds(1)) == std::future_status::ready;
  arbiter.Leave();
  cancellation.wait();
  assert(immediate);
  assert(!arbiter.ResumeAfterRecovery(before));
  assert(arbiter.ResumeAfterRecovery(arbiter.Cancellation()));
  assert(arbiter.TryBegin(Arbiter::Purpose::Admission, extra));
  assert(arbiter.IsCurrent(extra));
  assert(arbiter.Release(extra));
  // Even idle Stop fences future admissions; evidence obligations may finish.
  arbiter.CancelAdmissions();
  assert(!arbiter.TryBegin(Arbiter::Purpose::Admission, extra));
  assert(arbiter.TryBegin(Arbiter::Purpose::Evidence, extra));
  assert(arbiter.IsCurrent(extra));
  assert(!arbiter.ResumeAfterRecovery(arbiter.Cancellation()));
  assert(arbiter.Release(extra));
  assert(!arbiter.TryBegin(static_cast<Arbiter::Purpose>(255), extra));
}
