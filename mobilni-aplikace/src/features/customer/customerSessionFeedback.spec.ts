import { describe, expect, it } from 'vitest';
import { projectCustomerSessionError } from './customerSessionFeedback';

describe('projectCustomerSessionError', () => {
  it('maps allowlisted Bluetooth permission failures to actionable Czech feedback', () => {
    expect(projectCustomerSessionError(new Error('permission_denied'))).toEqual({
      message: 'Povolte aplikaci přístup k Bluetooth v Nastavení iPhonu a zkuste připojení znovu.',
      diagnosticCode: 'permission_denied',
    });
  });

  it('maps observed fetch failures without exposing the native message', () => {
    expect(projectCustomerSessionError(new TypeError('Failed to fetch'))).toEqual({
      message: 'Server není dostupný. Zkontrolujte připojení k internetu a zkuste obnovit stav.',
      diagnosticCode: 'network_unavailable',
    });
  });

  it('keeps explicitly mapped server reconciliation guidance', () => {
    expect(projectCustomerSessionError(Object.assign(new Error('untrusted body text'), {
      code: 'customer_session_requires_reconciliation',
    }))).toEqual({
      message: 'Předchozí žádost se ještě ověřuje. Obnovte stav rozhovoru a nepoužívejte nový start.',
      diagnosticCode: 'customer_session_requires_reconciliation',
    });
  });

  it.each(['constructor', 'toString', '__proto__'])('redacts inherited object property %s', code => {
    expect(projectCustomerSessionError({ code })).toEqual({
      message: 'Stav rozhovoru se nepodařilo načíst. Zkuste obnovit stav.',
    });
  });

  it('supports runtimes without Object.hasOwn', () => {
    const descriptor = Object.getOwnPropertyDescriptor(Object, 'hasOwn');
    let feedback: ReturnType<typeof projectCustomerSessionError> | undefined;
    try {
      Reflect.deleteProperty(Object, 'hasOwn');
      feedback = projectCustomerSessionError({ code: 'permission_denied' });
    } finally {
      if (descriptor) Object.defineProperty(Object, 'hasOwn', descriptor);
    }
    expect(feedback?.diagnosticCode).toBe('permission_denied');
    expect(feedback?.message).toContain('Povolte aplikaci přístup k Bluetooth');
  });

  it('redacts unrecognized raw strings and unknown native codes', () => {
    const secret = 'Bearer private-token-123';
    expect(projectCustomerSessionError(secret)).toEqual({
      message: 'Stav rozhovoru se nepodařilo načíst. Zkuste obnovit stav.',
    });
    expect(projectCustomerSessionError({ code: 'private-token-123', message: secret })).toEqual({
      message: 'Stav rozhovoru se nepodařilo načíst. Zkuste obnovit stav.',
    });
  });
});
