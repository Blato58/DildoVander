import type { LocalVoiceState } from './LocalVoiceClient';
import type { LocalDanceState } from './LocalDanceClient';
import type { LocalConversationState } from './LocalConversationClient';

export type ControlMode = 'observer' | 'manual';
export type DevicePhase = 'ready' | 'running' | 'stopped' | 'disconnected';

export interface DeviceSnapshot {
  source: 'local-fake' | 'e2-simulator' | 'native-device';
  controlMode: ControlMode;
  phase: DevicePhase;
  readyMask: number;
  activeCompartment: number | null;
  busy: boolean;
  revision?: number;
  status: string;
  conversation?: LocalConversationState;
  voice?: LocalVoiceState;
  dance?: LocalDanceState;
  diagnostics?: {
    deviceId?: string;
    lastObservedAtMs?: number;
    bootId: string;
    devicePhase: string;
    eventSequence: number;
    deviceUptimeMs: number;
    receivedAtMs: number;
    roundTripMs: number | null;
    faultCount: number;
  };
}

const manualLeaseDurationMs = 6000;

// Development-only state machine. It never opens a network connection.
export class FakeDeviceConnection {
  private state: DeviceSnapshot = {
    source: 'local-fake',
    controlMode: 'observer',
    phase: 'ready',
    readyMask: 63,
    activeCompartment: null,
    busy: false,
    status: 'Webová simulace je připravena. Ke kufru není připojena.',
  };
  private leaseExpiresAtMs = 0;
  private completionTimer: number | null = null;

  constructor(private readonly onChange: (snapshot: DeviceSnapshot) => void) {
    this.publish();
  }

  snapshot(): DeviceSnapshot {
    this.expireLeaseIfNeeded();
    return { ...this.state };
  }

  acquire(): boolean {
    this.expireLeaseIfNeeded();
    if (this.state.controlMode === 'manual' || this.state.phase === 'stopped') {
      return false;
    }
    this.state.controlMode = 'manual';
    this.leaseExpiresAtMs = performance.now() + manualLeaseDurationMs;
    this.state.status = 'Ruční řízení simulace převzato.';
    this.publish();
    return true;
  }

  heartbeat(): void {
    this.expireLeaseIfNeeded();
    if (this.state.controlMode === 'manual') {
      this.leaseExpiresAtMs = performance.now() + manualLeaseDurationMs;
    }
  }

  release(): void {
    if (this.state.controlMode !== 'manual') {
      return;
    }
    if (this.state.phase === 'running') {
      this.stop();
    }
    this.state.controlMode = 'observer';
    this.leaseExpiresAtMs = 0;
    this.state.status = this.state.phase === 'stopped'
      ? 'Řízení uvolněno. Před pokračováním je nutná místní kontrola.'
      : 'Řízení uvolněno. Simulace je jen ke čtení.';
    this.publish();
  }

  select(compartment: number): boolean {
    this.expireLeaseIfNeeded();
    if (!this.canChangeCompartment(compartment) ||
        (this.state.readyMask & (1 << (compartment - 1))) === 0) {
      return false;
    }
    this.state.phase = 'running';
    this.state.activeCompartment = compartment;
    this.state.status = `Simulovaný výběr přihrádky ${compartment} probíhá.`;
    this.publish();
    this.completionTimer = window.setTimeout(() => {
      this.completionTimer = null;
      if (this.state.phase !== 'running' ||
          this.state.activeCompartment !== compartment) {
        return;
      }
      this.state.readyMask &= ~(1 << (compartment - 1));
      this.state.activeCompartment = null;
      this.state.phase = 'ready';
      this.state.status = `Simulovaná sekvence ${compartment} skončila. Otevření nebylo fyzicky ověřeno.`;
      this.publish();
    }, 1200);
    return true;
  }

  confirmReload(compartment: number): boolean {
    this.expireLeaseIfNeeded();
    if (!this.canChangeCompartment(compartment) ||
        (this.state.readyMask & (1 << (compartment - 1))) !== 0) {
      return false;
    }
    this.state.readyMask |= 1 << (compartment - 1);
    this.state.status = `Založení přihrádky ${compartment} bylo potvrzeno jen v simulaci.`;
    this.publish();
    return true;
  }

  stop(): void {
    if (this.completionTimer !== null) {
      window.clearTimeout(this.completionTimer);
      this.completionTimer = null;
    }
    if (this.state.activeCompartment !== null) {
      this.state.readyMask &= ~(1 << (this.state.activeCompartment - 1));
    }
    this.state.phase = 'stopped';
    this.state.activeCompartment = null;
    this.state.status = 'Stop v simulaci. Výsledek rozběhnutého výběru je neznámý; vyžaduje kontrolu.';
    this.publish();
  }

  inspectLocally(): void {
    if (this.state.phase !== 'stopped') {
      return;
    }
    this.state.phase = 'ready';
    this.state.status = 'Místní kontrola byla simulována. Žádný fyzický kufr nebyl zkontrolován.';
    this.publish();
  }

  private canChangeCompartment(compartment: number): boolean {
    return Number.isInteger(compartment) && compartment >= 1 &&
      compartment <= 6 && this.state.controlMode === 'manual' &&
      this.state.phase === 'ready';
  }

  private expireLeaseIfNeeded(): void {
    if (this.state.controlMode === 'manual' &&
        performance.now() >= this.leaseExpiresAtMs) {
      this.release();
    }
  }

  private publish(): void {
    this.onChange({ ...this.state });
  }
}
