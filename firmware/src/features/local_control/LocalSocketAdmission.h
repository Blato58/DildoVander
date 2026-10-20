#pragma once

#include <stddef.h>
#include <stdint.h>

#include <mutex>

class LocalBearerAuthenticator;

// Transport-only ownership. It never invokes the control or actuator runtime.
class LocalSocketAdmission final {
 public:
  struct Handle {
    Handle(int socketValue = -1, uint64_t generationValue = 0)
        : socket(socketValue), generation(generationValue) {}

    int socket;
    uint64_t generation;
  };

  enum class ReleaseResult {
    None,
    Reserved,
    Active,
  };

  enum class ReserveResult {
    Accepted,
    Unauthorized,
    Busy,
  };

  explicit LocalSocketAdmission(LocalBearerAuthenticator& authenticator);

  bool Reserve(int socket, const char* authorization, size_t length,
               uint64_t nowMs, Handle& handle);
  ReserveResult TryReserve(int socket, const char* authorization, size_t length,
                           uint64_t nowMs, Handle& handle);
  bool Confirm(const Handle& handle, uint64_t nowMs);
  bool IsActive(const Handle& handle) const;
  ReleaseResult Release(const Handle& handle);
  // The transport must close the returned socket before admitting a retry.
  bool ExpirePending(uint64_t nowMs, Handle& expired);

  // Reserve idle recovery under the short admission lock, then release the
  // lock before credential I/O. Handshakes refuse Busy while the hold exists.
  template <typename Action>
  bool RunIfEmpty(const Action& action) {
    if (!BeginRecovery()) return false;
    RecoveryHold hold(*this);
    action();
    return true;
  }

 private:
  class RecoveryHold final {
   public:
    explicit RecoveryHold(LocalSocketAdmission& owner) : owner_(owner) {}
    ~RecoveryHold() { owner_.EndRecovery(); }
    RecoveryHold(const RecoveryHold&) = delete;
    RecoveryHold& operator=(const RecoveryHold&) = delete;
   private:
    LocalSocketAdmission& owner_;
  };
  bool BeginRecovery();
  void EndRecovery();
  enum class State {
    Empty,
    Reserved,
    Active,
  };

  static constexpr uint64_t HandshakeLimitMs = 10000;

  LocalBearerAuthenticator& authenticator_;
  mutable std::mutex mutex_;
  State state_ = State::Empty;
  int socket_ = -1;
  uint64_t generation_ = 0;
  uint64_t nextGeneration_ = 1;
  uint64_t reservedAtMs_ = 0;
  bool recoveryHeld_ = false;
};
