import { LocalVoiceClient, type VoiceSettings } from './LocalVoiceClient';
import { LocalDanceClient, danceActive, type DanceSettings } from './LocalDanceClient';
import type { DeviceSnapshot } from './FakeDeviceConnection';
import { LocalConversationClient, type ConversationDeviceState } from './LocalConversationClient';

interface ProtocolSnapshot extends ConversationDeviceState {
  type: 'state.snapshot';
  operationEpoch?: string;
  voice?: VoiceSettings;
  dance?: DanceSettings;
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
  operationEpoch?: string;
  sentAt: number;
};

type ActiveCommand = {
  id: string;
  kind: PendingCommand['kind'];
  compartment: number;
  revision: number;
  encoded: string;
  sequence: number;
  sentAt: number;
  retries: number;
};

export interface ControlSocket {
  readonly readyState: number;
  addEventListener(type: string, listener: (event: { data?: unknown; failureCode?: string }) => void): void;
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

export type OfflineCommandKind = 'offline_preflight' | 'offline_arm' | 'offline_start' | 'quiet'
  | 'offline_ambient' | 'offline_outbox' | 'offline_sync_ack';
export interface OfflineCommandOptions { enabled?: boolean; shortVariant?: boolean; operationEpoch?: string }
export interface OfflineShowStatus {
  state: 'ready' | 'audio_only' | 'missing_assets' | 'incompatible_catalog' | 'calibration_required' | 'recovery_required';
  armed: boolean; quiet: boolean; generation: number; readyMask: number; outboxCount: number;
  catalogVersion: string; bankVersion: string; audioBytes: number; audioCapacityBytes: number;
  streamWorkingBytes: number; missing: string[]; ambientEnabled: boolean; bankVoice: string; voiceCompatible: boolean;
}
export interface OfflineOutbox { type: 'offline.outbox'; offline: OfflineShowStatus; records: unknown[] }

// Keep uint64 epochs as canonical decimals: JavaScript numbers cannot preserve them.
function validOperationEpoch(value: unknown): value is string {
  return typeof value === 'string' && /^(0|[1-9][0-9]{0,19})$/.test(value) &&
    (value.length < 20 || value <= '18446744073709551615');
}

// One owner of lease, command retry and uncertain-result reconciliation, shared
// by the development proxy and the native pinned-TLS transport.
export class ProtocolDeviceConnection {
  private operationEpochPending = false;
  private offlineStatus: OfflineShowStatus | null = null;
  private offlinePending: { id: string; kind: OfflineCommandKind; bootId: string; resolve: (value: OfflineShowStatus | OfflineOutbox) => void;
    reject: (error: Error) => void; timer: number } | null = null;

  private invalidateOffline(message: string): void {
    this.offlineStatus = null;
    if (!this.offlinePending) return;
    window.clearTimeout(this.offlinePending.timer);
    this.offlinePending.reject(new Error(message));
    this.offlinePending = null;
  }

