import { mount } from '@vue/test-utils';
import { beforeEach, describe, expect, it } from 'vitest';
import { createMemoryHistory, createRouter } from 'vue-router';
import { interfaceLanguagePreference } from '../../application/interfaceLanguage';
import CustomerJourneyCard from './CustomerJourneyCard.vue';
import type { CustomerJourneyInput } from './customerJourney';

const customer = { id: 'customer', displayName: 'Host', roles: ['Customer'] };

beforeEach(() => {
  interfaceLanguagePreference.setScope(null);
  interfaceLanguagePreference.setLanguage('cs');
});

async function render(journey: CustomerJourneyInput) {
  const router = createRouter({
    history: createMemoryHistory(),
    routes: [{ path: '/:pathMatch(.*)*', component: { template: '<div />' } }],
  });
  await router.push('/');
  return mount(CustomerJourneyCard, { props: { journey }, global: { plugins: [router] } });
}

describe('customer next-action guidance', () => {
  it('offers one character action for a new customer and keeps account help visible', async () => {
    const wrapper = await render({ account: customer, profile: null });
    expect(wrapper.get('.aw-button--primary').attributes('href')).toBe('/me/character');
    expect(wrapper.text()).toContain('bez přípravy pomocí AI');
    expect(wrapper.get('a[href="/me/account"]').text()).toContain('pomoc');
    wrapper.unmount();
  });

  it('announces failed verification and links to recovery without offering a start', async () => {
    const wrapper = await render({ account: customer, profile: { status: 'ready' }, error: true });
    expect(wrapper.get('[role="status"]').text()).toContain('není ověřený');
    expect(wrapper.get('.aw-button--primary').attributes('href')).toBe('/me/conversation');
    expect(wrapper.text()).not.toContain('Postava je uložená.');
    expect(wrapper.find('button').exists()).toBe(false);
    wrapper.unmount();
  });

  it('returns to an unresolved session despite a missing profile and explains reachability', async () => {
    const wrapper = await render({ account: customer, profile: null, sessionState: 'recovering' });
    expect(wrapper.get('.aw-button--primary').text()).toBe('Pokračovat v rozhovoru');
    expect(wrapper.text()).toContain('nepotvrzuje aktuální spojení');
    wrapper.unmount();
  });

  it('keeps non-AI profiles usable and requires suitcase and consent verification in the conversation', async () => {
    const wrapper = await render({ account: customer, profile: { status: 'saved' } });
    expect(wrapper.get('.aw-button--primary').attributes('href')).toBe('/me/conversation');
    expect(wrapper.text()).toContain('ověříte blízký kufr');
    expect(wrapper.text()).toContain('souhlasy pro tento rozhovor');
    wrapper.unmount();
  });

  it('reactively localizes an active unknown session without changing its route or action', async () => {
    const wrapper = await render({ account: customer, profile: null, sessionState: 'future-active-state' });
    const action = wrapper.get('.aw-button--primary');
    expect(action.text()).toBe('Pokračovat v rozhovoru');
    expect(action.attributes('href')).toBe('/me/conversation');

    interfaceLanguagePreference.setLanguage('en');
    await wrapper.vm.$nextTick();

    expect(wrapper.text()).toContain('Your character, conversation, and keepsakes');
    expect(wrapper.text()).toContain('Return to the existing conversation');
    expect(action.text()).toBe('Continue conversation');
    expect(action.attributes('href')).toBe('/me/conversation');
    wrapper.unmount();
  });
});
