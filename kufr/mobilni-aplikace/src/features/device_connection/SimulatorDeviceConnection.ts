import { LocalVoiceClient, type VoiceSettings } from './LocalVoiceClient';
import type { DeviceSnapshot } from './FakeDeviceConnection';
import { LocalConversationClient, type ConversationDeviceState } from './LocalConversationClient';

interface ProtocolSnapshot extends ConversationDeviceState {
  type: 'state.snapshot';
  voice?: VoiceSettings;
  deviceId: string;
  bootId: string;
  deviceUptimeMs: number;
  eventSequence: number;
  revision: number;
  controlMode: 'standalone' | 'manual' | 'recovery';
  controlLeaseId: string | null;
  phase: string;
  activeCommandId: string | null;
  readyMask: number;
  faults: string[];
  lastResult: { commandId: string; status: string } | null;
}

type PendingCommand = {
  kind: 'select' | 'reload';
  compartment: number;
  requestId: string;
  revision: number;
  readyMask: number;
  leaseId: string;
  sentAt: number;
};

type ActiveCommand = {
  id: string;
  kind: PendingCommand['kind'];
  compartment: number;
  revision: number;
  encoded: string;
  sentAt: number;
  retries: number;
};

export interface ControlSocket {
  readonly readyState: number;
  addEventListener(type: string, listener: (event: { data?: unknown }) => void): void;
  send(frame: string): void;
  close(): void;
}

export interface ProtocolConnectionOptions {
  source: DeviceSnapshot['source'];
  deviceId: string;
  createSocket: () => ControlSocket;
  parseFrame?: (frame: string) => Record<string, unknown>;
  autoConnect?: boolean;
}

// One owner of lease, command retry and uncertain-result reconciliation, shared
// by the development proxy and the native pinned-TLS transport.
export class ProtocolDeviceConnection {
  private readonly voice = new LocalVoiceClient(
    (message) => this.send(message), () => this.publish(), () => this.socket?.close(),
  );
  private readonly conversation = new LocalConversationClient(
    (message) => this.send(message),
    () => this.publish(),
    () => this.socket?.close(),
  );
  private socket: ControlSocket | null = null;
  private device: ProtocolSnapshot | null = null;
  private sequence = 0;
  private pendingCommand: PendingCommand | null = null;
  private activeCommand: ActiveCommand | null = null;
  private unresolvedCommand: Pick<ActiveCommand, 'id' | 'kind' | 'compartment' | 'revision'> | null = null;
  private retryTimer: number | null = null;
  private requestTimer: number | null = null;
  private commandTimer: number | null = null;
  private resultTimer: number | null = null;
  private staleTimer: number | null = null;
  private lastMessageAt = 0;
  private lastDeviceSampleAt = 0;
  private lastDeviceUptimeMs = 0;
  private roundTripMs = Number.POSITIVE_INFINITY;
  private readonly requests = new Map<string, number>();
  private acquirePending = false;
  private releasePending = false;
  private awaitingRevision: number | null = null;
  private reconnectAttempt = 0;
  private suspended = false;
  private state: DeviceSnapshot = {
    source: 'e2-simulator',
    controlMode: 'observer',
    phase: 'disconnected',
    readyMask: 0,
    activeCompartment: null,
    busy: false,
    status: 'Připojuji hostitelský simulátor…',
  };

  constructor(
    private readonly onChange: (snapshot: DeviceSnapshot) => void,
    private readonly options: ProtocolConnectionOptions,
  ) {
    this.state.source = options.source;
    this.publish();
    if (options.autoConnect !== false) {
      this.connect();
    }
  }

  snapshot(): DeviceSnapshot {
    return this.viewSnapshot();
  }

  submitText(text: string): boolean {
    return this.viewSnapshot().conversation?.canSubmit === true && this.conversation.submit(text);
  }

  configureVoice(mode: VoiceSettings['mode'], voice: VoiceSettings['voice']): boolean {
    return this.voice.configure(mode, voice);
  }
  startVoice(): boolean { return this.voice.start(); }
  stopVoice(): boolean { return this.voice.stop(); }

