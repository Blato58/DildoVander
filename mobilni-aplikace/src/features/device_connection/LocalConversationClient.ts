export interface ConversationDeviceState {
  deviceId: string;
  bootId: string;
  revision: number;
  eventSequence: number;
  controlLeaseId: string | null;
  capabilities?: string[];
  conversation?: { conversationId: string; generation: number; activeTurnId: string | null };
}

export interface LocalConversationState {
  canSubmit: boolean;
  canCancel: boolean;
  pending: boolean;
  reply: string | null;
  simulated: boolean | null;
  status: string;
}

type PendingTurn = {
  requestId: string;
  clientTurnId: string;
  accepted: boolean;
};

// Text is sent only to the device owner. No provider calls, persistent history
// or deferred queue live in the phone; every result belongs to one live lease.
export class LocalConversationClient {
  private device: ConversationDeviceState | null = null;
  private turn: PendingTurn | null = null;
  private completedTurnId: string | null = null;
  private cancelId: string | null = null;
  private blocked = false;
  private watermark = -1;
  private timer: number | null = null;
  private reply: string | null = null;
  private simulated: boolean | null = null;
  private status = 'Text vyžaduje připojení a ruční řízení.';

  constructor(
    private readonly send: (message: Record<string, unknown>) => boolean,
    private readonly changed: () => void,
    private readonly uncertain: () => void,
  ) {}

  snapshot(eligible = true): LocalConversationState {
    const manual = this.device?.controlLeaseId != null && this.device.conversation != null;
    const remotePending = this.device?.conversation?.activeTurnId != null &&
      this.device.conversation.activeTurnId !== this.completedTurnId;
    return {
      canSubmit: manual && eligible && !this.blocked && this.turn === null &&
        this.cancelId === null && this.device!.conversation!.activeTurnId === null &&
        this.device!.capabilities?.includes('conversationText') === true,
      canCancel: manual && !this.blocked && this.cancelId === null &&
        (this.turn !== null || remotePending),
      pending: this.turn !== null || this.cancelId !== null ||
        remotePending,
      reply: this.reply,
      simulated: this.simulated,
      status: this.status,
    };
  }

  update(device: ConversationDeviceState): void {
    const previous = this.device;
    const contextChanged = previous !== null && (
      previous.bootId !== device.bootId || previous.controlLeaseId !== device.controlLeaseId ||
      previous.conversation?.conversationId !== device.conversation?.conversationId ||
      previous.conversation?.generation !== device.conversation?.generation ||
      previous.revision !== device.revision
    );
    if (contextChanged) {
      this.invalidate('Stav nebo řízení kufru se změnilo; předchozí tah se nepřehrává.');
    }
    this.device = device;
    if (device.conversation?.activeTurnId !== this.completedTurnId) {
      this.completedTurnId = null;
    }
    this.watermark = Math.max(this.watermark, device.eventSequence);
    if (this.turn?.accepted && device.conversation?.activeTurnId !== this.turn.clientTurnId) {
      this.turn = null;
      this.clearTimer();
      this.status = 'Tah už není aktivní; odpověď nebyla doručena. Text se automaticky neopakuje.';
    }
    if (!device.controlLeaseId && this.turn === null) {
      this.status = 'Pro text nejprve výslovně převezměte ruční řízení.';
    } else if ((previous === null || contextChanged) && this.turn === null) {
      this.status = 'Text jde přes kufr do jeho rozhovoru. Telefon nevolá AI službu.';
    }
  }

  submit(text: string): boolean {
    const state = this.snapshot();
    if (!state.canSubmit || !this.device?.conversation) {
      return false;
    }
    // Count Unicode scalars, reject lone surrogates and obey the raw envelope
    // limit too: escaped Unicode text uses the v1 8192-byte exception.
    const characters = Array.from(text);
    if (text.trim().length === 0 || characters.length > 1000 || characters.some((character) =>
      character.length === 1 && character.charCodeAt(0) >= 0xd800 && character.charCodeAt(0) <= 0xdfff)) {
      this.status = 'Zadejte neprázdný text do 1000 znaků.';
      this.changed();
      return false;
    }
    const requestId = crypto.randomUUID();
    const clientTurnId = crypto.randomUUID();
    const message = {
      type: 'conversation.text', messageId: requestId,
      ...this.context(), clientTurnId, expectedRevision: this.device.revision, text,
    };
    if (new TextEncoder().encode(JSON.stringify({ protocolVersion: 1, ...message })).length > 8192) {
      this.status = 'Text je příliš dlouhý pro místní zprávu. Zkraťte jej.';
      this.changed();
      return false;
    }
    this.reply = null;
    this.simulated = null;
    this.turn = { requestId, clientTurnId, accepted: false };
    this.status = 'Odesílám text kufru; čekám na přijetí.';
    this.armTimeout(3000);
    if (!this.send(message)) {
      this.invalidate('Text nebyl odeslán. Obnovte spojení; nevzniká fronta textů.');
      this.changed();
      return false;
    }
    this.changed();
    return true;
  }

