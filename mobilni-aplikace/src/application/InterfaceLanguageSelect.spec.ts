import { mount } from '@vue/test-utils';
import { describe, expect, it } from 'vitest';
import { defineComponent, h } from 'vue';
import InterfaceLanguageSelect from './InterfaceLanguageSelect.vue';
import { createInterfaceLanguagePreference, type InterfaceLanguageStorage } from './interfaceLanguage';

class MemoryStorage implements InterfaceLanguageStorage {
  private readonly values = new Map<string, string>();
  getItem(key: string) { return this.values.get(key) ?? null; }
  setItem(key: string, value: string) { this.values.set(key, value); }
}

describe('InterfaceLanguageSelect', () => {
  it('labels the native selector and updates the active account without leaking another choice', async () => {
    const preference = createInterfaceLanguagePreference(new MemoryStorage());
    const wrapper = mount(InterfaceLanguageSelect, { props: { preference, accountId: 'account-a' } });
    const select = wrapper.get('select');
    expect(wrapper.get('label').text()).toBe('Jazyk rozhraní');
    expect(select.attributes('aria-describedby')).toBe(wrapper.get('small').attributes('id'));
    expect(wrapper.get('small').text()).toContain('trvalé uložení zatím není ověřené');
    expect((select.element as HTMLSelectElement).value).toBe('cs');

    await select.setValue('en');
    expect(preference.language.value).toBe('en');
    expect(wrapper.get('label').text()).toBe('Interface language');
    expect(wrapper.get('small').text()).toBe('Saved only for this account on this device.');
    await wrapper.setProps({ accountId: 'account-b' });
    expect((wrapper.get('select').element as HTMLSelectElement).value).toBe('cs');
    expect(wrapper.get('label').text()).toBe('Jazyk rozhraní');
    expect(wrapper.get('small').text()).toBe('Pro tento účet v tomto zařízení; trvalé uložení zatím není ověřené.');
    await wrapper.get('select').setValue('en');
    await wrapper.setProps({ accountId: null });
    expect((wrapper.get('select').element as HTMLSelectElement).value).toBe('cs');
    expect(wrapper.get('small').text()).toBe('Pro toto zařízení; trvalé uložení zatím není ověřené.');
    await wrapper.setProps({ accountId: 'account-a' });
    expect((wrapper.get('select').element as HTMLSelectElement).value).toBe('en');
    wrapper.unmount();
  });

  it('uses distinct label and hint IDs for multiple selectors and preserves a blocked-storage runtime choice', async () => {
    const blockedStorage: InterfaceLanguageStorage = {
      getItem() { throw new Error('storage denied'); },
      setItem() { throw new Error('storage denied'); },
    };
    const preference = createInterfaceLanguagePreference(blockedStorage);
    const wrapper = mount(defineComponent({
      setup: () => () => h('div', [
        h(InterfaceLanguageSelect, { preference, accountId: 'account-a' }),
        h(InterfaceLanguageSelect, { preference, accountId: 'account-a' }),
      ]),
    }));
    const [firstSelect, secondSelect] = wrapper.findAll('select');
    expect(firstSelect.attributes('id')).not.toBe(secondSelect.attributes('id'));
    const labels = wrapper.findAll('label');
    const hints = wrapper.findAll('small');
    expect(labels[0].attributes('for')).toBe(firstSelect.attributes('id'));
    expect(labels[1].attributes('for')).toBe(secondSelect.attributes('id'));
    expect(firstSelect.attributes('aria-describedby')).not.toBe(secondSelect.attributes('aria-describedby'));
    await firstSelect.setValue('en');
    expect((secondSelect.element as HTMLSelectElement).value).toBe('en');
    expect(labels[0].text()).toBe('Interface language');
    expect(labels[1].text()).toBe('Interface language');
    expect(hints[1].text()).toContain('only for this app session');
    wrapper.unmount();
  });
});
