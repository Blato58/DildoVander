import { mount } from '@vue/test-utils';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { interfaceLanguagePreference } from '../../application/interfaceLanguage';
import CustomerDemo from './CustomerDemo.vue';

describe('CustomerDemo', () => {
  beforeEach(() => {
    interfaceLanguagePreference.setScope(null);
    interfaceLanguagePreference.setLanguage('cs');
  });
  afterEach(() => {
    interfaceLanguagePreference.setScope(null);
    interfaceLanguagePreference.setLanguage('cs');
  });

  it('walks through the fictional story, supports keyboard-focusable controls, and resets', async () => {
    const wrapper = mount(CustomerDemo, { attachTo: document.body });
    expect(wrapper.get('h1').text()).toBe('Příběh bez připojení');
    expect(wrapper.get('h2').text()).toBe('Poznejte postavu');
    expect(wrapper.get('[role="status"]').text()).toContain('Syntetická ukázka');
    expect(wrapper.text()).toContain('nepoužívá mikrofon');
    expect(wrapper.text()).toContain('neukládá ani nikam neodesílá');
    expect(wrapper.text()).toContain('samostatném výslovném souhlasu');

    const buttons = wrapper.findAll('button');
    expect(buttons.length).toBe(3);
    expect(buttons.every((button) => button.element.tagName === 'BUTTON')).toBe(true);
    expect(buttons[0].attributes('disabled')).toBeDefined();
    buttons[1].element.focus();
    expect(document.activeElement).toBe(buttons[1].element);
    await buttons[1].trigger('click');
    expect(wrapper.get('h2').text()).toBe('Ukázka rozhovoru');
    expect(wrapper.get('[aria-label="Předem napsaný dialog"] li').text()).toContain('Průvodce');
    await wrapper.get('button.aw-button--primary').trigger('click');
    expect(wrapper.get('h2').text()).toBe('Ilustrativní návrh hůlky');
    expect(wrapper.text()).toContain('Žádný předmět nebyl vytvořen ani vyroben');
    for (const designField of ['Tvar', 'Rukojeť', 'Ornament', 'Materiál', 'Magické téma']) {
      expect(wrapper.text()).toContain(designField);
    }
    await wrapper.get('button.aw-button--primary').trigger('click');
    expect(wrapper.get('h2').text()).toBe('Náhled památky');
    expect(wrapper.text()).toContain('Fiktivní náhled památky');
    expect(wrapper.findAll('audio, video, form, input')).toHaveLength(0);

    await wrapper.findAll('button')[0].trigger('click');
    expect(wrapper.get('h2').text()).toBe('Ilustrativní návrh hůlky');
    await wrapper.get('button:not(.aw-button--primary):last-of-type').trigger('click');
    expect(wrapper.get('h2').text()).toBe('Poznejte postavu');
    expect(wrapper.findAll('button')[0].attributes('disabled')).toBeDefined();
    wrapper.unmount();
  });

  it('switches static story copy reactively without leaving the current step or making requests', async () => {
    const fetch = vi.spyOn(globalThis, 'fetch');
    const wrapper = mount(CustomerDemo);
    await wrapper.findAll('button')[1].trigger('click');
    expect(wrapper.get('h2').text()).toBe('Ukázka rozhovoru');

    interfaceLanguagePreference.setLanguage('en');
    await wrapper.vm.$nextTick();
    expect(wrapper.get('h1').text()).toBe('A story without a connection');
    expect(wrapper.get('h2').text()).toBe('Conversation sample');
    expect(wrapper.text()).toContain('does not connect to a suitcase, use a microphone, or send data to AI services');
    expect(wrapper.get('[aria-label="Prewritten dialogue"] li').text()).toContain('Guide');
    expect(wrapper.findAll('button').map((button) => button.text())).toEqual(['Back', 'Next step', 'Start the demo again']);
    expect(fetch).not.toHaveBeenCalled();

    interfaceLanguagePreference.setLanguage('cs');
    await wrapper.vm.$nextTick();
    expect(wrapper.get('h2').text()).toBe('Ukázka rozhovoru');
    expect(fetch).not.toHaveBeenCalled();
    wrapper.unmount();
    fetch.mockRestore();
  });
});
