import type { DeviceSnapshot } from './FakeDeviceConnection';

export interface OperatorReadiness {
  title: string;
  message: string;
  tone: 'neutral' | 'info' | 'warning' | 'danger';
  action: 'connection' | 'manual' | 'live' | 'recovery';
  actionLabel: string;
}

// This is presentation policy only. Feature adapters still authorize every command.
export function operatorReadiness(snapshot: DeviceSnapshot): OperatorReadiness {
  if (snapshot.phase === 'disconnected') {
    return { title: 'Připojte kufr', message: 'Stav přihrádek není aktuální. Otevřete připojení; uložené párování zůstává zachované.', tone: 'warning', action: 'connection', actionLabel: 'Otevřít připojení kufru' };
  }
  if (snapshot.phase === 'stopped') {
    return { title: 'Nejprve místní kontrola', message: 'Výsledek posledního pohybu je nejistý. Obnovení spojení nenahrazuje kontrolu a servisní zotavení.', tone: 'danger', action: 'recovery', actionLabel: 'Zobrazit zotavení' };
  }
  if (snapshot.voice?.canStop || snapshot.dance?.canStop || snapshot.conversation?.pending) {
    return { title: 'Probíhá akce', message: 'Sledujte její průběh. Otevření jiné sekce akci neruší; Stop zůstává v horní liště.', tone: 'info', action: 'live', actionLabel: 'Zobrazit probíhající akci' };
  }
  if (snapshot.phase === 'running' || snapshot.busy) {
    return { title: 'Probíhá povel kufru', message: 'Vyčkejte na potvrzený výsledek. Přijetí povelu neprokazuje dokončený pohyb; Stop zůstává v horní liště.', tone: 'info', action: 'manual', actionLabel: 'Zobrazit stav povelu' };
  }
  if (snapshot.controlMode === 'manual') {
    return { title: 'Ruční řízení je převzaté', message: 'Pro samostatný rozhovor nebo tanec uvolněte ruční řízení. Přihrádku vybírejte v ručním ovládání.', tone: 'info', action: 'manual', actionLabel: 'Otevřít ruční ovládání' };
  }
  if (snapshot.source === 'local-fake') {
    return { title: 'Vyzkoušejte ovládání', message: 'Jde o místní simulaci bez fyzického kufru. Hlas a tanec potřebují skutečný kufr; ruční postup můžete vyzkoušet zde.', tone: 'neutral', action: 'manual', actionLabel: 'Vyzkoušet ruční ovládání' };
  }
  if (snapshot.source === 'e2-simulator') {
    return { title: 'Simulátor je připojený', message: 'Text a ruční ovládání používají hostitelský simulátor. Stav neprokazuje fyzický pohyb ani audio kufru.', tone: 'info', action: 'live', actionLabel: 'Otevřít textový rozhovor' };
  }
  return { title: 'Kufr je připojený', message: 'Vyberte hlas, tanec nebo text. Dostupnost každé akce potvrzuje kufr; připojení samo nepotvrzuje připravené audio ani polohu přihrádek.', tone: 'neutral', action: 'live', actionLabel: 'Otevřít živý provoz' };
}

export function liveActionBlocker(snapshot: DeviceSnapshot, feature: 'voice' | 'dance'): string {
  if (snapshot.source !== 'native-device') {
    return 'Spuštění vyžaduje připojený skutečný kufr. Simulace neobsahuje jeho audio ani fyzické efekty.';
  }
  if (snapshot.phase === 'disconnected') {
    return 'Kufr je odpojený. V sekci Kufr obnovte spojení a načtěte aktuální stav.';
  }
  if (snapshot.phase === 'stopped') {
    return 'Po Stop je nutná místní kontrola a servisní zotavení kufru.';
  }
  const state = feature === 'voice' ? snapshot.voice : snapshot.dance;
  if (state?.canStop) return '';
  if (snapshot.controlMode === 'manual') {
    return 'Nejprve v ručním ovládání uvolněte řízení. Hlas a tanec spouští samostatný kufr.';
  }
  if (snapshot.voice?.canStop || snapshot.dance?.canStop || snapshot.conversation?.pending) {
    return 'Nejprve ukončete probíhající rozhovor nebo tanec v jeho záložce.';
  }
  if (snapshot.phase === 'running' || snapshot.busy) {
    return 'Probíhá jiná akce. Vyčkejte na její potvrzený výsledek.';
  }
  if (!state?.available) {
    return feature === 'voice' ? 'Kufr nepotvrdil dostupné audio. Ověřte jeho nastavení a zapojení.'
      : 'Kufr nepotvrdil dostupný tanec. Ověřte SD kartu, skladby a efekty.';
  }
  if (feature === 'voice' && snapshot.voice?.mode === 'off') {
    return 'Hlas je vypnutý. Zvolte hlasový režim a použijte nastavení před spuštěním.';
  }
  return state.canStart ? '' : 'Spuštění zatím není dostupné podle kufru. Vyčkejte na aktuální stav.';
}
