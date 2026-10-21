#pragma once
#include <functional>
#include "BoundedPlaybackOwner.h"

// The owner token alone cannot see a physical Stop that revoked the enclosing
// conversation. Compose both predicates at the raw output-enable/write gate.
class PlaybackAuthorityPort final : public BoundedPlaybackOwner::Port {
 public:
  PlaybackAuthorityPort(BoundedPlaybackOwner::Port& raw, std::function<bool()> current)
      : raw_(raw), current_(std::move(current)) {}
  bool Begin(const BoundedPlaybackOwner::Fence& fence) override {
    Claim claim{this, fence}; return raw_.Begin(View(claim));
  }
  bool Play(const BoundedPlaybackOwner::Fence& fence, const uint8_t* pcm, size_t length) override {
    Claim claim{this, fence}; return raw_.Play(View(claim), pcm, length);
  }
  bool Drain(const BoundedPlaybackOwner::Fence& fence) override {
    Claim claim{this, fence}; return raw_.Drain(View(claim));
  }
  bool CancelPlayback(const BoundedPlaybackOwner::Fence& fence) override {
    Claim claim{this, fence}; return raw_.CancelPlayback(View(claim));
  }
 private:
  struct Claim { PlaybackAuthorityPort* port; BoundedPlaybackOwner::Fence original; };
  static bool Current(void* context, BoundedPlaybackOwner::Scope scope) {
    auto& claim = *static_cast<Claim*>(context);
    return scope == claim.original.scope && claim.port->current_() && claim.original.Current();
  }
  static BoundedPlaybackOwner::Fence View(Claim& claim) {
    return {claim.original.scope, &claim, Current};
  }
  BoundedPlaybackOwner::Port& raw_;
  std::function<bool()> current_;
};