  cancelConversation(): boolean {
    return this.conversation.cancel();
  }

  connect(): void {
    this.suspended = false;
    if (this.retryTimer !== null) {
      window.clearTimeout(this.retryTimer);
      this.retryTimer = null;
    }
    if (this.socket !== null && this.socket.readyState <= 1) {
      return;
    }
    const socket = this.options.createSocket();
    this.socket = socket;
    this.state.phase = 'disconnected';
    this.state.controlMode = 'observer';
    this.state.busy = false;
    this.state.status = 'Připojuji hostitelský simulátor…';
    this.publish();
    socket.addEventListener('open', () => {
      if (this.socket !== socket) { return; }
      this.sendRequest('hello', { deviceId: this.options.deviceId });
      this.armRequestTimeout('Odpověď simulátoru na připojení nepřišla.');
    });
    socket.addEventListener('message', (event) => {
      if (this.socket === socket) { this.receive(event.data); }
    });
    socket.addEventListener('close', () => {
      if (this.socket === socket) {
        this.rememberUnresolvedCommand();
        this.socket = null;
        this.device = null;
        this.pendingCommand = null;
        this.activeCommand = null;
        this.acquirePending = false;
        this.releasePending = false;
        this.awaitingRevision = null;
        this.requests.clear();
        this.voice.invalidate();
        this.conversation.invalidate();
        this.clearTimers();
        this.state = {
          ...this.state,
          controlMode: 'observer',
          phase: 'disconnected',
          activeCompartment: null,
          busy: false,
          status: 'Spojení se simulátorem bylo přerušeno. Výsledek případného povelu je nejistý.',
        };
        this.publish();
        if (!this.suspended && !document.hidden) {
          const delayMs = this.options.source === 'native-device'
            ? Math.min(8000, 500 * 2 ** Math.min(this.reconnectAttempt++, 4)) * (0.8 + Math.random() * 0.4)
            : 1500;
          this.retryTimer = window.setTimeout(() => this.connect(), delayMs);
        }
      }
    });
    socket.addEventListener('error', () => {
      if (this.socket !== socket) { return; }
      socket.close();
    });
  }

  acquire(): boolean {
    if (!this.device || this.state.controlMode !== 'observer' ||
        this.state.phase !== 'ready' || this.state.busy) {
      return false;
    }
    this.acquirePending = true;
    this.state.busy = true;
    this.state.status = 'Žádám simulátor o ruční lease…';
    this.publish();
    const sent = this.send({
      type: 'control.acquire',
      messageId: crypto.randomUUID(),
      deviceId: this.device.deviceId,
      bootId: this.device.bootId,
    });
    if (!sent) {
      this.acquirePending = false;
      this.state.busy = false;
      this.state.status = 'Převzetí nebylo odesláno; spojení není dostupné.';
      this.publish();
    }
    if (sent) {
      this.armRequestTimeout('Ruční lease nebyl potvrzen; spojení obnovuji.');
    }
    return sent;
  }

  heartbeat(): void {
    if (!this.device || this.state.phase === 'disconnected') {
      return;
    }
    if (performance.now() - this.lastMessageAt > 3000) {
      this.socket?.close();
      return;
    }
    const fields = {
      deviceId: this.device.deviceId,
      bootId: this.device.bootId,
    };
    if (this.device.controlLeaseId) {
      this.sendRequest('heartbeat', {
        ...fields,
        controlLeaseId: this.device.controlLeaseId,
      });
    } else {
      this.sendRequest('state.get', fields);
    }
  }

  release(): void {
    this.conversation.invalidate();
    this.pendingCommand = null;
    this.rememberUnresolvedCommand();
    this.clearCommand();
    if (!this.device?.controlLeaseId) {
      return;
    }
    this.send({
      type: 'control.release',
      messageId: crypto.randomUUID(),
      deviceId: this.device.deviceId,
      bootId: this.device.bootId,
      controlLeaseId: this.device.controlLeaseId,
    });
    this.releasePending = true;
    this.state.controlMode = 'observer';
    this.state.busy = true;
    this.state.status = 'Uvolňuji ruční řízení simulátoru…';
    this.publish();
  }

