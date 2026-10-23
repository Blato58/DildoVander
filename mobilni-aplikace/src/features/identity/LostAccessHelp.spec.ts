import { describe, expect, it } from 'vitest';
import { mount } from '@vue/test-utils';
import { createMemoryHistory, createRouter } from 'vue-router';
import LostAccessHelp from './LostAccessHelp.vue';

async function render() {
  const router = createRouter({
    history: createMemoryHistory(),
    routes: [{ path: '/support', component: { template: '<div />' } }],
  });
  await router.push('/');
  return mount(LostAccessHelp, { global: { plugins: [router] } });
}

describe('lost access guidance', () => {
  it('offers an expandable keyboard-native summary and existing support route', async () => {
    const wrapper = await render();
    const details = wrapper.get('details');
    expect(details.get('summary').text()).toBe('Ztratili jste přístup?');
    expect(details.attributes('open')).toBeUndefined();

    await details.get('summary').trigger('click');
    expect(details.attributes('open')).toBeDefined();
    expect(wrapper.get('a[href="/support"]').text()).toBe('kontaktujte podporu');
    expect(wrapper.text()).toContain('Podpora vás provede ověřením vlastnictví účtu; jednorázový kód obnovy může vystavit pouze vlastník služby.');
    expect(wrapper.text()).toContain('jednorázový kód s omezenou platností');
    expect(wrapper.text()).toContain('původní klíče odebrány a ostatní relace odhlášeny');
    expect(wrapper.text()).toContain('Zobrazované jméno ani odkaz na památku vlastnictví účtu neprokazují.');
    expect(wrapper.text()).toContain('Nikomu neposílejte přístupové klíče ani obnovovací kódy.');
    expect(wrapper.find('form').exists()).toBe(false);
    expect(wrapper.find('input').exists()).toBe(false);
    expect(wrapper.findAll('button')).toHaveLength(0);
    wrapper.unmount();
  });
});
