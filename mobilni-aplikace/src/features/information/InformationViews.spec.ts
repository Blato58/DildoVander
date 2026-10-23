import { mount } from '@vue/test-utils';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { nextTick } from 'vue';
import { interfaceLanguagePreference } from '../../application/interfaceLanguage';
import PrivacyView from './PrivacyView.vue';
import SupportView from './SupportView.vue';

const global = {
  stubs: {
    RouterLink: { props: ['to'], template: '<a :href="to"><slot /></a>' },
  },
};

beforeEach(() => {
  interfaceLanguagePreference.setScope(null);
  interfaceLanguagePreference.setLanguage('cs');
});

afterEach(() => {
  interfaceLanguagePreference.setScope(null);
  interfaceLanguagePreference.setLanguage('cs');
});

describe('guest information pages', () => {
  it('localizes support while preserving contact and route destinations', async () => {
    const wrapper = mount(SupportView, { global });
    expect(wrapper.get('h1').text()).toBe('S čím potřebujete pomoct?');
    expect(wrapper.text()).toContain('Přístup k účtu sám o sobě neuděluje oprávnění');
    expect(wrapper.get('a[href^="mailto:"]').attributes('href')).toBe('mailto:support@dildovander.de?subject=Wand%20Night%20Stand%20%E2%80%93%20support');
    expect(wrapper.get('a[href^="mailto:"]').text()).toBe('support@dildovander.de');

    interfaceLanguagePreference.setLanguage('en');
    await nextTick();
    expect(wrapper.get('h1').text()).toBe('How can we help?');
    expect(wrapper.text()).toContain('An account alone does not grant permission');
    expect(wrapper.get('a[href="/account-deletion"]').text()).toBe('Deletion status');
    expect(wrapper.get('a[href="/privacy"]').text()).toBe('How the app processes and stores data');
    expect(wrapper.get('a[href^="mailto:"]').attributes('href')).toBe('mailto:support@dildovander.de?subject=Wand%20Night%20Stand%20%E2%80%93%20support');
    expect(wrapper.get('a[href^="mailto:"]').text()).toBe('support@dildovander.de');
    wrapper.unmount();
  });

  it('localizes privacy disclosures without changing their consent, retention, or deletion boundaries', async () => {
    const wrapper = mount(PrivacyView, { global });
    expect(wrapper.get('h1').text()).toBe('Ochrana soukromí');
    expect(wrapper.text()).toContain('samostatný výslovný souhlas');
    expect(wrapper.text()).toContain('nesmaže cizí památku');
    expect(wrapper.text()).toContain('24 hodin nebo 5 000 záznamů');
    expect(wrapper.get('a[href^="mailto:"]').attributes('href')).toBe('mailto:support@dildovander.de?subject=Wand%20Night%20Stand%20%E2%80%93%20privacy');
    expect(wrapper.get('a[href^="mailto:"]').text()).toBe('support@dildovander.de');

    interfaceLanguagePreference.setLanguage('en');
    await nextTick();
    expect(wrapper.get('h1').text()).toBe('Privacy');
    expect(wrapper.text()).toContain('each require separate explicit consent');
    expect(wrapper.text()).toContain('does not delete someone else’s keepsake');
    expect(wrapper.text()).toContain('24 hours or 5,000 records');
    expect(wrapper.text()).toContain('local completion does not confirm their deletion');
    expect(wrapper.get('a[href="https://openai.com/policies/privacy-policy/"]').attributes('target')).toBe('_blank');
    expect(wrapper.get('a[href="/support"]').text()).toBe('Support and how to send a request safely');
    expect(wrapper.get('a[href^="mailto:"]').attributes('href')).toBe('mailto:support@dildovander.de?subject=Wand%20Night%20Stand%20%E2%80%93%20privacy');
    expect(wrapper.get('a[href^="mailto:"]').text()).toBe('support@dildovander.de');
    wrapper.unmount();
  });
});