  suspend(): void {
    this.conversation.invalidate();
    this.suspended = true;
    this.pendingCommand = null;
    this.rememberUnresolvedCommand();
    this.clearCommand();
    if (this.retryTimer !== null) {
      window.clearTimeout(this.retryTimer);
      this.retryTimer = null;
    }
    this.socket?.close();
  }

  select(compartment: number): boolean {
    return this.prepareCommand('select', compartment);
  }

  confirmReload(compartment: number): boolean {
    return this.prepareCommand('reload', compartment);
  }

  stop(): void {
    if (!this.device) {
      return;
    }
    this.conversation.invalidate('Stop zneplatnil rozhovor; opožděná odpověď se nezobrazí.');
    this.pendingCommand = null;
    this.rememberUnresolvedCommand();
    this.clearCommand();
    this.state.busy = true;
    this.state.status = 'Odesílám Stop; čekám na potvrzený stav simulátoru…';
    this.publish();
    this.send({
      type: 'safety.stop',
      messageId: crypto.randomUUID(),
      deviceId: this.device.deviceId,
      bootId: this.device.bootId,
    });
  }

  private prepareCommand(kind: PendingCommand['kind'], compartment: number): boolean {
    if (!this.device?.controlLeaseId || this.pendingCommand !== null ||
        this.activeCommand !== null || this.state.busy ||
        this.state.phase !== 'ready' || this.conversation.snapshot().pending || !Number.isInteger(compartment) ||
        compartment < 1 || compartment > 6) {
      return false;
    }
    const ready = (this.device.readyMask & (1 << (compartment - 1))) !== 0;
    if (ready !== (kind === 'select')) {
      return false;
    }
    const requestId = crypto.randomUUID();
    this.pendingCommand = {
      kind,
      compartment,
      requestId,
      revision: this.device.revision,
      readyMask: this.device.readyMask,
      leaseId: this.device.controlLeaseId,
      sentAt: performance.now(),
    };
    this.state.busy = true;
    this.state.status = 'Ověřuji aktuální stav simulátoru před povelem…';
    this.publish();
    const sent = this.send({
      type: 'heartbeat',
      messageId: requestId,
      deviceId: this.device.deviceId,
      bootId: this.device.bootId,
      controlLeaseId: this.device.controlLeaseId,
    });
    if (!sent) {
      this.pendingCommand = null;
      this.state.busy = false;
      this.publish();
      return false;
    }
    this.armRequestTimeout('Aktuální čas zařízení nepřišel; povel nebyl odeslán.', 550);
    return true;
  }

