import { describe, expect, it } from 'vitest';
import { classifyLabFailure, describeLabFailure } from './errors';

describe('closed Persona Lab failure categories', () => {
  it('matches actual account and configuration codes with actionable Czech text', () => {
    const reauth = classifyLabFailure('recent_passkey_required', 403);
    expect(reauth).toMatchObject({ category: 'Auth', recoverable: false });
    expect(describeLabFailure(reauth)).toContain('přístupovým klíčem');
    expect(classifyLabFailure('lab_revision_conflict', 409).action).toContain('zkontrolujte rozdíl');
    expect(classifyLabFailure('lab_session_closed', 409).action).toContain('nové sezení');
    expect(classifyLabFailure('lab_provider_unavailable', 503)).toMatchObject({ category: 'OpenAIAPI', recoverable: false });
  });
  it('never retries permanent provider auth, protocol or invalid configuration errors', () => {
    for (const code of ['lab_decision_provider_auth', 'lab_provider_unknown_parameter', 'lab_provider_immutable_field',
      'lab_provider_rejected_command', 'invalid_lab_configuration', 'invalid_server_event', 'invalid_socket_address'])
      expect(classifyLabFailure(code).recoverable, code).toBe(false);
    expect(classifyLabFailure('lab_provider_capacity', 429)).toMatchObject({ category: 'OpenAIAPI', recoverable: true });
    expect(classifyLabFailure('lab_provider_timeout', 408)).toMatchObject({ category: 'Network', recoverable: true });
    expect(classifyLabFailure('lab_provider_capacity', 403)).toMatchObject({ category: 'Auth', recoverable: false });
  });
  it('distinguishes validated tool, infrastructure and media evidence from unknown causes', () => {
    expect(classifyLabFailure('lab_provider_invalid_delegation').category).toBe('Tool');
    expect(classifyLabFailure('lab_live_memory_storage_failed').category).toBe('Infrastructure');
    expect(classifyLabFailure('microphone_permission')).toMatchObject({ category: 'AudioVideo', recoverable: false, manualRestart: true });
    const unknown = classifyLabFailure('arbitrary_untrusted_provider_text');
    expect(unknown).toMatchObject({ category: 'Unknown', recoverable: false });
    expect(unknown.message).toContain('není'); expect(unknown.message).not.toContain('síť');
    expect(classifyLabFailure('connection_failed')).toMatchObject({ category: 'Unknown', recoverable: false });
  });
});
