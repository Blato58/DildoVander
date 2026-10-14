import QRCode from 'qrcode';
import type { DeviceSnapshot } from '../device_connection/FakeDeviceConnection';

export interface KeepsakeReceipt {
  outcomeId: string;
  sessionEpoch: number;
  physicalOperationId: string;
  url: string;
  expiresAtEpochMs: number;
}

export function validKeepsake(snapshot: DeviceSnapshot, now = Date.now()): KeepsakeReceipt | null {
  const receipt = snapshot.keepsake;
  if (snapshot.source !== 'native-device' || snapshot.phase !== 'ready' || snapshot.busy || !receipt
    || receipt.sessionEpoch !== snapshot.conversationGeneration || receipt.expiresAtEpochMs <= now) { return null; }
  const uuid = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
  if (!uuid.test(receipt.outcomeId) || !uuid.test(receipt.physicalOperationId)) { return null; }
  try {
    const url = new URL(receipt.url);
    if (url.protocol !== 'https:' || url.username || url.password || url.pathname !== '/keepsake/'
      || url.search || !/^#c=[A-Za-z0-9_-]{32,256}$/.test(url.hash)) { return null; }
  } catch { return null; }
  return receipt;
}

// An observer can hand the exact completed outcome to its visitor. No WAN
// credential, claim exchange or generation request is made by this panel.
export class KeepsakePanel {
  readonly element = document.createElement('section');
  private epoch = 0;
  private binding = '';
  private expiryTimer: number | undefined;

  constructor() {
    this.element.className = 'aw-card keepsake-qr-panel';
    this.element.hidden = true;
    this.element.setAttribute('aria-label', 'Soukromá památka vybraného hosta');
    this.element.innerHTML = '<h2>Hůlka má vlastní archivní list</h2><p>Návštěvník může naskenovat soukromý QR kód. Příběh a obraz se připravují samostatně.</p><canvas aria-label="Soukromý QR kód k právě dokončenému výběru"></canvas><p class="keepsake-qr-state" role="status"></p><p>Dokončení prezentace je potvrzené časováním; fyzické předání není měřené. QR je klíč pro tohoto návštěvníka.</p>';
  }

  update(snapshot: DeviceSnapshot): void {
    const receipt = validKeepsake(snapshot);
    const binding = receipt ? `${snapshot.diagnostics?.bootId}:${receipt.outcomeId}:${receipt.sessionEpoch}:${receipt.physicalOperationId}:${receipt.url}` : '';
    if (binding === this.binding) { return; }
    this.binding = binding;
    const epoch = ++this.epoch;
    window.clearTimeout(this.expiryTimer);
    this.element.hidden = true;
    const canvas = this.element.querySelector('canvas')!;
    canvas.getContext('2d')?.clearRect(0, 0, canvas.width, canvas.height);
    if (!receipt) { return; }
    // Render into a detached canvas: a stale asynchronous callback cannot
    // repaint the next visitor's visible panel.
    const draft = document.createElement('canvas');
    QRCode.toCanvas(draft, receipt.url, { width: 256, margin: 4, errorCorrectionLevel: 'M' })
      .then(() => {
        if (epoch !== this.epoch || receipt.expiresAtEpochMs <= Date.now()) { return; }
        canvas.width = draft.width; canvas.height = draft.height;
        canvas.getContext('2d')?.drawImage(draft, 0, 0);
        this.element.querySelector('.keepsake-qr-state')!.textContent = 'Soukromý QR k právě dokončenému výběru.';
        this.element.hidden = false;
        this.expiryTimer = window.setTimeout(() => {
          if (epoch !== this.epoch) { return; }
          this.binding = ''; ++this.epoch; this.element.hidden = true;
          canvas.getContext('2d')?.clearRect(0, 0, canvas.width, canvas.height);
        }, Math.min(2_147_483_647, receipt.expiresAtEpochMs - Date.now()));
      }).catch(() => {
        if (epoch === this.epoch) { this.binding = ''; this.element.hidden = true; }
      });
  }
}