  requestOffline(kind: OfflineCommandKind, compartment?: number, commandId?: string,
    options: OfflineCommandOptions = {}): Promise<OfflineShowStatus | OfflineOutbox> {
    if (!['offline_preflight', 'offline_arm', 'offline_start', 'quiet', 'offline_ambient', 'offline_outbox', 'offline_sync_ack'].includes(kind)
      || this.options.source !== 'native-device' || !this.device || this.state.phase === 'disconnected'
      || this.offlinePending) return Promise.reject(new Error('Místní autentizovaná cesta není připravená.'));
    if (kind === 'offline_arm' && (!this.device.controlLeaseId || this.state.controlMode !== 'manual')) {
      return Promise.reject(new Error('Arm vyžaduje platné ruční řízení; převezměte je v Provozu.'));
    }
    if ((kind === 'offline_arm' || kind === 'offline_start') && (!this.offlineStatus || this.offlineStatus.state !== 'ready')) {
      return Promise.reject(new Error('Nejprve obnovte místní preflight; kufr musí potvrdit připravenost.'));
    }
    if (kind === 'offline_start' && (!Number.isInteger(compartment) || compartment! < 1 || compartment! > 6
      || !this.offlineStatus?.armed || this.state.phase !== 'ready' || this.state.busy)) {
      return Promise.reject(new Error('Start vyžaduje výslovnou přihrádku 1–6, Arm a klidový stav kufru.'));
    }
    if (kind === 'offline_sync_ack' && !/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(commandId ?? '')) {
      return Promise.reject(new Error('Potvrzení synchronizace vyžaduje přesné ID importovaného výsledku.'));
    }
    if ((kind === 'offline_start' || kind === 'offline_sync_ack') && !this.operationEpochReady()) {
      return Promise.reject(new Error('Nejprve vyčkejte na aktuální metadata kufru.'));
    }
    if (options.operationEpoch !== undefined && (kind !== 'offline_sync_ack' || !validOperationEpoch(options.operationEpoch) ||
        (options.operationEpoch !== '0' && !this.device.capabilities?.includes('operationEpoch')))) {
      return Promise.reject(new Error('Výsledek nemá platnou epochu operace.'));
    }
    const messageId = crypto.randomUUID();
    const message: Record<string, unknown> = { type: kind, messageId,
      deviceId: this.device.deviceId, bootId: this.device.bootId };
    if (kind === 'offline_arm' || kind === 'offline_start') Object.assign(message, {
      generation: this.offlineStatus!.generation, expectedRevision: this.device.revision,
      ...(kind === 'offline_arm' ? { controlLeaseId: this.device.controlLeaseId }
        : { compartment, shortVariant: options.shortVariant === true }),
    });
    if (kind === 'quiet' || kind === 'offline_ambient') {
      if (typeof options.enabled !== 'boolean') return Promise.reject(new Error('Zadejte výslovné zapnutí nebo vypnutí.'));
      message.enabled = options.enabled;
    }
    if (kind === 'offline_start' && this.device.capabilities?.includes('operationEpoch') &&
        this.device.operationEpoch !== undefined) {
      message.operationEpoch = this.device.operationEpoch;
    }
    if (kind === 'offline_sync_ack') {
      message.commandId = commandId;
      // The imported record owns this epoch. Never replace it with current metadata.
      if (this.device.capabilities?.includes('operationEpoch')) message.operationEpoch = options.operationEpoch ?? '0';
    }
    return new Promise((resolve, reject) => {
      const timer = window.setTimeout(() => {
        this.offlinePending = null; this.offlineStatus = null;
        reject(new Error('Výsledek místního povelu není známý. Obnovte preflight; povel se neopakuje.'));
      }, 4000);
      this.offlinePending = { id: messageId, kind, bootId: this.device!.bootId, resolve, reject, timer };
      if (!this.send(message)) {
        window.clearTimeout(timer); this.offlinePending = null;
        reject(new Error('Místní spojení není dostupné. Použijte fyzické zastavení kufru.'));
      }
    });
  }
  private static readonly nativeFailures: Record<string, string> = {
    invalid_identity: 'Uložené párování není platné.',
    certificate_mismatch: 'Kufr má jiný bezpečnostní klíč než uložené párování. Ověřte zařízení a servisní párování.',
    authorization_rejected: 'Kufr odmítl uložené párování. Ověřte servisní párování.',
    server_busy: 'Kufr má již aktivní Wi-Fi spojení.',
    handshake_rejected: 'Kufr odmítl připojení k místnímu ovládání.',
    tls_failed: 'Zabezpečené připojení ke kufru selhalo.',
    host_unreachable: 'Kufr není dostupný v místní Wi-Fi.',
    network_unavailable: 'Místní síť není dostupná. Ověřte Wi-Fi a oprávnění Místní síť.',
    connection_timeout: 'Připojení ke kufru neodpovědělo včas.',
    protocol_error: 'Kufr poslal neplatnou zprávu ovládání.',
  };
  private static readonly terminalNativeFailures = new Set(['invalid_identity', 'certificate_mismatch',
    'authorization_rejected', 'handshake_rejected', 'tls_failed', 'protocol_error']);
  private closeFailure: string | undefined;
  private stableSince: number | null = null;
  private readonly voice = new LocalVoiceClient(
    (message) => this.send(message), () => this.publish(), () => this.socket?.close(),
  );
  private readonly dance = new LocalDanceClient(
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
  private lastObservedAtMs: number | undefined;
  private lastDeviceSampleAt = 0;
  private lastDeviceUptimeMs = 0;
  private roundTripMs = Number.POSITIVE_INFINITY;
  private readonly requests = new Map<string, number>();
  private acquirePending = false;
  private releasePending = false;
  private pendingControl: { id: string; bootId: string; operation: 'control.acquire' | 'control.release' } | null = null;
  private controlRefusal: string | null = null;
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
    return this.options.source === 'e2-simulator' && this.viewSnapshot().conversation?.canSubmit === true && this.conversation.submit(text);
  }

