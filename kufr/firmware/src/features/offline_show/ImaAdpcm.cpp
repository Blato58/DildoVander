#include "ImaAdpcm.h"
#include <algorithm>
namespace kufr { namespace offline {
namespace {
const int Steps[] = {7,8,9,10,11,12,13,14,16,17,19,21,23,25,28,31,34,37,41,45,50,55,60,66,73,80,88,97,107,118,130,143,157,173,190,209,230,253,279,307,337,371,408,449,494,544,598,658,724,796,876,963,1060,1166,1282,1411,1552,1707,1878,2066,2272,2499,2749,3024,3327,3660,4026,4428,4871,5358,5894,6484,7132,7845,8630,9493,10442,11487,12635,13899,15289,16818,18500,20350,22385,24623,27086,29794,32767};
const int Index[] = {-1,-1,-1,-1,2,4,6,8};
}
bool ImaAdpcm::Begin(const uint8_t* data, size_t bytes, uint32_t samples) {
  data_ = nullptr;
  if (!data || bytes < 4 || data[2] > 88 || data[3] != 0 ||
      samples == 0 || samples > 192000 || bytes != 4 + samples / 2) return false;
  predictor_ = static_cast<int16_t>(data[0] | (data[1] << 8));
  index_ = data[2]; data_ = data + 4; samples_ = samples; emitted_ = 0;
  return true;
}
size_t ImaAdpcm::Read(uint8_t* pcm, size_t capacity) {
  if (!data_ || !pcm) return 0;
  size_t written = 0;
  while (emitted_ < samples_ && written + 2 <= capacity) {
    if (emitted_) {
      const uint32_t nibbleIndex = emitted_ - 1;
      const uint8_t nibble = (data_[nibbleIndex / 2] >> ((nibbleIndex % 2) * 4)) & 15;
      const int step = Steps[index_];
      int difference = step >> 3;
      if (nibble & 1) difference += step >> 2;
      if (nibble & 2) difference += step >> 1;
      if (nibble & 4) difference += step;
      predictor_ += (nibble & 8) ? -difference : difference;
      predictor_ = std::max(-32768, std::min(32767, predictor_));
      index_ = std::max(0, std::min(88, index_ + Index[nibble & 7]));
    }
    pcm[written++] = static_cast<uint8_t>(predictor_);
    pcm[written++] = static_cast<uint8_t>(predictor_ >> 8);
    ++emitted_;
  }
  return written;
}
} }
