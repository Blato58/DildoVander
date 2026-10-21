#pragma once
#include <cstddef>
#include <cstring>
#include <deque>
#include <memory>
#include <mutex>
#include <vector>
using QueueHandle_t = void*;
struct AudioTestQueue {
 size_t item = 0;
 unsigned capacity = 0;
 std::mutex gate;
 std::deque<std::vector<unsigned char>> frames;
};
inline QueueHandle_t xQueueCreate(unsigned capacity, size_t item) {
 static std::vector<std::unique_ptr<AudioTestQueue>> queues;
 auto queue = std::make_unique<AudioTestQueue>();
 queue->capacity = capacity; queue->item = item;
 auto* result = queue.get(); queues.push_back(std::move(queue)); return result;
}
inline int xQueueReset(QueueHandle_t handle) {
 auto& queue = *static_cast<AudioTestQueue*>(handle);
 std::lock_guard<std::mutex> lock(queue.gate); queue.frames.clear(); return 1;
}
inline int xQueueReceive(QueueHandle_t handle, void* output, unsigned) {
 auto& queue = *static_cast<AudioTestQueue*>(handle);
 std::lock_guard<std::mutex> lock(queue.gate);
 if (queue.frames.empty()) return 0;
 std::memcpy(output, queue.frames.front().data(), queue.item); queue.frames.pop_front(); return 1;
}
inline int xQueueSend(QueueHandle_t handle, const void* input, unsigned) {
 auto& queue = *static_cast<AudioTestQueue*>(handle);
 std::lock_guard<std::mutex> lock(queue.gate);
 if (queue.frames.size() == queue.capacity) return 0;
 const auto* bytes = static_cast<const unsigned char*>(input);
 queue.frames.emplace_back(bytes, bytes + queue.item); return 1;
}