  configureVoice(mode: VoiceSettings['mode'], voice: VoiceSettings['voice']): boolean {
    return !this.dance.state().canStop && this.voice.configure(mode, voice);
  }
  startVoice(imageConsent = false, testContext?: string): boolean {
    if (testContext !== undefined && this.options.source !== 'native-device') return false;
    return !this.dance.state().canStop && this.voice.start(imageConsent, testContext);
  }
  stopVoice(testContextId?: string): boolean {
    if (testContextId !== undefined && this.options.source !== 'native-device') return false;
    return this.voice.stop(testContextId);
  }
  startDance(): boolean { return !this.state.busy && !this.voice.state().pending && this.dance.start(); }
  stopDance(): boolean { return this.dance.stop(); }

  cancelConversation(): boolean {
    return this.options.source === 'e2-simulator' && this.conversation.cancel();
  }

  connect(): void {
    this.reconnectAttempt = 0;
    this.openConnection();
  }

  private openConnection(): void {
    this.suspended = false;
    if (this.retryTimer !== null) {
      window.clearTimeout(this.retryTimer);
      this.retryTimer = null;
    }
    if (this.socket !== null && this.socket.readyState <= 1) {
      return;
    }
    const socket = this.options.createSocket();
    this.closeFailure = undefined;
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
    socket.addEventListener('close', (event) => {
      if (this.socket === socket) {
        this.invalidateOffline('Místní spojení se přerušilo. Výsledek povelu není známý; povel se neopakuje.');
        this.rememberUnresolvedCommand();
        this.socket = null;
        this.device = null;
        this.pendingCommand = null;
        this.activeCommand = null;
        this.acquirePending = false;
        this.releasePending = false;
        this.pendingControl = null;
        this.controlRefusal = null;
        this.awaitingRevision = null;
        this.requests.clear();
        this.voice.invalidate();
        this.dance.invalidate();
        this.conversation.invalidate();
        this.clearTimers();
        this.stableSince = null;
        const native = this.options.source === 'native-device';
        const failure = this.closeFailure ?? event.failureCode;
        const stopped = native && (this.reconnectAttempt >= 3 ||
          (failure !== undefined && ProtocolDeviceConnection.terminalNativeFailures.has(failure)));
        const failureText = native && failure ? ProtocolDeviceConnection.nativeFailures[failure] : undefined;
        this.state = {
          ...this.state,
          controlMode: 'observer',
          phase: 'disconnected',
          activeCompartment: null,
          busy: false,
          status: (failureText ?? 'Spojení se simulátorem bylo přerušeno. Výsledek případného povelu je nejistý.') +
            (failureText && this.unresolvedCommand ? ' Výsledek případného povelu je nejistý.' : '') +
            (stopped ? ' Automatické připojování bylo zastaveno. Po ověření zkuste připojení znovu.' : ''),
        };
        this.publish();
        if (!stopped && !this.suspended && !document.hidden) {
          const delayMs = this.options.source === 'native-device'
            ? Math.min(8000, 500 * 2 ** Math.min(this.reconnectAttempt++, 4)) * (0.8 + Math.random() * 0.4)
            : 1500;
          this.retryTimer = window.setTimeout(() => this.openConnection(), delayMs);
        }
      }
    });
    socket.addEventListener('error', () => {
      if (this.socket !== socket) { return; }
      socket.close();
    });
  }

