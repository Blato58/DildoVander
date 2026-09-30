#include <cassert>
#include <chrono>
#include <future>
#include <thread>

#include "../src/features/local_control/LocalControlInbox.h"

int main() {
  LocalControlInbox inbox("kufr-001", "boot");
  const LocalSocketAdmission::Handle first{7, 1};
  const LocalSocketAdmission::Handle reused{7, 2};
  LocalControlEvent event;
  LocalControlMessage text;
  text.kind = LocalControlMessageKind::Text;
  text.deviceId = "kufr-001";
  text.bootId = "boot";
  text.text = "private pending text";
  LocalControlMessage stop;
  stop.kind = LocalControlMessageKind::Stop;
  stop.deviceId = "kufr-001";
  stop.bootId = "boot";
  stop.messageId = "stop";
  assert(!inbox.Take(event));
  assert(!inbox.Open({}, 0));
  assert(inbox.Open(first, 20));
  assert(!inbox.Open(reused, 21));
  assert(!inbox.Submit(reused, text));
  for (size_t index = 0; index < LocalControlInbox::QueueCapacity; ++index) {
    assert(inbox.Submit(first, text));
  }
  assert(!inbox.Submit(first, text));
  LocalControlMessage wrongStop = stop;
  wrongStop.bootId = "old-boot";
  assert(!inbox.Submit(first, wrongStop));
  assert(inbox.Submit(first, stop));
  assert(!inbox.Submit(first, text));
  inbox.Disconnect(first);
  assert(!inbox.Open(reused, 22));
  assert(inbox.Take(event) && event.kind == LocalControlEventKind::Connected);
  assert(event.handle.generation == 1 && event.connectedAtMs == 20);
  assert(inbox.Take(event) && event.kind == LocalControlEventKind::Stop);
  assert(event.message.messageId == "stop");
  assert(inbox.Take(event) && event.kind == LocalControlEventKind::Disconnected);
  assert(!inbox.Take(event));
  assert(inbox.Open(reused, 30));
  inbox.Disconnect(first);
  assert(inbox.Submit(reused, text));
  assert(inbox.Take(event) && event.kind == LocalControlEventKind::Connected);
  assert(inbox.Take(event) && event.kind == LocalControlEventKind::Message);
  assert(event.message.text == text.text);
  assert(!inbox.Take(event));
  assert(inbox.Submit(reused, text));
  inbox.Disconnect(reused);
  assert(inbox.Take(event) && event.kind == LocalControlEventKind::Disconnected);
  assert(!inbox.Take(event));
  assert(event.message.text.empty());

  LocalControlInbox guarded("kufr-001", "boot");
  assert(guarded.Open(first, 0));
  std::promise<void> attempting;
  std::promise<void> admitted;
  auto attemptingFuture = attempting.get_future();
  auto admittedFuture = admitted.get_future();
  std::thread producer;
  bool began = false;
  assert(guarded.RunIfCurrent(first, [&]() {
    producer = std::thread([&]() {
      attempting.set_value();
      assert(guarded.Submit(first, stop));
      admitted.set_value();
    });
    attemptingFuture.wait();
    assert(admittedFuture.wait_for(std::chrono::milliseconds(20)) ==
           std::future_status::timeout);
    began = true;
  }));
  producer.join();
  assert(began);
  assert(!guarded.RunIfCurrent(first, [&]() { assert(false); }));
  assert(!guarded.RunIfNoUrgent([&]() { assert(false); }));
}
