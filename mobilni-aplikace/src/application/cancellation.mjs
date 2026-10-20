/** Cancellation without requiring newer AbortSignal convenience methods. */
export function throwIfAborted(signal) {
  if (signal?.aborted) throw signal.reason ?? new DOMException('The operation was aborted.', 'AbortError');
}
