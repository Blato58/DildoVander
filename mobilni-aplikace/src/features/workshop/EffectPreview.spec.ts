import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { flushPromises, mount } from '@vue/test-utils';
import EffectPreview from './EffectPreview.vue';
import { previewFrame, validateEffect } from './effect-preview.mjs';

const model = {
  mechanism: { count: 6, travel_degrees: 160 },
  effects: { max_brightness: 64, main: [{ phase: 'Zdvih', duration_ms: 100, target: 'selected', light: [255,0,0], motion: 'raise', vibration: true }], short: [] },
};
let callbacks: Map<number, FrameRequestCallback>, frame = 0, now = 0;
beforeEach(() => {
  callbacks = new Map(); frame = 0; now = 0;
  vi.stubGlobal('requestAnimationFrame', (callback: FrameRequestCallback) => { callbacks.set(++frame, callback); return frame; });
  vi.stubGlobal('cancelAnimationFrame', (id: number) => callbacks.delete(id));
  vi.spyOn(performance, 'now').mockImplementation(() => now);
});
afterEach(() => { vi.unstubAllGlobals(); vi.restoreAllMocks(); });
async function tick(timestamp: number) {
  const callback = [...callbacks.values()][0]; callbacks.clear(); callback(timestamp); await flushPromises();
}
describe('local illustrative effects', () => {
  it('validates timing/technical bounds without using any transport', () => {
    expect(validateEffect(model.effects.main)).toHaveLength(1);
    expect(() => validateEffect([{ ...model.effects.main[0], duration_ms: 16000 }])).toThrow();
    expect(() => validateEffect([{ ...model.effects.main[0], light: [999,0,0] }])).toThrow();
    const value = previewFrame(model, model.effects.main[0], Array(6).fill(0), 1, .5);
    expect(value.angles).toEqual([0,80,0,0,0,0]); expect(value.outputs).toEqual({ u059_2: 'vibration-commanded' });
  });
  it('finishes at the exact last model pose and clears transient light/output indications', async () => {
    const wrapper = mount(EffectPreview, { props: { model, angles: Array(6).fill(0) } });
    await wrapper.findAll('button').find(button => button.text() === 'Hlavní ilustrace')!.trigger('click');
    await tick(0); now = 100; await tick(100);
    const last = wrapper.emitted('frame')!.at(-1)![0] as any;
    expect(last.angles[0]).toBe(160); expect(last.ledColors).toEqual({}); expect(last.outputs).toEqual({});
    expect(wrapper.text()).toContain('Ilustrační sekvence dokončena'); wrapper.unmount();
  });
  it('honors the monotonic fifteen second deadline and requires an explicit reset', async () => {
    const wrapper = mount(EffectPreview, { props: { model, angles: Array(6).fill(0) } });
    await wrapper.findAll('button').find(button => button.text() === 'Hlavní ilustrace')!.trigger('click');
    await tick(0); now = 16000; await tick(50);
    expect(wrapper.text()).toContain('Technický limit 15 s'); expect(callbacks.size).toBe(0);
    const start = wrapper.findAll('button').find(button => button.text() === 'Hlavní ilustrace')!;
    expect(start.attributes('disabled')).toBeDefined();
    await wrapper.findAll('button').find(button => button.text() === 'Potvrdit reset ilustrace')!.trigger('click');
    expect(start.attributes('disabled')).toBeUndefined(); wrapper.unmount();
  });
  it('unmount cancels animation without emitting an old pose into a new source', async () => {
    const onFrame = vi.fn();
    const wrapper = mount(EffectPreview, { props: { model, angles: Array(6).fill(0), onFrame } });
    await wrapper.findAll('button').find(button => button.text() === 'Hlavní ilustrace')!.trigger('click'); await tick(0);
    const count = onFrame.mock.calls.length; wrapper.unmount();
    expect(onFrame.mock.calls.length).toBe(count); expect(callbacks.size).toBe(0);
  });
});
