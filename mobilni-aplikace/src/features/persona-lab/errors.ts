export type LabErrorCategory = 'Auth' | 'Network' | 'ClientLifecycle' | 'OpenAIAPI' | 'AudioVideo'
  | 'Backend' | 'Infrastructure' | 'Tool' | 'Unknown';

export interface LabFailure {
  category: LabErrorCategory;
  message: string;
  action: string;
  recoverable: boolean;
  manualRestart: boolean;
  reauthenticate: boolean;
}

const labels: Record<LabErrorCategory, string> = {
  Auth: 'Ověření a oprávnění', Network: 'Spojení', ClientLifecycle: 'Požadavek nebo stav klienta',
  OpenAIAPI: 'API modelu', AudioVideo: 'Zvuk a obraz', Backend: 'Serverová integrace',
  Infrastructure: 'Provoz serveru', Tool: 'Simulovaný nástroj', Unknown: 'Neurčená příčina',
};

function failure(category: LabErrorCategory, message: string, action: string, recoverable = false, manualRestart = false, reauthenticate = false): LabFailure {
  return { category, message, action, recoverable, manualRestart, reauthenticate };
}

/** Only application-owned codes establish a cause; arbitrary provider strings do not. */
export function classifyLabFailure(code: string, status = 0): LabFailure {
  if (code === 'recent_passkey_required') return failure('Auth',
    'Změna konfigurace vyžaduje nové ověření přístupovým klíčem.', 'Ověřte přihlášení a potom akci znovu výslovně odešlete.', false, false, true);
  if (['lab_decision_provider_auth', 'lab_provider_auth'].includes(code)) return failure('Auth',
    'Poskytovatel odmítl serverové oprávnění pro model.', 'Správce musí ověřit přístup poskytovatele. Opakování testu ani nové přihlášení testera tento problém neopraví.');
  if (['invalid_lab_ticket', 'connection_policy_rejected'].includes(code))
    return failure('Auth', 'Server nepovolil toto hlasové spojení.', 'Zkontrolujte technické údaje spojení. Nové přihlášení není potvrzenou nápravou tohoto problému.');
  if (status === 401 || ['account_required', 'account_session_revoked'].includes(code))
    return failure('Auth', 'Přihlášení k této operaci není platné.', 'Ověřte přihlášení a potom akci znovu výslovně odešlete.', false, false, true);
  if (status === 403 || ['forbidden', 'confirmation_required', 'csrf_required', 'account_origin_required', 'lab_socket_origin_required'].includes(code))
    return failure('Auth', 'Server tuto operaci nepovolil.', 'Obnovte přehled a ověřte oprávnění nebo požadované potvrzení.');

  if (['transport_error', 'lab_transport_error', 'lab_live_socket_failed', 'lab_live_close_unexpected', 'connection_closed', 'connection_backpressure'].includes(code))
    return failure('Network', 'Spojení se přerušilo; příčina přerušení není potvrzená.',
      'Zkontrolujte síť a obnovte uložené výsledky před opakováním. Nové hlasové spojení vytvoří nový kontext.', true);
  if (['connection_timeout', 'interruption_timeout', 'lab_provider_timeout', 'lab_live_finalization_timeout'].includes(code))
    return failure('Network', 'Operace nebyla potvrzena v časovém limitu.',
      'Zkontrolujte technickou stopu a uložené tahy. Další pokus spusťte výslovně po krátké prodlevě.', true);

  if (code === 'microphone_permission') return failure('AudioVideo', 'Mikrofon nebyl povolen.',
    'Povolte mikrofon v nastavení prohlížeče a potom hlas znovu spusťte.', false, true);
  if (['audio_interrupted', 'microphone_disconnected'].includes(code)) return failure('AudioVideo',
    'Zvuk byl pozastaven nebo se odpojil vstup. Mikrofon i přehrávání byly vypnuté.',
    'Ověřte zvukový vstup a hlas znovu spusťte ručně v novém modelovém kontextu.', false, true);
  if (code === 'microphone_unavailable') return failure('AudioVideo', 'Toto prostředí neposkytuje potřebný mikrofon nebo Web Audio.',
    'Pokračujte textem nebo použijte prostředí s podporovaným zvukovým vstupem.');
  if (code === 'audio_setup_failed') return failure('AudioVideo', 'Zahájení zvukového vstupu se nepodařilo; přesná příčina není známá.',
    'Zkontrolujte podporu zvuku, oprávnění a technickou diagnostiku. Textové testování zůstává dostupné.');
  if (code === 'image_prepare_failed') return failure('AudioVideo', 'Fotografii se nepodařilo připravit.', 'Zkontrolujte podporovaný formát a velikost snímku.');
  if (code === 'audio_stalled') return failure('AudioVideo', 'Přehrávání zvuku se zastavilo. Mikrofon i přehrávání byly vypnuté.',
    'Ověřte zvukový výstup a hlas znovu spusťte ručně v novém modelovém kontextu.', false, true);
  if (code === 'audio_backpressure') return failure('AudioVideo', 'Fronta zvuku překročila technickou kapacitu a přehrávání bylo vypnuté.',
    'Ověřte zvukový výstup. Nové spojení spusťte výslovně po krátké prodlevě.', true);
  if (['lab_image_consent_or_format', 'lab_image_format', 'lab_image_requires_realtime'].includes(code))
    return failure('AudioVideo', 'Obrazový vstup nesplňuje podmínky testu.', 'Zkontrolujte souhlas, formát snímku a výběr Realtime 2.1.');

  if (['lab_provider_capacity', 'lab_decision_provider_capacity', 'lab_provider_server_error', 'lab_decision_provider_server'].includes(code))
    return failure('OpenAIAPI', 'Poskytovatel oznámil kapacitní omezení nebo serverovou chybu.',
      'Vyčkejte. Další pokus musí být výslovný a omezený; původní operace se automaticky neopakuje.', true);
  if (code === 'lab_provider_unavailable') return failure('OpenAIAPI', 'Vybraný model není na serveru připravený.',
    'Správce musí ověřit konfiguraci a přístup modelu. Vyberte dostupný model.');
  if (['lab_provider_unknown_parameter', 'lab_provider_immutable_field', 'lab_provider_rejected_command',
    'lab_decision_provider_rejected', 'lab_provider_invalid_error_event'].includes(code))
    return failure('OpenAIAPI', 'Poskytovatel odmítl příkaz nebo se nepodařilo ověřit jeho formát.',
      'Správce musí zkontrolovat konfiguraci a verzi protokolu. Stejný příkaz neopakujte.');
  if (code === 'lab_provider_moderation') return failure('OpenAIAPI', 'Poskytovatel odmítl tento vstup podle svých pravidel.',
    'Zvolte jiný testovací vstup. Stejný vstup se automaticky neopakuje.');
  if (['lab_provider_error', 'lab_provider_error_unknown', 'lab_provider_no_reply', 'lab_provider_start_failed'].includes(code))
    return failure('OpenAIAPI', 'Model nevrátil potvrzený výsledek; podrobnější příčina není známá.',
      'Zkontrolujte technickou stopu. Bez dalšího důkazu nelze určit, zda pomůže nový pokus.');

  if (['lab_provider_invalid_delegation', 'lab_decision_invalid_evidence', 'lab_decision_incomplete',
    'lab_decision_ambiguous', 'lab_decision_invalid_fields', 'lab_live_decision_count'].includes(code))
    return failure('Tool', 'Výsledek simulovaného nástroje neprošel validací.',
      'Zkontrolujte rozhodnutí a kontrakt v technické stopě. Tato laboratoř nespíná pohony.');
  if (code === 'lab_live_memory_storage_failed') return failure('Infrastructure', 'Server oznámil chybu úložiště testu.',
    'Správce musí zkontrolovat úložiště. Uložení výsledku nepovažujte za potvrzené.');
  if (['lab_concurrency_limit', 'lab_ticket_capacity', 'lab_ephemeral_capacity', 'lab_image_capacity'].includes(code))
    return failure('Infrastructure', 'Server dosáhl technického limitu souběhu nebo dočasného úložiště.',
      'Vyčkejte na dokončení ostatních testů. Potom případný další pokus spusťte výslovně.', true);

  if (code === 'lab_revision_conflict') return failure('ClientLifecycle', 'Aktivní verze se změnila.',
    'Obnovte přehled a zkontrolujte rozdíl před opětovným potvrzením.');
  if (code === 'lab_session_closed') return failure('ClientLifecycle', 'Sezení už skončilo.', 'Pro další test připravte nové sezení.');
  if (code === 'client_platform_unsupported') return failure('ClientLifecycle', 'Prostředí aplikace není rozpoznáno jako web nebo iOS.',
    'Pro nový test použijte podporovanou aplikaci; uložené výsledky zůstávají dostupné.');
  if (code === 'lab_hardware_target_changed') return failure('ClientLifecycle', 'Identita, boot, revize nebo hlas skutečného kufru se změnily.',
    'Test se nepřenese na nový cíl. Ověřte původní kufr a stav ukončení testovací vazby.');
  if (code === 'lab_hardware_start_unconfirmed') return failure('ClientLifecycle', 'Předání skutečného testu není potvrzené.',
    'Test se automaticky neopakuje. Ověřte původní kufr; ukončení serverové vazby není důkaz fyzického zastavení.');
  if (code === 'client_offline') return failure('Network', 'Připojení k síti není dostupné. Audio bylo vypnuté.',
    'Výsledek rozpracovaného požadavku není potvrzený. Po návratu sítě obnovte přehled.');
  if (['user_cancelled', 'lab_live_cancelled', 'lab_live_close_interrupted'].includes(code))
    return failure('ClientLifecycle', 'Operace nebo její dokončení byly přerušené.', 'Zkontrolujte uložené výsledky. Další akce vyžaduje nový výslovný pokyn.');
  if (code === 'lab_session_busy') return failure('ClientLifecycle', 'Server ještě dokončuje předchozí operaci sezení.',
    'Vyčkejte a obnovte přehled před další akcí.', true);
  if (['invalid_lab_configuration', 'lab_instruction_limit'].includes(code)) return failure('ClientLifecycle',
    'Konfigurace nesplňuje podporované hodnoty nebo technické limity.', 'Opravte draft. Stejnou konfiguraci znovu neposílejte.');
  if (['client_request_invalid', 'request_rejected_locally', 'invalid_lab_request', 'invalid_lab_command', 'invalid_lab_session',
    'invalid_lab_evaluation', 'invalid_lab_turn_reference', 'lab_text_requires_http', 'lab_character_input_repair_limit'].includes(code) || status === 400 || status === 422)
    return failure('ClientLifecycle', 'Požadavek byl odmítnut jako neplatný.', 'Zkontrolujte vstup nebo obnovte aplikaci. Odmítnutí samo o sobě neukazuje na chybu sítě.');
  if (['lab_audio_limit', 'lab_input_context_limit', 'lab_event_limit', 'lab_transcript_limit', 'lab_turn_limit'].includes(code))
    return failure('ClientLifecycle', 'Sezení dosáhlo technického limitu.', 'Uložte dostupné hodnocení a pro další test připravte nové sezení.');

  if (code === 'lab_realtime_input_transcription_conflict') return failure('Backend',
    'Přepis řeči neodpovídal právě zpracovávanému tahu.',
    'Předejte technickou časovou osu správci. Další hlasový test spusťte až po ověření příčiny a opravy.');
  if (code === 'lab_realtime_input_buffer_limit') return failure('Backend',
    'Při přerušení odpovědi se další řeč nevešla do omezené vyrovnávací paměti.',
    'Ukončete tento hlasový test a předejte časovou osu správci. Ztracenou řeč nelze vydávat za obnovenou.');
  if (['lab_realtime_response_incomplete', 'lab_realtime_session_invalid', 'lab_realtime_output_invalid',
    'lab_realtime_tool_invalid', 'lab_realtime_event_invalid'].includes(code)) return failure('Backend',
    'Hlasový tah nesplnil ověření protokolu Realtime.',
    'Předejte technickou časovou osu správci. Bez opravy nebo vysvětlení stejný test neopakujte.');
  if (['invalid_response', 'invalid_server_event', 'invalid_socket_address', 'connection_protocol_rejected', 'lab_live_event_json_invalid',
    'lab_live_event_format_invalid', 'lab_live_event_field_missing', 'lab_live_event_timing_invalid', 'lab_live_event_unsupported',
    'lab_live_context_correlation', 'lab_live_context_limit', 'lab_live_data_invalid', 'lab_live_audio_invalid',
    'lab_live_usage_regressed', 'lab_live_final_usage_missing', 'lab_decision_usage_unconfirmed', 'lab_provider_usage_missing',
    'lab_provider_usage_regressed', 'lab_provider_close_unconfirmed', 'lab_live_internal_error', 'lab_character_recovery_unverified'].includes(code))
    return failure('Backend', 'Serverová integrace nevrátila ověřený výsledek protokolu.',
      'Předejte technickou diagnostiku správci. Bez opravy nebo vysvětlení stejný test neopakujte.');
  if (status >= 500) return failure('Backend', 'Server oznámil chybu bez potvrzené podrobnější příčiny.',
    'Zkontrolujte technickou diagnostiku; samotný stav HTTP neurčuje příčinu ani vhodnost opakování.');
  return failure('Unknown', 'Příčina chyby není z dostupných údajů známá.',
    'Zkontrolujte technické údaje a aktuální stav sezení. Automatický nový pokus není povolen.');
}

export function describeLabFailure(value: LabFailure): string {
  return `${labels[value.category]}: ${value.message} ${value.action}`;
}

export function isNetworkTransportFailure(cause: unknown): boolean {
  return cause instanceof DOMException && cause.name === 'NetworkError'
    || cause instanceof TypeError && ['Failed to fetch', 'fetch failed', 'Load failed', 'NetworkError when attempting to fetch resource.'].includes(cause.message);
}
