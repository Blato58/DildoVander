import { button, field, statusBadge, notice, emptyState } from '../../../../design-system/ui.js';

export function mountBenchUi(document) {
  const mount = (id, markup) => { document.getElementById(id).innerHTML = markup; };
  mount('log-empty', emptyState({ title: 'Zatím bez sériových zpráv', message: 'Po připojení USB se zde zobrazí posledních 200 řádků.' }));
  mount('connection-actions', button({ id: 'connect', label: 'Připojit USB', variant: 'primary' }) +
    button({ id: 'disconnect', label: 'Odpojit', disabled: true }));
  mount('pulse-actions', button({ id: 'arm', label: 'Arm · připravit jeden pulz', disabled: true }) +
    button({ id: 'pulse', label: 'Pulz 500 ms', variant: 'primary', disabled: true }));
  mount('calibration-actions', button({ id: 'cal-arm', label: 'Arm · připravit pokus', disabled: true }) +
    button({ id: 'cal-move', label: 'Spustit kalibrační pokus', variant: 'primary', disabled: true }));
  mount('auto-actions', button({ id: 'auto-min', label: 'Hledat dolní mez', variant: 'primary', disabled: true }) +
    button({ id: 'auto-max', label: 'Hledat horní mez', variant: 'primary', disabled: true }));
  mount('stop-actions', button({ id: 'stop', label: 'Stop', variant: 'danger', disabled: true }) +
    button({ id: 'noise', label: 'Drhne / vrčí', variant: 'danger', disabled: true }));
  mount('calibration-fields', field({ id: 'cal-width', label: 'Cílová šířka pulzu (µs)',
    control: '<input class="aw-input" type="number" min="600" max="2300" step="1" value="1500" aria-describedby="cal-help">' }) +
    field({ id: 'cal-duration', label: 'Doba pokusu (ms)',
      control: '<input class="aw-input" type="number" min="500" max="3000" step="100" value="1500" aria-describedby="cal-help">' }));
}

export function benchGuidance(state, capabilities, { supported, connecting, closing, automatic }) {
  if (!supported) {
    return { title: 'USB ovládání není dostupné', message: 'Otevřete místní panel v Chrome nebo Edge na počítači. Prohlížeč musí podporovat Web Serial v zabezpečeném kontextu.', tone: 'warning' };
  }
  if (closing) {
    return { title: 'Ukončuje se USB spojení', message: 'Panel se pokouší o Stop před odpojením. Potvrzení signálu nenahrazuje fyzické vypnutí 5 V serv.', tone: 'info' };
  }
  if (connecting) {
    return { title: 'Vyberte USB port', message: 'Napájení 5 V serv ponechte vypnuté. Otevření USB může restartovat ESP; po otevření panel vyžádá Stop.', tone: 'info' };
  }
  if (state.fault) {
    return { title: 'Porucha · vypněte napájení serv', message: 'Pokus je zablokovaný. Zkontrolujte zapojení a hlášení firmwaru; po odstranění příčiny proveďte restart a nové potvrzení Stop.', tone: 'danger' };
  }
  if (!state.connected) {
    return { title: 'Začněte připojením USB', message: 'Vypněte 5 V serv, zavřete sériový monitor a připojte USB. Registry a log z minulého spojení jsou pouze historické.', tone: 'neutral' };
  }
  if (automatic) {
    return { title: 'Probíhá asistované hledání', message: 'Zůstaňte u serva. Při drhnutí nebo vrčení stiskněte Drhne / vrčí a vypněte V+. Návrh meze není měření dorazu.', tone: 'warning' };
  }
  if (state.pending || state.writing) {
    const messages = {
      stop: 'Stop byl vyžádán. Vyčkejte na potvrzení PWM FULL_OFF; samotné odeslání nepotvrzuje vypnutý signál.',
      arm: 'Arm byl vyžádán. Pokus bude dostupný teprve po potvrzení firmwaru a nejvýše na 10 sekund.',
      pulse: 'Pokus byl vyžádán. Vyčkejte na dokončení PWM FULL_OFF z firmwaru; hlášení pulzu nepotvrzuje fyzický pohyb.',
    };
    return { title: 'Čeká na potvrzení firmwaru', message: messages[state.pending] ?? 'Probíhá sériový požadavek. Další pokus vyžaduje potvrzený stav.', tone: 'info' };
  }
  if (!state.baseline) {
    return { title: 'USB otevřeno · chybí potvrzení Stop', message: 'Stiskněte Stop a vyčkejte na jeho potvrzení. Napájení 5 V serv ponechte vypnuté; pouhé USB připojení pokus nepovoluje.', tone: 'warning' };
  }
  if (capabilities.pulse) {
    return { title: 'Arm potvrzen · jeden pokus', message: state.calibrationSupported
      ? 'Spusťte zvolený ruční pokus. Arm platí nejvýše 10 sekund; po pokusu je třeba nové Arm.'
      : 'Firmware dovoluje krátký zkušební pulz. Ruční kalibrace vyžaduje podporu calibration=v2 nebo starší v1. Arm platí nejvýše 10 sekund.', tone: 'warning' };
  }
  return { title: state.lastCompleted ? 'Firmware potvrdil dokončení pokusu' : 'Stop potvrzen · můžete připravit pokus',
    message: state.lastCompleted?.calibration
      ? 'Po vlastní kontrole lze poslední kalibrační cíl uložit do místního profilu. Potvrzení PWM neměří polohu serva; další pokus vyžaduje nové Arm.'
      : state.lastCompleted
      ? 'Krátký zkušební pulz je dokončený a do kalibračního profilu se neukládá. Poloha serva se neměří; další pokus vyžaduje nové Arm.'
      : state.calibrationSupported
      ? 'Zkontrolujte volné servo a napájení, zvolte kanál a cílové hodnoty. Arm připraví jediný pokus; pohyb spouštíte samostatně.'
      : 'Ruční kalibrace čeká na firmware s oznámenými mezemi. K dispozici je samostatný krátký zkušební pulz po novém Arm.', tone: 'info' };
}

export { statusBadge, notice, emptyState };
