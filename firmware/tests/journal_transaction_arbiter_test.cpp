#include <array>
#include <cassert>
#include <future>
#include <chrono>

#include "../src/features/device_control/JournalTransactionArbiter.h"

int main() {
  using Arbiter = JournalTransactionArbiter;
  using Result = Arbiter::Result;
  Arbiter arbiter;
  std::array<Arbiter::Token, Arbiter::Capacity> tokens{};
  for (auto& token : tokens) assert(arbiter.TryBeginDetailed(Arbiter::Purpose::Admission, token) == Result::Accepted);
  Arbiter::Token extra;
  extra.id = 999;
  assert(arbiter.TryBeginDetailed(Arbiter::Purpose::Recovery, extra) == Result::Busy && !extra.id);
  assert(!arbiter.TryBegin(Arbiter::Purpose::Evidence, extra));
  assert(!arbiter.TryEnter());
  auto forged = tokens[0];
  ++forged.cancellation;
  assert(!arbiter.Release(forged));
  const auto before = arbiter.Cancellation();
  arbiter.CancelAdmissions();
  assert(arbiter.Cancellation() != before && arbiter.AdmissionsStopped());
  assert(arbiter.TryBeginDetailed(Arbiter::Purpose::Admission, extra) == Result::Rejected && !extra.id);
  for (const auto& token : tokens) {
    assert(!arbiter.IsCurrent(token));
    assert(arbiter.Release(token));
    assert(!arbiter.Release(token));
  }
  assert(arbiter.TryEnter());
  assert(arbiter.TryBeginDetailed(Arbiter::Purpose::Evidence, extra) == Result::Busy && !extra.id);
  assert(arbiter.TryBeginDetailed(Arbiter::Purpose::Recovery, extra) == Result::Busy && !extra.id);
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
  assert(arbiter.TryBeginDetailed(Arbiter::Purpose::Evidence, extra) == Result::Accepted);
  assert(arbiter.IsCurrent(extra));
  assert(!arbiter.ResumeAfterRecovery(arbiter.Cancellation()));
  assert(arbiter.Release(extra));
  assert(arbiter.TryBeginDetailed(static_cast<Arbiter::Purpose>(255), extra) == Result::Rejected && !extra.id);
  assert(!arbiter.TryBegin(static_cast<Arbiter::Purpose>(255), extra));
  // Recovery and evidence keep their bounded result capacity after Stop; all
  // original leases must drain before that same fence may reopen admission.
  for (size_t index = 0; index < tokens.size(); ++index) {
    const auto purpose = index % 2 ? Arbiter::Purpose::Evidence : Arbiter::Purpose::Recovery;
    assert(arbiter.TryBeginDetailed(purpose, tokens[index]) == Result::Accepted);
    assert(arbiter.IsCurrent(tokens[index]));
  }
  assert(arbiter.TryBeginDetailed(Arbiter::Purpose::Recovery, extra) == Result::Busy);
  assert(!arbiter.ResumeAfterRecovery(arbiter.Cancellation()));
  for (const auto& token : tokens) assert(arbiter.Release(token));
  assert(arbiter.ResumeAfterRecovery(arbiter.Cancellation()));
  assert(arbiter.TryBeginDetailed(Arbiter::Purpose::Admission, extra) == Result::Accepted);
  assert(arbiter.Release(extra));
}