  cancel(): boolean {
    if (!this.snapshot().canCancel) {
      return false;
    }
    const messageId = crypto.randomUUID();
    // Invalidate before sending: even a queued result from this generation
    // must not flash on screen while cancellation awaits its ACK/snapshot.
    this.turn = null;
    this.reply = null;
    this.simulated = null;
    this.cancelId = messageId;
    this.status = 'Ruším tah na kufru; čekám na nový stav.';
    this.armTimeout(3000);
    if (!this.send({ type: 'conversation.cancel', messageId, ...this.context() })) {
      this.invalidate('Zrušení nebylo potvrzeno. Obnovte spojení.');
      this.uncertain();
      return false;
    }
    this.changed();
    return true;
  }

  receive(message: Record<string, unknown>): void {
    const device = this.device;
    const conversation = device?.conversation;
    if (!device || !conversation || !device.controlLeaseId ||
        message.bootId !== device.bootId || message.conversationId !== conversation.conversationId ||
        !Number.isSafeInteger(message.eventSequence) || Number(message.eventSequence) <= this.watermark) {
      return;
    }
    if (message.type === 'conversation.ack' && message.requestMessageId === this.cancelId) {
      const accepted = message.status === 'accepted' && message.clientTurnId === null &&
        Number(message.generation) === conversation.generation + 1;
      if (!accepted && message.generation !== conversation.generation) { return; }
      this.watermark = Number(message.eventSequence);
      this.cancelId = null;
      this.clearTimer();
      this.blocked = true;
      this.status = accepted ? 'Zrušení přijato. Čekám na potvrzení nové generace kufru.'
        : `Kufr zrušení odmítl: ${String(message.errorCode)}. Obnovte spojení.`;
      this.changed();
      // A new authoritative snapshot clears blocked via the changed generation.
      // Rejected cancellation has an uncertain server turn; do not resubmit.
      if (!accepted) { this.uncertain(); }
      return;
    }
    const turn = this.turn;
    if (!turn || message.clientTurnId !== turn.clientTurnId ||
        message.generation !== conversation.generation || this.cancelId !== null) {
      return;
    }
    if (message.type === 'conversation.ack' && message.requestMessageId === turn.requestId) {
      this.watermark = Number(message.eventSequence);
      if (message.status === 'accepted') {
        // Duplicate ACKs must not extend the 45-second result deadline.
        if (!turn.accepted) { this.armTimeout(45000); }
        turn.accepted = true;
        this.status = 'Kufr text přijal. Čekám na odpověď; můžete tah zrušit.';
      } else {
        this.clearTimer();
        this.turn = null;
        this.status = `Kufr text odmítl: ${String(message.errorCode)}.`;
      }
      this.changed();
    } else if (message.type === 'conversation.result' && turn.accepted &&
        typeof message.reply === 'string' && Array.from(message.reply).length <= 500 &&
        typeof message.simulated === 'boolean') {
      this.watermark = Number(message.eventSequence);
      this.reply = message.reply;
      this.simulated = message.simulated;
      this.completedTurnId = turn.clientTurnId;
      this.turn = null;
      this.clearTimer();
      this.status = message.simulated ? 'Odpověď simulátoru. Žádná skutečná AI ani pohyb.'
        : 'Odpověď z kufru. Text sám není povelem pro přihrádku.';
      this.changed();
    }
  }

  invalidate(status = 'Spojení nebo řízení skončilo; text se automaticky neopakuje.'): void {
    this.device = null;
    this.turn = null;
    this.completedTurnId = null;
    this.cancelId = null;
    this.blocked = false;
    this.watermark = -1;
    this.reply = null;
    this.simulated = null;
    this.status = status;
    this.clearTimer();
  }

  private context(): Record<string, unknown> {
    return {
      deviceId: this.device!.deviceId, bootId: this.device!.bootId,
      controlLeaseId: this.device!.controlLeaseId,
      conversationId: this.device!.conversation!.conversationId,
      generation: this.device!.conversation!.generation,
    };
  }

  private armTimeout(milliseconds: number): void {
    this.clearTimer();
    this.timer = window.setTimeout(() => {
      this.invalidate('Odpověď chybí. Spojení obnovuji bez opakování textu.');
      this.changed();
      this.uncertain();
    }, milliseconds);
  }

  private clearTimer(): void {
    if (this.timer !== null) {
      window.clearTimeout(this.timer);
      this.timer = null;
    }
  }
}
