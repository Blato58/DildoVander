import { beforeEach, describe, expect, it } from 'vitest';
import { nextTick } from 'vue';
import { mount } from '@vue/test-utils';
import { createMemoryHistory, createRouter } from 'vue-router';
import LostAccessHelp from './LostAccessHelp.vue';
import { interfaceLanguagePreference } from '../../application/interfaceLanguage';

async function render() {
  const router = createRouter({
    history: createMemoryHistory(),
    routes: [{ path: '/support', component: { template: '<div />' } }],
  });
  await router.push('/');
  return mount(LostAccessHelp, { global: { plugins: [router] } });
}

describe('lost access guidance', () => {
  beforeEach(() => {
    interfaceLanguagePreference.setScope(null);
    interfaceLanguagePreference.setLanguage('cs');
  });

  it('offers an expandable keyboard-native summary and existing support route', async () => {
    const wrapper = await render();
    const details = wrapper.get('details');
    expect(details.get('summary').text()).toBe('Ztratili jste přístup?');
    expect(details.attributes('open')).toBeUndefined();

    await details.get('summary').trigger('click');
    expect(details.attributes('open')).toBeDefined();
    expect(wrapper.get('a[href="/support"]').text()).toBe('kontaktujte podporu');
    expect(wrapper.text()).toContain('Podpora vás provede ověřením vlastnictví účtu; jednorázový kód obnovy může vystavit pouze vlastník služby.');
    expect(wrapper.text()).toContain('Kód obnovy má omezenou platnost.');
    expect(wrapper.text()).toContain('původní klíče odebrány a ostatní relace odhlášeny');
    expect(wrapper.text()).toContain('Zobrazované jméno ani odkaz na památku vlastnictví účtu neprokazují.');
    expect(wrapper.text()).toContain('Nikomu neposílejte přístupové klíče ani obnovovací kódy.');
    expect(wrapper.find('form').exists()).toBe(false);
    expect(wrapper.find('input').exists()).toBe(false);
    expect(wrapper.findAll('button')).toHaveLength(0);
    wrapper.unmount();
  });

  it('localizes recovery guidance while preserving the existing support destination and safety limits', async () => {
    const wrapper = await render();
    await wrapper.get('summary').trigger('click');
    interfaceLanguagePreference.setLanguage('en');
    await nextTick();
    expect(wrapper.get('summary').text()).toBe('Lost access?');
    expect(wrapper.get('a[href="/support"]').text()).toBe('contact support');
    expect(wrapper.text()).toContain('only the service owner can issue a one-time recovery code');
    expect(wrapper.text()).toContain('original keys are removed and other sessions are signed out');
    expect(wrapper.text()).toContain('Never send anyone your passkeys or recovery codes.');
    wrapper.unmount();
  });
});
