import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils';
import { interfaceLanguagePreference } from '../../application/interfaceLanguage';
import { parseConversationPreferences } from './conversationPreferences';

const api = vi.hoisted(() => ({ request: vi.fn() }));
vi.mock('../identity/session', async () => {
  const { ref } = await import('vue');
  return { account: ref({ id: 'account-a', displayName: 'Guest', roles: ['Customer'] }), accountRequest: api.request };
});
import { account } from '../identity/session';
import ConversationPreferences from './ConversationPreferences.vue';

const view = {
  schemaVersion: 1, profileId: 'profile-a', profileRevision: 3, preferenceRevision: 0,
  conversationLanguage: 'cs-CZ', tone: 'roast', persisted: false,
};
const mounted: VueWrapper[] = [];
function render() {
  const wrapper = mount(ConversationPreferences, { props: { profileId: 'profile-a', profileRevision: 3 } });
  mounted.push(wrapper);
  return wrapper;
}

beforeEach(() => {
  api.request.mockReset();
  api.request.mockResolvedValue(view);
  account.value = { id: 'account-a', displayName: 'Guest', roles: ['Customer'] };
  interfaceLanguagePreference.setScope('account-a');
  interfaceLanguagePreference.setLanguage('cs');
});
afterEach(() => { mounted.splice(0).forEach(wrapper => wrapper.unmount()); });

describe('conversation preferences', () => {
  it('rejects malformed and stale profile bindings before displaying editable settings', () => {
    expect(parseConversationPreferences(view, 'profile-a', 3)).toEqual(view);
    for (const changed of [
      { schemaVersion: 2 }, { profileId: 'other-profile' }, { profileRevision: 4 },
      { preferenceRevision: -1 }, { preferenceRevision: 0.5 }, { conversationLanguage: 'de-DE' },
      { tone: 'unrestricted' }, { persisted: 'yes' }, { tone: ['gentle'] }, { conversationLanguage: ['en-US'] },
    ]) expect(parseConversationPreferences({ ...view, ...changed }, 'profile-a', 3)).toBeNull();
  });

  it('keeps UI language separate and writes only an explicit profile-bound next-session choice', async () => {
    const wrapper = render();
    await flushPromises();
    expect(api.request).toHaveBeenCalledTimes(1);
    interfaceLanguagePreference.setLanguage('en');
    await flushPromises();
    expect(wrapper.text()).toContain('conversation language is independent');
    expect(api.request).toHaveBeenCalledTimes(1);
    expect(wrapper.findAll('select')[0]!.element.value).toBe('cs-CZ');
    await wrapper.findAll('select')[0]!.setValue('en-US');
    await wrapper.findAll('select')[1]!.setValue('gentle');
    expect(api.request).toHaveBeenCalledTimes(1);
    api.request.mockResolvedValue({ ...view, conversationLanguage: 'en-US', tone: 'gentle', preferenceRevision: 1, persisted: true });
    await wrapper.get('form').trigger('submit');
    await flushPromises();
    expect(api.request).toHaveBeenLastCalledWith('/app/v1/customer/conversation-preferences', 'PUT', {
      schemaVersion: 1, profileId: 'profile-a', profileRevision: 3, expectedPreferenceRevision: 0,
      conversationLanguage: 'en-US', tone: 'gentle',
    }, expect.any(AbortSignal));
    expect(wrapper.text()).toContain('Saved for the next new conversation.');
    expect(interfaceLanguagePreference.language.value).toBe('en');
  });

  it('clears stale settings after a conflict and requires an explicit reload before retrying', async () => {
    const wrapper = render();
    await flushPromises();
    api.request.mockRejectedValueOnce({ status: 409, message: 'private-provider-secret' });
    await wrapper.get('form').trigger('submit');
    await flushPromises();
    expect(wrapper.find('form').exists()).toBe(false);
    expect(wrapper.text()).not.toContain('private-provider-secret');
    expect(api.request).toHaveBeenCalledTimes(2);
    api.request.mockResolvedValue({ ...view, preferenceRevision: 2, persisted: true });
    await wrapper.get('button').trigger('click');
    await flushPromises();
    expect(wrapper.find('form').exists()).toBe(true);
    expect(api.request.mock.calls.at(-1)?.[1]).toBe('GET');
  });

  it('aborts a save on account change and discards its late success', async () => {
    const wrapper = render();
    await flushPromises();
    let finish!: (value: unknown) => void;
    let saveSignal!: AbortSignal;
    api.request.mockImplementationOnce((_path, _method, _body, signal) => {
      saveSignal = signal;
      return new Promise(resolve => { finish = resolve; });
    });
    await wrapper.get('form').trigger('submit');
    api.request.mockResolvedValueOnce({ ...view, profileId: 'profile-b' });
    account.value = { id: 'account-b', displayName: 'Next', roles: ['Customer'] };
    finish({ ...view, preferenceRevision: 1, persisted: true });
    await flushPromises();
    expect(saveSignal.aborted).toBe(true);
    expect(wrapper.find('form').exists()).toBe(false);
    expect(wrapper.find('[role="status"]').exists()).toBe(false);
  });
});
