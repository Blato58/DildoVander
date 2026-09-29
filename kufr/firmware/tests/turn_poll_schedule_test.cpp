#include <cassert>
#include <cstdint>

#include "../src/features/conversation/TurnPollSchedule.h"

int main() {
  TurnPollSchedule schedule;
  assert(!schedule.IsDue(0));
  schedule.Begin(1000, 1000);
  assert(schedule.IsActive());
  assert(!schedule.IsDue(1499));
  assert(schedule.MarkStarted(1500));
  assert(!schedule.MarkStarted(1500));
  schedule.MarkCompleted(1510, true);
  assert(!schedule.IsDue(2009));
  assert(schedule.MarkStarted(2010));
  schedule.MarkCompleted(2020, true);

  assert(schedule.MarkStarted(2520));
  schedule.MarkCompleted(3500, true);
  assert(schedule.MarkStarted(4000));
  schedule.MarkCompleted(4000, true);
  assert(!schedule.IsDue(4999));
  assert(schedule.MarkStarted(5000));
  schedule.MarkCompleted(5000, false);
  assert(!schedule.IsActive());

  schedule.Begin(1000, 1000);
  assert(schedule.IsExpired(999));
  assert(!schedule.IsExpired(45999));
  assert(schedule.IsExpired(46000));
  assert(!schedule.IsDue(46000));
  schedule.Cancel();
  assert(!schedule.IsActive());

  schedule.Begin(1000, 45999);
  assert(!schedule.IsDue(45999));
  assert(schedule.IsExpired(46000));
  schedule.Begin(1000, 46000);
  assert(!schedule.IsActive());
  schedule.Begin(1000, 999);
  assert(!schedule.IsActive());
}
