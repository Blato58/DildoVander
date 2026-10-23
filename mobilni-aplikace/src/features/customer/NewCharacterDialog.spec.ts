import { mount } from '@vue/test-utils';
import { nextTick } from 'vue';
import { describe, expect, it } from 'vitest';
import NewCharacterDialog from './NewCharacterDialog.vue';

function mountDialog() {
  return mount(NewCharacterDialog, {
    props: { open: true, busy: false, currentCharacterName: 'Luna' },
  });
}

describe('NewCharacterDialog', () => {
  it('starts a distinct blank identity and explains what happens to the current one', () => {
    const wrapper = mountDialog();

    expect(wrapper.get('#new-character-name').element).toHaveProperty('value', '');
    expect(wrapper.get('#new-character-world').element).toHaveProperty('value', '');
    expect(wrapper.get('#new-character-version').element).toHaveProperty('value', '');
    expect(wrapper.get('#new-character-description').element).toHaveProperty('value', '');
    expect(wrapper.get('#new-character-language').element).toHaveProperty('value', '');
    expect((wrapper.get('#new-character-openai-consent').element as HTMLInputElement).checked).toBe(false);
    expect(wrapper.get('dialog').attributes('aria-label')).toBe('Vytvořit novou postavu');
    expect(wrapper.text()).toContain('Aktuální postava: Luna');
    expect(wrapper.text()).toContain('Historie rozhovorů a památky předchozí postavy zůstanou uložené.');
    expect(wrapper.text()).toContain('založí nový');
    wrapper.unmount();
  });

  it('cancels without changing external character inputs or emitting a confirmation', async () => {
    const originalInputs = { name: 'Luna upravená', world: 'Kniha', language: 'cs' };
    const wrapper = mountDialog();
    await wrapper.get('#new-character-name').setValue('Nový koncept');
    await wrapper.get('#new-character-world').setValue('Jiný svět');
    await wrapper.get('button[type="button"]').trigger('click');

    expect(wrapper.emitted('cancel')).toHaveLength(1);
    expect(wrapper.emitted('confirm')).toBeUndefined();
    expect(originalInputs).toEqual({ name: 'Luna upravená', world: 'Kniha', language: 'cs' });
    wrapper.unmount();
  });

  it('resets a fresh blank draft and optional AI consent when reopened', async () => {
    const wrapper = mountDialog();
    await wrapper.get('#new-character-name').setValue('Nová hrdinka');
    await wrapper.get('#new-character-world').setValue('Kniha');
    await wrapper.get('#new-character-openai-consent').setValue(true);

    await wrapper.setProps({ open: false });
    await wrapper.setProps({ open: true });

    expect(wrapper.get('#new-character-name').element).toHaveProperty('value', '');
    expect(wrapper.get('#new-character-world').element).toHaveProperty('value', '');
    expect((wrapper.get('#new-character-openai-consent').element as HTMLInputElement).checked).toBe(false);
    wrapper.unmount();
  });

  it('preserves entered values through busy and error prop updates', async () => {
    const wrapper = mountDialog();
    await wrapper.get('#new-character-name').setValue('  Nová postava  ');
    await wrapper.get('#new-character-world').setValue('  Jiný svět  ');
    await wrapper.get('#new-character-description').setValue('  Zachovat přesně  ');
    await wrapper.setProps({ busy: true, error: 'Uložení se nepodařilo.' });

    expect(wrapper.get('#new-character-name').element).toHaveProperty('value', '  Nová postava  ');
    expect(wrapper.get('#new-character-world').element).toHaveProperty('value', '  Jiný svět  ');
    expect(wrapper.get('#new-character-description').element).toHaveProperty('value', '  Zachovat přesně  ');
    expect(wrapper.text()).toContain('Uložení se nepodařilo.');
    expect(wrapper.get('button[type="submit"]').attributes('disabled')).toBeDefined();

    await wrapper.setProps({ busy: false, error: 'Zkuste to znovu.' });
    expect(wrapper.get('#new-character-name').element).toHaveProperty('value', '  Nová postava  ');
    expect(wrapper.text()).toContain('Zkuste to znovu.');
    wrapper.unmount();
  });

  it('emits the exact raw draft and explicit consent without performing a request', async () => {
    const wrapper = mountDialog();
    await wrapper.get('#new-character-name').setValue('  Nová postava  ');
    await wrapper.get('#new-character-world').setValue('  Jiný svět  ');
    await wrapper.get('#new-character-version').setValue('  Léto  ');
    await wrapper.get('#new-character-description').setValue('  Její vlastní příběh.  ');
    await wrapper.get('#new-character-language').setValue(' cs ');
    await wrapper.get('#new-character-openai-consent').setValue(true);
    await wrapper.get('form').trigger('submit');

    expect(wrapper.emitted('confirm')).toEqual([[
      {
        name: '  Nová postava  ',
        world: '  Jiný svět  ',
        version: '  Léto  ',
        description: '  Její vlastní příběh.  ',
        language: ' cs ',
        openAiConsent: true,
      },
    ]]);
    expect(wrapper.emitted('cancel')).toBeUndefined();
    wrapper.unmount();
  });

  it('routes Escape through the shared dialog cancel event', async () => {
    const wrapper = mountDialog();
    await nextTick();
    const dialog = wrapper.get('dialog').element;
    const cancel = new Event('cancel', { cancelable: true });
    dialog.dispatchEvent(cancel);

    expect(cancel.defaultPrevented).toBe(true);
    expect(wrapper.emitted('cancel')).toHaveLength(1);
    expect(dialog.hasAttribute('open')).toBe(true);
    wrapper.unmount();
  });

  it.each(['showModal', 'close'] as const)('uses an explicit inline flow when native %s is unavailable', async method => {
    const descriptor = Object.getOwnPropertyDescriptor(HTMLDialogElement.prototype, method);
    const opener = document.createElement('button');
    document.body.append(opener);
    let wrapper: ReturnType<typeof mount> | undefined;
    try {
      Object.defineProperty(HTMLDialogElement.prototype, method, { configurable: true, value: undefined });
      wrapper = mount(NewCharacterDialog, { props: { open: false, busy: false }, attachTo: document.body });
      opener.focus();
      expect(wrapper.find('form').exists()).toBe(false);
      await wrapper.setProps({ open: true });
      await nextTick();
      expect(wrapper.find('dialog').exists()).toBe(false);
      expect(wrapper.get('section[aria-label="Vytvořit novou postavu"]').attributes('aria-modal')).toBeUndefined();
      expect(document.activeElement).toBe(wrapper.get('#new-character-name').element);
      await wrapper.get('#new-character-name').setValue('Mira');
      await wrapper.get('#new-character-world').setValue('Star Book');
      await wrapper.get('form').trigger('submit');
      expect(wrapper.emitted('confirm')?.[0]).toEqual([expect.objectContaining({ name: 'Mira', world: 'Star Book', openAiConsent: false })]);
      await wrapper.get('button[type="button"]').trigger('click');
      expect(wrapper.emitted('cancel')).toHaveLength(1);
      await wrapper.setProps({ open: false });
      expect(wrapper.find('form').exists()).toBe(false);
      expect(document.activeElement).toBe(opener);
    } finally {
      wrapper?.unmount();
      opener.remove();
      if (descriptor) Object.defineProperty(HTMLDialogElement.prototype, method, descriptor);
      else delete (HTMLDialogElement.prototype as unknown as Record<string, unknown>)[method];
    }
  });
});
