import { beforeEach, describe, expect, it } from 'vitest';
import { mount } from '@vue/test-utils';
import CustomerDataPanel from './CustomerDataPanel.vue';
import { interfaceLanguagePreference } from '../../application/interfaceLanguage';

function render(accountId = 'customer-1', eligible = true) {
  return mount(CustomerDataPanel, {
    props: { accountId, eligible },
    attachTo: document.body,
    global: {
      stubs: {
        CustomerMemories: { name: 'CustomerMemories', template: '<div data-testid="memories">Paměti</div>' },
        CustomerDataExports: { name: 'CustomerDataExports', template: '<div data-testid="exports">Exporty</div>' },
      },
    },
  });
}

describe('customer data disclosure panel', () => {
  beforeEach(() => {
    interfaceLanguagePreference.setScope(null);
    interfaceLanguagePreference.setLanguage('cs');
  });
  it('starts collapsed, exposes a labelled disclosure, and mounts data readers only after activation', async () => {
    const wrapper = render();
    const toggle = wrapper.get('button');

    expect(wrapper.text()).toContain('Moje data');
    expect(toggle.text()).toBe('Zobrazit moje data');
    expect(toggle.attributes('aria-expanded')).toBe('false');
    expect(wrapper.find(`[id="${toggle.attributes('aria-controls')}"]`).exists()).toBe(true);
    expect(wrapper.find('[data-testid="memories"]').exists()).toBe(false);
    expect(wrapper.find('[data-testid="exports"]').exists()).toBe(false);

    (toggle.element as HTMLButtonElement).focus();
    await toggle.trigger('click');

    expect(toggle.attributes('aria-expanded')).toBe('true');
    expect(toggle.text()).toBe('Skrýt moje data');
    expect(document.activeElement).toBe(toggle.element);
    expect(wrapper.find('[data-testid="memories"]').exists()).toBe(true);
    expect(wrapper.find('[data-testid="exports"]').exists()).toBe(true);

    await toggle.trigger('click');
    expect(toggle.attributes('aria-expanded')).toBe('false');
    expect(wrapper.find('[data-testid="memories"]').exists()).toBe(false);
    expect(wrapper.find('[data-testid="exports"]').exists()).toBe(false);
    wrapper.unmount();
  });

  it('collapses and unmounts account data immediately when the account scope changes', async () => {
    const wrapper = render();
    await wrapper.get('button').trigger('click');
    expect(wrapper.find('[data-testid="memories"]').exists()).toBe(true);

    await wrapper.setProps({ accountId: 'customer-2' });

    expect(wrapper.get('button').attributes('aria-expanded')).toBe('false');
    expect(wrapper.find('[data-testid="memories"]').exists()).toBe(false);
    expect(wrapper.find('[data-testid="exports"]').exists()).toBe(false);
    wrapper.unmount();
  });

  it('does not mount while ineligible and resumes after the parent restores eligibility', async () => {
    const wrapper = render('customer-1', false);
    const toggle = wrapper.get('button');
    expect((toggle.element as HTMLButtonElement).disabled).toBe(true);
    expect(wrapper.find('[data-testid="memories"]').exists()).toBe(false);

    await wrapper.setProps({ eligible: true });
    await toggle.trigger('click');
    expect(wrapper.find('[data-testid="memories"]').exists()).toBe(true);
    wrapper.unmount();
  });

  it('updates the visible disclosure in English without mounting data readers', async () => {
    const wrapper = render();
    interfaceLanguagePreference.setLanguage('en');
    await wrapper.vm.$nextTick();
    expect(wrapper.get('h2').text()).toBe('My data');
    expect(wrapper.get('button').text()).toBe('Show my data');
    expect(wrapper.find('[data-testid="memories"]').exists()).toBe(false);
    wrapper.unmount();
  });
});
