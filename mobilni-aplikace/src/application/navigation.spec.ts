import { describe, expect, it } from 'vitest';
import { safeReturnPath } from './navigation';

describe('customer and staff sign-in return paths', () => {
  it('preserves local customer and staff destinations, including their query and fragment', () => {
    for (const path of ['/me', '/me/', '/me/character', '/me/conversation?resume=1', '/me/keepsakes#saved', '/me/account', '/admin/customer-sessions']) {
      expect(safeReturnPath(path)).toBe(path);
    }
  });

  it('rejects foreign origins, controls, API paths and similar but unapproved prefixes', () => {
    for (const path of ['//evil.test/me', 'https://evil.test/me', '/me\\evil', '/me\naccount', '/me-too', '/administrator', '/app/v1/session', '/guest/v1', '/signin']) {
      expect(safeReturnPath(path)).toBe('/');
    }
  });
});
