import { describe, expect, it } from 'vitest';
import {
  customerDemoSteps,
  getCustomerDemoStep,
  getCustomerDemoSteps,
  initialCustomerDemoStep,
  nextCustomerDemoStep,
  previousCustomerDemoStep,
} from './customerDemoModel';

describe('fictional customer demo model', () => {
  it('defines the four local story stages in a fixed order', () => {
    expect(customerDemoSteps.map(({ id }) => id)).toEqual([
      'character', 'conversation', 'wand-result', 'keepsake',
    ]);
    expect(getCustomerDemoStep('conversation').lines).toHaveLength(2);
    expect(getCustomerDemoStep('wand-result').description).toContain('textový návrh');
    expect(getCustomerDemoStep('wand-result').wandDesign).toMatchObject({
      shape: expect.any(String),
      handle: expect.any(String),
      ornament: expect.any(String),
      material: expect.any(String),
      magicalTheme: expect.any(String),
    });
    expect(getCustomerDemoStep('keepsake').description).toContain('nikoli uložená');
  });

  it('keeps translated wand design values within the established design contract fields', () => {
    const czechDesign = getCustomerDemoStep('wand-result').wandDesign!;
    const englishDesign = getCustomerDemoStep('wand-result', 'en').wandDesign!;
    expect(Object.keys(englishDesign).sort()).toEqual(Object.keys(czechDesign).sort());
    expect(getCustomerDemoSteps('en').map(({ id }) => id)).toEqual(customerDemoSteps.map(({ id }) => id));
    expect(englishDesign).toMatchObject({ shape: expect.any(String), handle: expect.any(String), ornament: expect.any(String), material: expect.any(String), magicalTheme: expect.any(String) });
  });

  it('moves in either direction without escaping the first or last step', () => {
    expect(initialCustomerDemoStep).toBe('character');
    expect(previousCustomerDemoStep('character')).toBe('character');
    expect(nextCustomerDemoStep('character')).toBe('conversation');
    expect(nextCustomerDemoStep('conversation')).toBe('wand-result');
    expect(nextCustomerDemoStep('wand-result')).toBe('keepsake');
    expect(nextCustomerDemoStep('keepsake')).toBe('keepsake');
    expect(previousCustomerDemoStep('keepsake')).toBe('wand-result');
  });
});
