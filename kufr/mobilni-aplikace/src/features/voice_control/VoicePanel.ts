import type { DeviceSnapshot } from '../device_connection/FakeDeviceConnection';
import type { NativeDeviceConnection } from '../device_connection/NativeDeviceConnection';

export class VoicePanel {
  readonly element = document.createElement('section');
  private readonly form: HTMLFormElement;
  private readonly mode: HTMLSelectElement;
  private readonly voice: HTMLSelectElement;
  private draftChanged = false;
  constructor(connection: () => NativeDeviceConnection | null) {
    this.element.className = 'conversation-panel';
    this.element.setAttribute('aria-label', 'Hlasové ovládání kufru');
    this.element.innerHTML = `
      <h2>Hlas kufru</h2>
      <p>Kufr poslouchá svým mikrofonem a odpovídá reproduktorem. Hlasové režimy používají AI a čerpají OpenAI účet.
         Spuštěním souhlasíte se zpracováním audia; přepis uchovává paměť večera. Nastavení platí do restartu kufru.</p>
      <form>
        <label>Hlasový režim<select name="mode"><option value="off">Vypnutý</option><option value="realtime">Realtime · průběžný rozhovor</option><option value="gpt-live">GPT-Live + Sol · test kvality</option></select></label>
        <label>Hlas<select name="voice"><option value="marin">Marin</option><option value="cedar">Cedar</option><option value="coral">Coral</option></select></label>
        <button type="submit" disabled>Použít nastavení hlasu</button>
      </form>
      <label><input type="checkbox" data-image-consent> Host souhlasí s jednou fotkou kostýmu z kamery kufru pro tento rozhovor.</label>
      <p class="footnote">Volitelné. Fotka se předá serveru a OpenAI jako nejistá nápověda a neukládá se do paměti večera.</p>
      <div class="actions">
        <button type="button" class="primary" data-voice="start" disabled>Spustit hlas na kufru</button>
        <button type="button" data-voice="stop" disabled>Zastavit hlas</button>
      </div>
      <p role="status"></p>
      <p class="footnote">Pro samostatný hlas uvolněte ruční řízení. Během odpovědi kufr neposlouchá;
         po dohrání se poslech obnoví. Stop kufru zastaví také hlas.</p>`;
    this.form = this.element.querySelector<HTMLFormElement>('form')!;
    this.mode = this.form.querySelector<HTMLSelectElement>('[name="mode"]')!;
    this.voice = this.form.querySelector<HTMLSelectElement>('[name="voice"]')!;
    this.form.addEventListener('change', () => { this.draftChanged = true; });
    this.form.addEventListener('submit', (event) => {
      event.preventDefault();
      if (this.form.querySelector<HTMLButtonElement>('button')!.disabled) return;
      if (connection()?.configureVoice(this.mode.value as 'off' | 'realtime' | 'gpt-live', this.voice.value as 'cedar' | 'marin' | 'coral'))
        this.draftChanged = false;
    });
    this.element.querySelector('[data-voice="start"]')!.addEventListener('click', () => {
      const consent = this.element.querySelector<HTMLInputElement>('[data-image-consent]')!;
      connection()?.startVoice(consent.checked);
      consent.checked = false;
    });
    document.addEventListener('visibilitychange', () => {
      if (document.hidden) this.element.querySelector<HTMLInputElement>('[data-image-consent]')!.checked = false;
    });
    this.element.querySelector('[data-voice="stop"]')!.addEventListener('click', () => connection()?.stopVoice());
  }
  update(snapshot: DeviceSnapshot): void {
    const voice = snapshot.voice;
    const native = snapshot.source === 'native-device';
    const consent = this.element.querySelector<HTMLInputElement>('[data-image-consent]')!;
    consent.disabled = !native || voice?.canStart !== true || voice?.cameraAvailable !== true || snapshot.busy;
    if (!native || voice?.cameraAvailable !== true) consent.checked = false;
    const configure = native && voice?.canConfigure === true;
    this.mode.disabled = this.voice.disabled = !configure;
    this.mode.querySelector<HTMLOptionElement>('[value="realtime"]')!.disabled = !voice?.available;
    this.mode.querySelector<HTMLOptionElement>('[value="gpt-live"]')!.disabled = !voice?.available;
    this.form.querySelector<HTMLButtonElement>('button')!.disabled = !configure;
    this.element.querySelector<HTMLButtonElement>('[data-voice="start"]')!.disabled = !native || voice?.canStart !== true || snapshot.busy;
    this.element.querySelector<HTMLButtonElement>('[data-voice="stop"]')!.disabled = !native || voice?.canStop !== true;
    if (!this.draftChanged && voice && !voice.pending) { this.mode.value = voice.mode; this.voice.value = voice.voice; }
    this.element.querySelector<HTMLElement>('[role="status"]')!.textContent = native
      ? voice?.status ?? 'Připojte kufr a načtěte jeho hlasové nastavení.'
      : 'Hlas ESP je dostupný po připojení skutečného kufru. Vývojový simulátor nemá mikrofon ani reproduktor.';
  }
}
