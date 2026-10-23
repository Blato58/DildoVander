import { customerCopy, type CustomerCopyKey } from '../../application/customerCopy';

export interface CustomerSessionFeedback {
  message: string;
  diagnosticCode?: string;
  copyKey?: CustomerCopyKey;
}

const feedbackByCode: Record<string, string> = {
  permission_denied: 'Povolte aplikaci přístup k Bluetooth v Nastavení iPhonu a zkuste připojení znovu.',
  bluetooth_off: 'Zapněte Bluetooth a zkuste připojení znovu.',
  unsupported: 'Toto zařízení nepodporuje potřebné Bluetooth připojení.',
  customer_ble_unsupported: 'Toto zařízení nepodporuje potřebné Bluetooth připojení.',
  device_not_discovered: 'Kufr nebyl nalezen. Přibližte se k němu a spusťte vyhledání znovu.',
  connection_failed: 'Ke kufru se nepodařilo připojit. Zkontrolujte, že je poblíž a zapnutý.',
  disconnected: 'Spojení s kufrem se přerušilo. Připojte se znovu a obnovte stav rozhovoru.',
  requires_update: 'Kufr používá nekompatibilní verzi. Aktualizujte jeho firmware a zkuste to znovu.',
  unavailable: 'Bluetooth spojení teď není dostupné. Zkontrolujte kufr a zkuste to znovu.',
  invalid_response: 'Kufr vrátil neplatnou odpověď. Připojte se znovu a obnovte stav.',
  write_failed: 'Příkaz se do kufru nepodařilo odeslat. Připojte se znovu a obnovte stav.',
  busy: 'Kufr právě zpracovává jiný požadavek. Chvíli počkejte a obnovte stav.',
  cancelled: 'Požadavek byl zrušen. Obnovte stav rozhovoru.',
  network_unavailable: 'Server není dostupný. Zkontrolujte připojení k internetu a zkuste obnovit stav.',
  customer_session_requires_reconciliation: 'Předchozí žádost se ještě ověřuje. Obnovte stav rozhovoru a nepoužívejte nový start.',
  customer_session_unavailable: 'Tuto relaci nelze načíst. Obnovte stav rozhovorů.',
  customer_mode_disabled: 'Obsluha na tomto kufru nepovolila zákaznický režim.',
  device_challenge_unavailable: 'Kufr neposkytl platnou výzvu. Připojte ho znovu a zkuste žádost znovu.',
  customer_profile_revoked: 'Profil už není platný pro nový rozhovor. Obnovte profil postavy.',
  character_not_found: 'Profil postavy nebyl nalezen. Obnovte stránku a zkontrolujte profil.',
  character_revision_mismatch: 'Profil mezitím změnil jiný požadavek. Načtěte aktuální profil.',
  device_busy: 'Kufr právě obsluhuje jiný rozhovor. Chvíli počkejte a obnovte stav.',
  start_ticket_expired: 'Žádost vypršela dříve, než ji kufr převzal. Připojte se znovu a spusťte novou žádost.',
  start_ticket_unavailable: 'Požadavek na spuštění už není dostupný. Obnovte stav a zahajte nový rozhovor.',
  start_ticket_revoked: 'Požadavek na spuštění už není platný. Obnovte stav a zahajte nový rozhovor.',
  start_ticket_limit: 'Počet žádostí o spuštění je dočasně vyčerpán. Chvíli počkejte a zkuste to znovu.',
  device_epoch_mismatch: 'Kufr se od připojení restartoval. Odpojte ho a znovu připojte.',
  openai_consent_required: 'Před spuštěním rozhovoru potvrďte souhlas s AI rozhovorem.',
  device_manage_required: 'K této správě kufru nemáte oprávnění.',
  device_unavailable: 'Kufr není pro správu dostupný.',
  staff_required: 'Tato stránka je dostupná pouze obsluze.',
};

const networkFailureMessages = new Set([
  'Failed to fetch',
  'fetch failed',
  'Load failed',
  'NetworkError when attempting to fetch resource.',
]);

const copyKeyByCode: Readonly<Record<string, CustomerCopyKey>> = {
  permission_denied: 'feedback.permission_denied',
  bluetooth_off: 'feedback.bluetooth_off',
  unsupported: 'feedback.unsupported',
  customer_ble_unsupported: 'feedback.unsupported',
  device_not_discovered: 'feedback.device_not_discovered',
  connection_failed: 'feedback.connection_failed',
  disconnected: 'feedback.disconnected',
  network_unavailable: 'feedback.network_unavailable',
  customer_session_requires_reconciliation: 'feedback.reconciliation',
  requires_update: 'feedback.firmware',
  unavailable: 'feedback.bluetooth-unavailable',
  invalid_response: 'feedback.invalid-response',
  write_failed: 'feedback.write-failed',
  busy: 'feedback.busy',
  cancelled: 'feedback.cancelled',
  customer_session_unavailable: 'feedback.session-unavailable',
  customer_mode_disabled: 'feedback.mode-disabled',
  device_challenge_unavailable: 'feedback.challenge-unavailable',
  customer_profile_revoked: 'feedback.profile-revoked',
  character_not_found: 'feedback.profile-not-found',
  character_revision_mismatch: 'feedback.profile-changed',
  device_busy: 'feedback.device-busy',
  start_ticket_expired: 'feedback.ticket-expired',
  start_ticket_unavailable: 'feedback.ticket-unavailable',
  start_ticket_revoked: 'feedback.ticket-unavailable',
  start_ticket_limit: 'feedback.ticket-limit',
  device_epoch_mismatch: 'feedback.epoch-mismatch',
  openai_consent_required: 'feedback.consent-required',
  device_manage_required: 'feedback.device-manage-required',
  device_unavailable: 'feedback.device-unavailable',
  staff_required: 'feedback.staff-required',
};

function candidateCode(error: unknown): string | undefined {
  if (typeof error === 'string') return error;
  if (!error || typeof error !== 'object') return undefined;
  if ('code' in error && typeof error.code === 'string') return error.code;
  if ('message' in error && typeof error.message === 'string') return error.message;
  return undefined;
}

export function projectCustomerSessionError(error: unknown): CustomerSessionFeedback {
  const candidate = candidateCode(error);
  if (candidate && Object.prototype.hasOwnProperty.call(feedbackByCode, candidate)) {
    const copyKey = copyKeyByCode[candidate];
    return {
      message: copyKey ? customerCopy.text(copyKey) : feedbackByCode[candidate],
      diagnosticCode: candidate,
      ...(copyKey ? { copyKey } : {}),
    };
  }

  if ((error instanceof TypeError || error instanceof Error) && networkFailureMessages.has(error.message)) {
    return {
      message: customerCopy.text('feedback.network_unavailable'),
      diagnosticCode: 'network_unavailable',
      copyKey: 'feedback.network_unavailable',
    };
  }

  const copyKey = 'feedback.generic';
  return { message: customerCopy.text(copyKey), copyKey };
}
