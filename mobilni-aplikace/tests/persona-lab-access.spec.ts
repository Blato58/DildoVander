import { describe, expect, it } from 'vitest';
import { accessDenial } from '../src/application/access';
import { safeReturnPath } from '../src/application/navigation';
import { router } from '../src/application/router';

describe('Persona Lab account admission', () => {
  it('admits an online viewer without a device role while rejecting anonymous and cached offline access', () => {
    const route = router.getRoutes().find(item => item.path === '/persona-lab')!;
    const viewer = { id: 'viewer', displayName: 'Tester', roles: ['Viewer'] };
    expect(accessDenial(route.meta, null)).toBe('signin');
    expect(accessDenial(route.meta, viewer)).toBeNull();
    expect(accessDenial(route.meta, viewer, true)).toBe('online');
    expect(safeReturnPath('/persona-lab')).toBe('/persona-lab');
    expect(safeReturnPath('//persona-lab')).toBe('/');
  });
});