  private receive(raw: unknown): void {
    if (typeof raw !== 'string') {
      this.socket?.close();
      return;
    }
    let message: Record<string, unknown>;
    try {
      message = this.options.parseFrame
        ? this.options.parseFrame(raw)
        : JSON.parse(raw) as Record<string, unknown>;
    } catch {
      this.socket?.close();
      return;
    }
    if (message === null || Array.isArray(message) || message.protocolVersion !== 1 || typeof message.type !== 'string') {
      this.socket?.close();
      return;
    }
    this.lastMessageAt = performance.now();
    if (this.staleTimer !== null) {
      window.clearTimeout(this.staleTimer);
    }
    this.staleTimer = window.setTimeout(() => this.socket?.close(), 3000);
    if (message.type === 'welcome' || message.type === 'state.snapshot') {
      const candidate = message.type === 'welcome' ? message.snapshot : message;
      if (!this.isSnapshot(candidate) || candidate.deviceId !== this.options.deviceId) {
        this.socket?.close();
        return;
      }
      const acquisitionCompleted = this.acquirePending && candidate.controlMode === 'manual';
      this.applySnapshot(candidate);
      if (message.type === 'welcome' || acquisitionCompleted) {
        this.clearRequestTimeout();
        this.reconnectAttempt = 0;
      }
    } else if (message.type === 'voice.ack') {
      this.voice.receive(message);
    } else if (message.type === 'conversation.ack' || message.type === 'conversation.result') {
      this.conversation.receive(message);
    } else if (message.type === 'command.ack') {
      const active = this.activeCommand;
      if (!active || message.commandId !== active.id ||
          message.bootId !== this.device?.bootId) {
        return;
      }
      this.clearCommandTimer();
      this.state.busy = message.status === 'accepted';
      if (message.status !== 'accepted') {
        this.activeCommand = null;
        this.state.activeCompartment = null;
      }
      this.state.status = message.status === 'accepted'
        ? 'Povel simulátor přijal; čekám na výsledek nebo nový snapshot.'
        : `Simulátor povel odmítl: ${String(message.errorCode)}.`;
      this.publish();
      if (message.status === 'accepted') {
        this.armResultTimeout();
      } else {
        this.socket?.close();
      }
    } else if (message.type === 'selection.result') {
      if (message.commandId !== this.activeCommand?.id ||
          message.bootId !== this.device?.bootId) {
        return;
      }
      this.clearCommand();
      this.state.busy = false;
      this.state.status = message.status === 'completed'
        ? 'Simulovaná sekvence skončila. Fyzické otevření nebylo ověřeno.'
        : 'Výsledek výběru je nejistý. Zkontrolujte stav simulátoru.';
      this.publish();
    } else if (message.type === 'heartbeat.ack' && this.device) {
      const pending = this.pendingCommand;
      if (pending && message.requestMessageId === pending.requestId) {
        if (message.bootId !== this.device.bootId ||
            message.controlLeaseId !== pending.leaseId ||
            message.revision !== pending.revision ||
            typeof message.deviceUptimeMs !== 'number') {
          this.pendingCommand = null;
          this.clearRequestTimeout();
          this.state.busy = false;
          this.state.status = 'Stav zařízení se změnil; povel nebyl odeslán.';
          this.publish();
          if (typeof message.revision === 'number') {
            this.refreshRevision(message.revision);
          }
          return;
        }
        this.roundTripMs = performance.now() - pending.sentAt;
        this.lastDeviceSampleAt = performance.now();
        this.lastDeviceUptimeMs = message.deviceUptimeMs;
        this.clearRequestTimeout();
        this.sendPendingCommand(this.device);
        return;
      }
      const sentAt = this.requests.get(String(message.requestMessageId));
      if (sentAt !== undefined && message.bootId === this.device.bootId &&
          message.controlLeaseId === this.device.controlLeaseId &&
          typeof message.deviceUptimeMs === 'number') {
        this.roundTripMs = performance.now() - sentAt;
        this.lastDeviceSampleAt = performance.now();
        this.lastDeviceUptimeMs = message.deviceUptimeMs;
        this.requests.delete(String(message.requestMessageId));
        this.publish();
        if (message.revision !== this.device.revision &&
            typeof message.revision === 'number') {
          this.refreshRevision(message.revision);
        }
      }
    }
  }

