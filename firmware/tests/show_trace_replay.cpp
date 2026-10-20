#include "physical_effects_test_support.h"
#include <iostream>

// Synthetic provenance: the same physical owner, fake I/O, 500 ms calibrated
// travel, compartment 3, no audio port, 10 ms control passes, fresh inspected
// profile, explicit proposal. This executable has no live transport or GPIO.
int main(int argc, char** argv) {
  Fixture fixture;
  fixture.adapter.BindBootIdentity("synthetic-host");
  assert(fixture.Start("523ef590-ce4d-4901-91b3-79de1bfe7e35"));
  for (uint64_t tick = 40; tick <= 12000; tick += 10) {
    if (argc > 1 && std::string(argv[1]) == "stop" && tick == 4000) {
      SelectionStep step;
      fixture.runtime.Stop(step);
    }
    fixture.Tick(tick);
  }
  std::cout << ExportPresentationTrace(fixture.adapter.Diagnostics()) << '\n';
}
