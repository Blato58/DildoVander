import type { DeviceSnapshot } from '../device_connection/FakeDeviceConnection';

interface DanceConnection { startDance(): boolean; stopDance(): boolean; }

export class DancePanel {
  readonly element = document.createElement('section');
  constructor(connection: () => DanceConnection | null) {
    this.element.className = 'conversation-panel dance-panel';
    this.element.setAttribute('aria-label', 'Dance for me');
    this.element.innerHTML = `
      <h2>Dance for me</h2>
      <p>Kufr náhodně vybere jednu z nastavených skladeb na SD kartě, přehraje ji
         svým reproduktorem a roztančí světla a krátké vibrace podle jejího tempa.</p>
      <div class="dance-track" hidden>
        <strong data-dance-track></strong><span data-dance-tempo></span>
        <progress aria-label="Průběh skladby" max="1" value="0"></progress>
        <span data-dance-time></span>
      </div>
      <div class="actions">
        <button type="button" class="primary" data-dance="start" disabled>Dance for me</button>
        <button type="button" data-dance="stop" disabled>Zastavit tanec</button>
      </div>
      <p role="status" aria-live="polite"></p>
      <p class="footnote">Pro tanec uvolněte ruční řízení a ukončete rozhovor.
         Kufr pokračuje i po odpojení telefonu. Stop kufru zastaví hudbu i efekty.</p>`;
    this.element.querySelector('[data-dance="start"]')!.addEventListener('click', () => connection()?.startDance());
    this.element.querySelector('[data-dance="stop"]')!.addEventListener('click', () => connection()?.stopDance());
  }

  update(snapshot: DeviceSnapshot): void {
    const dance = snapshot.dance;
    const native = snapshot.source === 'native-device';
    const start = this.element.querySelector<HTMLButtonElement>('[data-dance="start"]')!;
    start.disabled = !native || dance?.canStart !== true || snapshot.busy;
    start.setAttribute('aria-busy', String(dance?.pending === true));
    this.element.querySelector<HTMLButtonElement>('[data-dance="stop"]')!.disabled = !native || dance?.canStop !== true;
    const details = this.element.querySelector<HTMLElement>('.dance-track')!;
    details.hidden = !native || !dance?.track;
    this.element.querySelector<HTMLElement>('[data-dance-track]')!.textContent = dance?.track ?? '';
    this.element.querySelector<HTMLElement>('[data-dance-tempo]')!.textContent = dance?.bpm ? `${dance.bpm} BPM` : '';
    const progress = this.element.querySelector<HTMLProgressElement>('progress')!;
    progress.max = Math.max(1, dance?.durationMs ?? 0);
    progress.value = Math.min(progress.max, dance?.positionMs ?? 0);
    const time = (ms: number): string => `${Math.floor(ms / 60000)}:${String(Math.floor(ms / 1000) % 60).padStart(2, '0')}`;
    this.element.querySelector<HTMLElement>('[data-dance-time]')!.textContent =
      `${time(dance?.positionMs ?? 0)} / ${time(dance?.durationMs ?? 0)}`;
    const status = native ? dance?.status ?? 'Připojte kufr a načtěte stav tance.'
      : 'Tanec je dostupný po připojení skutečného kufru. Simulace nepřehrává SD ani fyzické efekty.';
    const live = this.element.querySelector<HTMLElement>('[role="status"]')!;
    if (live.textContent !== status) live.textContent = status;
  }
}