  private applySnapshot(snapshot: ProtocolSnapshot): void {
    if (this.device?.bootId === snapshot.bootId && snapshot.eventSequence < this.device.eventSequence) {
      return;
    }
    const previousMask = this.device?.readyMask ?? snapshot.readyMask;
    const previousLeaseId = this.device?.controlLeaseId;
    if (this.device && snapshot.bootId !== this.device.bootId) {
      this.lastDeviceUptimeMs = 0;
      this.roundTripMs = Number.POSITIVE_INFINITY;
      this.pendingCommand = null;
      this.clearCommand();
      this.sequence = 0;
    }
    this.device = snapshot;
    if (this.awaitingRevision !== null &&
        snapshot.revision >= this.awaitingRevision) {
      this.awaitingRevision = null;
      this.clearRequestTimeout();
    }
    if (this.unresolvedCommand?.kind === 'select' &&
        snapshot.lastResult?.commandId === this.unresolvedCommand.id &&
        snapshot.lastResult.status === 'completed') {
      this.unresolvedCommand = null;
    } else if (this.unresolvedCommand?.kind === 'reload' &&
        snapshot.revision > this.unresolvedCommand.revision &&
        (snapshot.readyMask & (1 << (this.unresolvedCommand.compartment - 1))) !== 0) {
      this.unresolvedCommand = null;
    }
    if (snapshot.controlLeaseId && snapshot.controlLeaseId !== previousLeaseId) {
      this.sequence = 0;
      this.clearCommand();
    }
    if (snapshot.controlMode === 'manual') {
      this.acquirePending = false;
    } else {
      this.releasePending = false;
      this.clearCommand();
    }
    if (this.activeCommand?.kind === 'reload' &&
        (snapshot.readyMask & (1 << (this.activeCommand.compartment - 1))) !== 0) {
      this.clearCommand();
    }
    if (this.activeCommand?.kind === 'select' &&
        snapshot.lastResult?.commandId === this.activeCommand.id) {
      this.clearCommand();
    }
    const stopped = this.unresolvedCommand !== null ||
      snapshot.controlMode === 'recovery' ||
      snapshot.phase === 'stopped' || snapshot.phase === 'fault' ||
      snapshot.phase === 'bootSafe' || snapshot.phase === 'recoveryRequired' ||
      snapshot.faults.length > 0;
    this.state = {
      ...this.state,
      controlMode: snapshot.controlMode === 'manual' && !this.releasePending
        ? 'manual' : 'observer',
      phase: stopped ? 'stopped' : snapshot.phase !== 'ready' ||
        snapshot.activeCommandId ? 'running' : 'ready',
      readyMask: snapshot.readyMask,
      revision: snapshot.revision,
      activeCompartment: snapshot.activeCommandId ? this.state.activeCompartment : null,
      busy: this.pendingCommand !== null || this.activeCommand !== null ||
        this.acquirePending || this.releasePending || this.awaitingRevision !== null,
      status: stopped
        ? this.unresolvedCommand !== null
          ? 'Výsledek předchozího povelu je nejistý. Ovládání vyžaduje servisní kontrolu.'
          : 'Simulátor vyžaduje servisní kontrolu. Obnovení se provádí mimo mobilní UI.'
        : snapshot.activeCommandId
          ? 'Na simulátoru běží výběr; čekám na výsledek.'
          : snapshot.readyMask < previousMask
            ? 'Simulovaný výběr skončil. Dotčená přihrádka čeká na kontrolu a založení.'
            : snapshot.readyMask > previousMask
              ? 'Založení bylo potvrzeno v simulátoru.'
          : snapshot.controlMode === 'manual'
            ? 'Ruční lease na simulátoru je aktivní.'
            : 'Připojeno k hostitelskému E2 simulátoru v režimu pozorování.',
    };
    this.voice.update(snapshot);
    this.conversation.update(snapshot);
    this.publish();
  }

  private sendPendingCommand(snapshot: ProtocolSnapshot): void {
    const pending = this.pendingCommand;
    this.pendingCommand = null;
    const now = performance.now();
    if (!pending || snapshot.controlLeaseId !== pending.leaseId ||
        snapshot.revision !== pending.revision ||
        snapshot.readyMask !== pending.readyMask ||
        snapshot.phase !== 'ready' ||
        snapshot.activeCommandId !== null ||
        now - pending.sentAt > 500 || this.roundTripMs > 500) {
      this.state.busy = false;
      this.state.status = 'Stav se změnil nebo odpověď přišla pozdě; povel nebyl odeslán.';
      this.publish();
      return;
    }
    const commandId = crypto.randomUUID();
    const command = {
      type: pending.kind === 'select' ? 'selection.start' : 'inventory.confirmReload',
      commandId,
      deviceId: snapshot.deviceId,
      bootId: snapshot.bootId,
      controlLeaseId: snapshot.controlLeaseId,
      sequence: ++this.sequence,
      expectedRevision: snapshot.revision,
      issuedAtDeviceMs: Math.floor(this.lastDeviceUptimeMs + now - this.lastDeviceSampleAt),
      maxAgeMs: 2000,
      payload: pending.kind === 'select'
        ? { compartment: pending.compartment, selectionSource: 'manual' }
        : { compartment: pending.compartment, inspectionConfirmed: true },
    };
    this.state.busy = true;
    if (pending.kind === 'select') {
      this.state.activeCompartment = pending.compartment;
    }
    this.state.status = `Posílám ${pending.kind === 'select' ? 'výběr' : 'založení'} přihrádky ${pending.compartment}…`;
    this.publish();
    const encoded = JSON.stringify({ protocolVersion: 1, ...command });
    this.activeCommand = {
      id: commandId,
      kind: pending.kind,
      compartment: pending.compartment,
      revision: snapshot.revision,
      encoded,
      sentAt: now,
      retries: 0,
    };
    if (!this.sendEncoded(encoded)) {
      this.clearCommand();
      this.state.busy = false;
      this.state.status = 'Povel nebyl odeslán; spojení se přerušilo.';
      this.publish();
      return;
    }
    this.armCommandRetry();
  }

