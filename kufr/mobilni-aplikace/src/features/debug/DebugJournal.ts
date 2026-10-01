import type { DeviceSnapshot } from '../device_connection/FakeDeviceConnection';

export type DebugCategory = 'device' | 'voice' | 'app';
export type DebugCode = 'snapshot' | 'voice_started' | 'voice_finished' | 'voice_failed' | 'voice_stopped' | 'profile_changed' | 'backend_checked' | 'background';
export interface DebugEntry {
  sequence: number;
  time: string;
  category: DebugCategory;
  code: DebugCode;
  values: Record<string, string | number | boolean>;
}

// Project allowed fields at ingress. Neither a raw frame nor arbitrary text can
// enter this journal, even when callers pass extra fields at runtime.
export class DebugJournal {
  private entries: DebugEntry[] = [];
  private sequence = 0;
  private previous = '';

  record(code: DebugCode): void {
    const category = code.startsWith('voice_') || code === 'backend_checked' || code === 'profile_changed'
      ? 'voice' : 'app';
    this.append(category, code, {});
  }

  observe(snapshot: DeviceSnapshot): void {
    const values = {
      source: snapshot.source,
      phase: snapshot.phase,
      controlMode: snapshot.controlMode,
      readyMask: snapshot.readyMask,
      revision: snapshot.revision ?? -1,
      eventSequence: snapshot.diagnostics?.eventSequence ?? -1,
      faults: snapshot.diagnostics?.faultCount ?? 0,
      busy: snapshot.busy,
      conversationPending: snapshot.conversation?.pending === true,
    };
    const signature = JSON.stringify(values);
    if (signature !== this.previous) {
      this.previous = signature;
      this.append('device', 'snapshot', values);
    }
  }

  read(category: DebugCategory | 'all' = 'all'): DebugEntry[] {
    return this.entries.filter((entry) => category === 'all' || entry.category === category)
      .map((entry) => ({ ...entry, values: { ...entry.values } }));
  }

  clear(): void {
    this.entries = [];
    this.previous = '';
  }

  export(): string {
    return JSON.stringify({ format: 'kufr-debug-v1', entries: this.read() }, null, 2);
  }

  private append(category: DebugCategory, code: DebugCode, values: DebugEntry['values']): void {
    this.entries.push({ sequence: ++this.sequence, time: new Date().toISOString(), category, code, values });
    if (this.entries.length > 200) {
      this.entries.shift();
    }
  }
}