  acquire(): boolean {
    if (this.dance.state().canStop) return false;
    if (!this.device || this.state.controlMode !== 'observer' ||
        this.state.phase !== 'ready' || this.state.busy) {
      return false;
    }
    this.acquirePending = true;
    this.controlRefusal = null;
    const messageId = crypto.randomUUID();
    this.pendingControl = { id: messageId, bootId: this.device.bootId, operation: 'control.acquire' };
    this.state.busy = true;
    this.state.status = 'Žádám simulátor o ruční lease…';
    this.publish();
    const sent = this.send({
      type: 'control.acquire',
      messageId,
      deviceId: this.device.deviceId,
      bootId: this.device.bootId,
      ...(this.device.capabilities?.includes('controlAck') ? { acknowledge: true } : {}),
    });
    if (!sent) {
      this.acquirePending = false;
      this.pendingControl = null;
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
    const messageId = crypto.randomUUID();
    this.controlRefusal = null;
    this.pendingControl = { id: messageId, bootId: this.device.bootId, operation: 'control.release' };
    const sent = this.send({
      type: 'control.release',
      messageId,
      deviceId: this.device.deviceId,
      bootId: this.device.bootId,
      controlLeaseId: this.device.controlLeaseId,
      ...(this.device.capabilities?.includes('controlAck') ? { acknowledge: true } : {}),
    });
    if (!sent) {
      this.pendingControl = null;
      return;
    }
    this.releasePending = true;
    this.state.controlMode = 'observer';
    this.state.busy = true;
    this.state.status = 'Uvolňuji ruční řízení simulátoru…';
    this.publish();
  }

  suspend(): void {
    this.invalidateOffline('Místní cesta byla přerušena. Výsledek není známý a povel se neopakuje.');
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
    this.invalidateOffline('Stop zneplatnil čekající místní povel. Obnovte preflight.');
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

  private operationEpochReady(): boolean {
    return !!this.device && (!this.device.capabilities?.includes('operationEpoch') ||
      (!this.operationEpochPending && this.device.operationEpoch !== undefined));
  }

  private prepareCommand(kind: PendingCommand['kind'], compartment: number): boolean {
    if (!this.device?.controlLeaseId || !this.operationEpochReady() || this.pendingCommand !== null ||
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
      operationEpoch: this.device.capabilities?.includes('operationEpoch') ? this.device.operationEpoch : undefined,
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
      this.closeFailure = 'protocol_error';
      this.socket?.close();
      return;
    }
    let message: Record<string, unknown>;
    try {
      message = this.options.parseFrame
        ? this.options.parseFrame(raw)
        : JSON.parse(raw) as Record<string, unknown>;
    } catch {
      this.closeFailure = 'protocol_error';
      this.socket?.close();
      return;
    }
    if (message === null || Array.isArray(message) || message.protocolVersion !== 1 || typeof message.type !== 'string') {
      this.closeFailure = 'protocol_error';
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
        this.closeFailure = 'protocol_error';
        this.socket?.close();
        return;
      }
      const acquisitionCompleted = this.acquirePending && candidate.controlMode === 'manual';
      if (this.device?.bootId !== candidate.bootId) {
        this.invalidateOffline('Kufr se restartoval. Obnovte místní preflight.');
        this.offlineStatus = null;
      }
      this.applySnapshot(candidate);
      if (candidate.capabilities?.includes('operationEpoch') && candidate.operationEpoch === undefined &&
          !this.operationEpochPending) {
        this.operationEpochPending = true;
        this.sendRequest('state.get', { deviceId: candidate.deviceId, bootId: candidate.bootId });
      }
      if (this.stableSince === null) { this.stableSince = performance.now(); }
      // A single welcome followed by another immediate close is not recovery.
      if (performance.now() - this.stableSince >= 5000) { this.reconnectAttempt = 0; }
      if (message.type === 'welcome' || acquisitionCompleted) {
        this.clearRequestTimeout();
      }
    } else if (message.type === 'control.ack') {
      const pending = this.pendingControl;
      if (!pending || pending.id !== message.requestMessageId || pending.bootId !== message.bootId ||
          pending.operation !== message.operation || this.device?.bootId !== pending.bootId ||
          !Number.isSafeInteger(message.eventSequence) || Number(message.eventSequence) < this.device.eventSequence ||
          !Number.isSafeInteger(message.revision) || Number(message.revision) < this.device.revision) return;
      if (message.accepted === true) return; // Authority still comes from the next snapshot.
      const statuses: Record<string, string> = {
        busy: 'Kufr je zaneprázdněný. Stav a Stop zůstávají dostupné; zkuste povel po dokončení.',
        controller_busy: 'Ruční řízení už je obsazené. Obnovte stav kufru.',
        recovery_required: 'Kufr vyžaduje servisní kontrolu před dalším řízením.',
      };
      if (message.accepted !== false || typeof message.errorCode !== 'string' || !Object.hasOwn(statuses, message.errorCode)) {
        this.closeFailure = 'protocol_error';
        this.socket?.close();
        return;
      }
      this.pendingControl = null;
      this.acquirePending = false;
      this.releasePending = false;
      this.clearRequestTimeout();
      this.controlRefusal = statuses[message.errorCode];
      this.state.controlMode = this.device.controlMode === 'manual' ? 'manual' : 'observer';
      this.state.busy = this.pendingCommand !== null || this.activeCommand !== null || this.awaitingRevision !== null;
      if (this.state.phase !== 'stopped') this.state.status = this.controlRefusal;
      this.publish();
    } else if (message.type === 'offline.ack' || message.type === 'offline.outbox') {
      const pending = this.offlinePending;
      if (!pending || pending.id !== message.requestMessageId || pending.bootId !== message.bootId
        || message.type !== (pending.kind === 'offline_outbox' ? 'offline.outbox' : 'offline.ack')) return;
      const offline = message.offline as OfflineShowStatus | undefined;
      if (typeof message.accepted !== 'boolean' || !offline || !Number.isSafeInteger(offline.generation)
        || offline.generation < 0 || typeof offline.armed !== 'boolean' || typeof offline.state !== 'string') return;
      window.clearTimeout(pending.timer); this.offlinePending = null;
      if (message.accepted === false) {
        this.offlineStatus = null;
        pending.reject(new Error(`Kufr odmítl povel: ${String(message.errorCode)}.`));
      } else {
        this.offlineStatus = { ...offline };
        pending.resolve(message.type === 'offline.outbox' ? message as unknown as OfflineOutbox : offline);
      }
    } else if (message.type === 'voice.ack') {
      this.voice.receive(message);
    } else if (message.type === 'dance.ack') {
      this.dance.receive(message);
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
      } else if (message.errorCode === 'wrong_operation_epoch') {
        // Epoch refusals consume no server sequence. New intent reuses that slot.
        this.sequence = active.sequence - 1;
        this.operationEpochPending = true;
        this.sendRequest('state.get', { deviceId: this.device!.deviceId, bootId: this.device!.bootId });
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
        this.lastObservedAtMs = Date.now();
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
        this.lastObservedAtMs = Date.now();
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
    // Wall-clock contact evidence changes only after a validated snapshot or a
    // correlated heartbeat response, never after connect attempts/local actions.
    this.lastObservedAtMs = Date.now();
    const previousMask = this.device?.readyMask ?? snapshot.readyMask;
    const previousLeaseId = this.device?.controlLeaseId;
    if (this.device && (snapshot.phase !== this.device.phase || snapshot.bootId !== this.device.bootId)) {
      this.controlRefusal = null;
    }
    if (this.device && snapshot.bootId !== this.device.bootId) {
      this.lastDeviceUptimeMs = 0;
      this.roundTripMs = Number.POSITIVE_INFINITY;
      this.pendingCommand = null;
      this.clearCommand();
      this.sequence = 0;
      this.pendingControl = null;
      this.acquirePending = false;
      this.releasePending = false;
    }
    if (snapshot.operationEpoch !== undefined) this.operationEpochPending = false;
    if (this.device?.bootId !== snapshot.bootId) this.operationEpochPending = false;
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
      if (this.pendingControl?.operation === 'control.acquire') this.pendingControl = null;
    } else {
      this.releasePending = false;
      if (this.pendingControl?.operation === 'control.release') this.pendingControl = null;
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
        : this.controlRefusal ?? (snapshot.phase === 'dancing'
          ? 'Kufr přehrává skladbu a tančí.'
        : snapshot.activeCommandId
          ? 'Na simulátoru běží výběr; čekám na výsledek.'
          : snapshot.readyMask < previousMask
            ? 'Simulovaný výběr skončil. Dotčená přihrádka čeká na kontrolu a založení.'
            : snapshot.readyMask > previousMask
              ? 'Založení bylo potvrzeno v simulátoru.'
          : snapshot.controlMode === 'manual'
            ? 'Ruční lease na simulátoru je aktivní.'
            : 'Připojeno k hostitelskému E2 simulátoru v režimu pozorování.'),
    };
    this.voice.update(snapshot);
    this.dance.update(snapshot);
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
      ...(pending.operationEpoch === undefined ? {} : { operationEpoch: pending.operationEpoch }),
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
      sequence: command.sequence,
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
    return (snapshot.operationEpoch === undefined || validOperationEpoch(snapshot.operationEpoch)) &&
      snapshot.type === 'state.snapshot' &&
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
    const sent = this.send({ type, messageId, ...fields,
      ...(type === 'state.get' && this.device?.capabilities?.includes('operationEpoch')
        ? { includeOperationEpoch: true } : {}),
    });
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
    const dance = this.dance.state();
    const voice = this.voice.state();
    const snapshot = {
      ...this.state,
      capabilities: this.device?.capabilities ? [...this.device.capabilities] : [],
      busy: this.state.busy || dance.pending,
      phase: this.state.phase === 'ready' && danceActive(dance.phase) ? 'running' as const : this.state.phase,
      diagnostics: this.device === null ? undefined : {
        deviceId: this.device.deviceId,
        lastObservedAtMs: this.lastObservedAtMs,
        bootId: this.device.bootId,
        devicePhase: this.device.phase,
        eventSequence: this.device.eventSequence,
        deviceUptimeMs: Math.max(this.device.deviceUptimeMs, this.lastDeviceUptimeMs),
        receivedAtMs: this.lastMessageAt,
        roundTripMs: Number.isFinite(this.roundTripMs) ? Math.round(this.roundTripMs) : null,
        faultCount: this.device.faults.length,
      },
      voice: { ...voice, canStart: voice.canStart && !dance.canStop,
        canConfigure: voice.canConfigure && !dance.canStop },
      dance: { ...dance, canStart: dance.canStart && !voice.pending && !this.state.busy },
      conversation: this.conversation.snapshot(
        this.options.source === 'e2-simulator' && this.state.controlMode === 'manual' && this.state.phase === 'ready' && !this.state.busy,
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