  private isSnapshot(value: unknown): value is ProtocolSnapshot {
    if (typeof value !== 'object' || value === null) {
      return false;
    }
    const snapshot = value as Partial<ProtocolSnapshot>;
    return snapshot.type === 'state.snapshot' &&
      typeof snapshot.deviceId === 'string' &&
      typeof snapshot.bootId === 'string' &&
      Number.isSafeInteger(snapshot.deviceUptimeMs) &&
      Number.isSafeInteger(snapshot.eventSequence) &&
      Number.isSafeInteger(snapshot.revision) &&
      Number.isInteger(snapshot.readyMask) && snapshot.readyMask! >= 0 &&
      snapshot.readyMask! <= 63 &&
      ['standalone', 'manual', 'recovery'].includes(snapshot.controlMode || '') &&
      (snapshot.controlLeaseId === null || typeof snapshot.controlLeaseId === 'string') &&
      Array.isArray(snapshot.faults) &&
      (snapshot.lastResult === null || typeof snapshot.lastResult === 'object');
  }

  private sendRequest(type: string, fields: Record<string, unknown>): boolean {
    const messageId = crypto.randomUUID();
    const sent = this.send({ type, messageId, ...fields });
    if (sent && type === 'heartbeat') {
      this.requests.set(messageId, performance.now());
      if (this.requests.size > 4) {
        this.requests.delete(this.requests.keys().next().value!);
      }
    }
    return sent;
  }

  private refreshRevision(revision: number): void {
    if (!this.device || this.awaitingRevision !== null ||
        revision <= this.device.revision) {
      return;
    }
    this.awaitingRevision = revision;
    this.state.busy = true;
    this.state.status = 'Stav zařízení se změnil; načítám nový snapshot.';
    this.publish();
    if (!this.sendRequest('state.get', {
      deviceId: this.device.deviceId,
      bootId: this.device.bootId,
    })) {
      this.socket?.close();
      return;
    }
    this.armRequestTimeout('Nový snapshot nepřišel; spojení obnovuji.');
  }

  private send(message: Record<string, unknown>): boolean {
    return this.sendEncoded(JSON.stringify({ protocolVersion: 1, ...message }));
  }

  private sendEncoded(encoded: string): boolean {
    if (this.socket?.readyState !== 1) {
      return false;
    }
    try {
      this.socket.send(encoded);
      return true;
    } catch {
      this.socket.close();
      return false;
    }
  }

  private armRequestTimeout(status: string, timeoutMs = 3000): void {
    this.clearRequestTimeout();
    this.requestTimer = window.setTimeout(() => {
      this.pendingCommand = null;
      this.acquirePending = false;
      this.state.busy = false;
      this.state.status = status;
      this.publish();
      this.socket?.close();
    }, timeoutMs);
  }

  private clearRequestTimeout(): void {
    if (this.requestTimer !== null) {
      window.clearTimeout(this.requestTimer);
      this.requestTimer = null;
    }
  }

