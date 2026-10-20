#pragma once
#include <algorithm>
#include <array>
#include <cmath>
#include <cstdint>
#include <vector>

// Bounded polyphase FIR. Precompute the rational-rate kernels once; the audio
// task does only integer multiply/add, never software double trigonometry.
// Continuous phase/history across arbitrary DMA/network chunks, 12-sample delay.
class PcmRateConverter {
 public:
  PcmRateConverter(unsigned inputRate, unsigned outputRate)
      : inputRate_(inputRate), outputRate_(outputRate) {
    unsigned a = inputRate, b = outputRate;
    while (b) { const unsigned remainder = a % b; a = b; b = remainder; }
    divisor_ = a;
    if (!inputRate || !outputRate || outputRate / divisor_ > 32) return;
    kernels_.resize(outputRate / divisor_);
    const double cutoff = 0.45 * std::min(1.0, static_cast<double>(outputRate) / inputRate);
    for (size_t phase = 0; phase < kernels_.size(); ++phase) {
      std::array<double, 24> weights{};
      double total = 0;
      for (int tap = -11; tap <= 12; ++tap) {
        const double distance = static_cast<double>(phase) / kernels_.size() - tap;
        const double window = 0.5 + 0.5 * std::cos(3.141592653589793 * distance / 12.0);
        const double x = 2 * cutoff * distance;
        const double coefficient = std::abs(x) < 1e-9 ? 2 * cutoff :
            2 * cutoff * std::sin(3.141592653589793 * x) / (3.141592653589793 * x);
        weights[tap + 11] = std::abs(distance) <= 12 ? coefficient * window : 0;
        total += weights[tap + 11];
      }
      for (size_t tap = 0; tap < weights.size(); ++tap)
        kernels_[phase][tap] = static_cast<int32_t>(std::lround(weights[tap] / total * Scale));
    }
  }
  void Reset() { inputCount_ = outputCount_ = 0; samples_.fill(0); }
  void Push(int16_t sample, std::vector<int16_t>& output) {
    if (kernels_.empty()) return;
    samples_[inputCount_ % samples_.size()] = sample;
    const uint64_t latest = inputCount_++;
    while (outputCount_ * inputRate_ / outputRate_ <= latest) {
      const uint64_t position = outputCount_ * inputRate_;
      const int64_t center = static_cast<int64_t>(position / outputRate_) - 12;
      const auto& kernel = kernels_[(position % outputRate_) / divisor_];
      int64_t sum = 0;
      for (int tap = -11; tap <= 12; ++tap) {
        const int64_t index = center + tap;
        const int16_t value = index < 0 ? 0 : samples_[static_cast<uint64_t>(index) % samples_.size()];
        sum += static_cast<int64_t>(value) * kernel[tap + 11];
      }
      const int64_t rounded = (sum + (sum >= 0 ? Scale / 2 : -Scale / 2)) / Scale;
      output.push_back(static_cast<int16_t>(std::max<int64_t>(-32768, std::min<int64_t>(32767, rounded))));
      ++outputCount_;
    }
  }
 private:
  static constexpr int64_t Scale = 1 << 20;
  unsigned inputRate_, outputRate_, divisor_ = 1;
  uint64_t inputCount_ = 0, outputCount_ = 0;
  std::array<int16_t, 48> samples_{};
  std::vector<std::array<int32_t, 24>> kernels_;
};
