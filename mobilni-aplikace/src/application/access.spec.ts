import { describe, expect, it } from 'vitest';
import { accessDenial, canEnterStaffArea, canOperateSuitcase, isCustomerAccount } from './access';

const customer = { id: 'guest', displayName: 'Guest', roles: ['Customer'] };
const viewer = { id: 'viewer', displayName: 'Viewer', roles: ['Viewer'] };
const operator = { id: 'operator', displayName: 'Operator', roles: ['Operator'] };

describe('customer and staff route boundaries', () => {
  it('allows every signed-in identity into its own customer pages', () => {
    expect(accessDenial({ requiresAccount: true }, customer)).toBeNull();
    expect(accessDenial({ requiresAccount: true }, operator)).toBeNull();
    expect(isCustomerAccount(customer)).toBe(true);
  });

  it('denies a customer every marked staff route, even when route metadata permits an account', () => {
    expect(accessDenial({ requiresAccount: true, staff: true }, customer)).toBe('staff');
    expect(accessDenial({ requiresAccount: true, staff: true, operation: true }, customer)).toBe('staff');
    expect(accessDenial({ requiresAccount: true, staff: true }, viewer)).toBeNull();
    expect(accessDenial({ requiresAccount: true, staff: true, operation: true }, viewer)).toBe('operator');
  });

  it('keeps customer-only identities outside staff access while respecting explicit staff roles', () => {
    const mixed = { ...customer, roles: ['Customer', 'Operator'] };
    expect(canEnterStaffArea(customer)).toBe(false);
    expect(canOperateSuitcase(customer)).toBe(false);
    expect(isCustomerAccount(mixed)).toBe(false);
    expect(canEnterStaffArea(mixed)).toBe(true);
    expect(canOperateSuitcase(mixed)).toBe(true);
    expect(canEnterStaffArea(viewer)).toBe(true);
    expect(canEnterStaffArea(null)).toBe(false);
  });
});
