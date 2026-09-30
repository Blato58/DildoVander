#include "LocalSocketAdmission.h"

#include "LocalBearerAuthenticator.h"

LocalSocketAdmission::LocalSocketAdmission(
    LocalBearerAuthenticator& authenticator)
    : authenticator_(authenticator) {}

bool LocalSocketAdmission::Reserve(int socket, const char* authorization,
                                   size_t length, uint64_t nowMs,
                                   Handle& handle) {
  return TryReserve(socket, authorization, length, nowMs, handle) ==
         ReserveResult::Accepted;
}

LocalSocketAdmission::ReserveResult LocalSocketAdmission::TryReserve(
    int socket, const char* authorization, size_t length, uint64_t nowMs,
    Handle& handle) {
  handle = {};
  if (socket < 0) {
    return ReserveResult::Unauthorized;
  }

  std::lock_guard<std::mutex> lock(mutex_);
  if (!authenticator_.Authenticate(authorization, length)) {
    return ReserveResult::Unauthorized;
  }
  if (state_ != State::Empty || nextGeneration_ == 0) {
    return ReserveResult::Busy;
  }

  state_ = State::Reserved;
  socket_ = socket;
  generation_ = nextGeneration_++;
  reservedAtMs_ = nowMs;
  handle = {socket_, generation_};
  return ReserveResult::Accepted;
}

bool LocalSocketAdmission::Confirm(const Handle& handle, uint64_t nowMs) {
  std::lock_guard<std::mutex> lock(mutex_);
  if (state_ != State::Reserved || handle.socket != socket_ ||
      handle.generation != generation_) {
    return false;
  }
  if (nowMs < reservedAtMs_ ||
      nowMs - reservedAtMs_ >= HandshakeLimitMs) {
    return false;
  }
  state_ = State::Active;
  return true;
}

bool LocalSocketAdmission::IsActive(const Handle& handle) const {
  std::lock_guard<std::mutex> lock(mutex_);
  return state_ == State::Active && handle.socket == socket_ &&
         handle.generation == generation_;
}

LocalSocketAdmission::ReleaseResult LocalSocketAdmission::Release(
    const Handle& handle) {
  std::lock_guard<std::mutex> lock(mutex_);
  if (state_ == State::Empty || handle.socket != socket_ ||
      handle.generation != generation_) {
    return ReleaseResult::None;
  }
  const ReleaseResult result = state_ == State::Active
                                   ? ReleaseResult::Active
                                   : ReleaseResult::Reserved;
  state_ = State::Empty;
  socket_ = -1;
  generation_ = 0;
  reservedAtMs_ = 0;
  return result;
}

bool LocalSocketAdmission::ExpirePending(uint64_t nowMs, Handle& expired) {
  expired = {};
  std::lock_guard<std::mutex> lock(mutex_);
  if (state_ != State::Reserved || nowMs < reservedAtMs_ ||
      nowMs - reservedAtMs_ < HandshakeLimitMs) {
    return false;
  }
  expired = {socket_, generation_};
  state_ = State::Empty;
  socket_ = -1;
  generation_ = 0;
  reservedAtMs_ = 0;
  return true;
}
