import { mount } from '@vue/test-utils';
import { describe, expect, it } from 'vitest';
import CustomerDemo from './CustomerDemo.vue';

describe('CustomerDemo', () => {
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
});
