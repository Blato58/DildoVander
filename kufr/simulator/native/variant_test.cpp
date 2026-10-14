#include "../../firmware/tests/physical_effects_test_support.h"

int main() {
  Fixture fixture;
  assert(fixture.adapter.SelectVariant(true));
  assert(fixture.Start("523ef590-ce4d-4901-91b3-79de1bfe7e35"));
  assert(!fixture.adapter.SelectVariant(false));
  assert(std::string(fixture.adapter.Diagnostics().variant) == "short");
  for (uint64_t time = 40; time <= 12000; time += 10) fixture.Tick(time);
  assert(fixture.presentation.State() == WandPresentationState::Idle);
  assert(fixture.adapter.SelectVariant(false));
  assert(fixture.Start("523ef590-ce4d-4901-91b3-79de1bfe7e36", 12000));
  assert(std::string(fixture.adapter.Diagnostics().variant) == "main");
  assert(!fixture.adapter.SelectVariant(true));
}