  private armCommandRetry(): void {
    this.clearCommandTimer();
    this.commandTimer = window.setTimeout(() => {
      const command = this.activeCommand;
      if (!command) {
        return;
      }
      if (command.retries >= 2 || performance.now() - command.sentAt >= 1700) {
        this.socket?.close();
        return;
      }
      command.retries += 1;
      if (!this.device?.controlLeaseId || this.state.phase === 'stopped' ||
          !this.sendEncoded(command.encoded)) {
        this.socket?.close();
        return;
      }
      this.armCommandRetry();
    }, this.activeCommand?.retries === 2 ? 700 : 500);
  }

  private clearCommandTimer(): void {
    if (this.commandTimer !== null) {
      window.clearTimeout(this.commandTimer);
      this.commandTimer = null;
    }
  }

  private clearCommand(): void {
    this.clearCommandTimer();
    this.clearResultTimer();
    this.activeCommand = null;
  }

  private rememberUnresolvedCommand(): void {
    if (this.activeCommand !== null) {
      this.unresolvedCommand = {
        id: this.activeCommand.id,
        kind: this.activeCommand.kind,
        compartment: this.activeCommand.compartment,
        revision: this.activeCommand.revision,
      };
    }
  }

  private armResultTimeout(): void {
    this.clearResultTimer();
    this.resultTimer = window.setTimeout(() => {
      if (!this.activeCommand || !this.device) {
        return;
      }
      this.state.status = 'Výsledek chybí; načítám aktuální stav simulátoru.';
      this.publish();
      if (!this.sendRequest('state.get', {
        deviceId: this.device.deviceId,
        bootId: this.device.bootId,
      })) {
        this.socket?.close();
        return;
      }
      this.resultTimer = window.setTimeout(() => {
        if (this.activeCommand) {
          this.socket?.close();
        }
      }, 3000);
    }, 6000);
  }

  private clearResultTimer(): void {
    if (this.resultTimer !== null) {
      window.clearTimeout(this.resultTimer);
      this.resultTimer = null;
    }
  }

  private clearTimers(): void {
    this.clearRequestTimeout();
    this.clearCommandTimer();
    this.clearResultTimer();
    if (this.staleTimer !== null) {
      window.clearTimeout(this.staleTimer);
      this.staleTimer = null;
    }
  }

  private publish(): void {
    this.onChange(this.viewSnapshot());
  }

  private viewSnapshot(): DeviceSnapshot {
    const snapshot = {
      ...this.state,
      diagnostics: this.device === null ? undefined : {
        bootId: this.device.bootId,
        devicePhase: this.device.phase,
        eventSequence: this.device.eventSequence,
        deviceUptimeMs: Math.max(this.device.deviceUptimeMs, this.lastDeviceUptimeMs),
        receivedAtMs: this.lastMessageAt,
        roundTripMs: Number.isFinite(this.roundTripMs) ? Math.round(this.roundTripMs) : null,
        faultCount: this.device.faults.length,
      },
      voice: this.voice.state(),
      conversation: this.conversation.snapshot(
        this.state.controlMode === 'manual' && this.state.phase === 'ready' && !this.state.busy,
      ),
    };
    if (this.options.source === 'native-device') {
      // Shared command behavior, with wording that does not claim simulation.
      snapshot.status = snapshot.status
        .replaceAll('hostitelský simulátor', 'kufr')
        .replaceAll('hostitelskému E2 simulátoru', 'kufru')
        .replaceAll('simulátorem', 'kufrem')
        .replaceAll('simulátoru', 'kufru')
        .replaceAll('simulátor', 'kufr')
        .replaceAll('Simulátor', 'Kufr')
        .replaceAll('Simulovaná sekvence', 'Povelová sekvence')
        .replaceAll('Simulovaný výběr', 'Povelový výběr');
    }
    return snapshot;
  }
}

// The development proxy owns bearer credentials and verified upstream TLS.
export class SimulatorDeviceConnection extends ProtocolDeviceConnection {
  constructor(onChange: (snapshot: DeviceSnapshot) => void) {
    super(onChange, {
      source: 'e2-simulator',
      deviceId: 'kufr-001',
      createSocket: () => new WebSocket(`ws://${window.location.host}/dev/control`),
    });
  }
}
