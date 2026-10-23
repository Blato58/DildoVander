import { mount } from '@vue/test-utils';
import { describe, expect, it } from 'vitest';
import TonightView from './TonightView.vue';
import type { TonightViewModel } from './useTonight';
import type { TonightProjection, TonightScope } from './tonightProjection';

const scope: TonightScope = { accountId: 'operator-1', deviceId: 'case-1' };
const nowMs = 1_800_000_000_000;

function model(overrides: Partial<TonightViewModel> = {}): TonightViewModel {
  const projection: TonightProjection = {
    session: {
      freshness: 'fresh',
      receivedAtMs: nowMs - 5_000,
      state: 'active',
      current: {
        id: 'session-1', operationId: 'session-operation-1', deviceId: 'case-1',
        state: 'running', endRequested: false, createdAt: 1_799_999_000, characterName: 'Host večera',
      },
    },
    device: { freshness: 'fresh', receivedAtMs: nowMs - 4_000, value: { phase: 'ready', busy: true } },
    network: { freshness: 'fresh', receivedAtMs: nowMs - 4_000, value: { state: 'connected' } },
    audio: { freshness: 'fresh', receivedAtMs: nowMs - 4_000, value: { phase: 'speaking' } },
    memory: {
      freshness: 'fresh', receivedAtMs: nowMs - 3_000, storageStatus: 'warning',
      storedBytes: 1_572_864, capacityBytes: 2_097_152, rejectedWrites: 2,
      storageIncomplete: true, lastStorageErrorCode: 'archive_capacity',
      lastStorageErrorAt: '2026-10-23T10:15:30.000Z',
    },
    nextGuest: { state: 'unknown' },
    keepsake: { state: 'unknown' },
  };

  return {
    scope,
    nowMs,
    projection,
    refreshing: false,
    stale: false,
    access: 'allowed',
    canNextVisit: true,
    canSessionCommand: true,
    error: null,
    operation: null,
    remoteStop: null,
    clientReceivedAtMs: nowMs - 3_000,
    ...overrides,
  };
}

describe('TonightView', () => {
  it('presents scoped current guest, freshness, device evidence and shared memory capacity', () => {
    const wrapper = mount(TonightView, { props: { model: model() } });

    expect(wrapper.get('h1').text()).toBe('Dnešní provoz');
    expect(wrapper.text()).toContain('Host večera');
    expect(wrapper.text()).toContain('Probíhá');
    expect(wrapper.text()).toContain('session-operation-1');
    expect(wrapper.text()).toContain('před 5 s');
    expect(wrapper.text()).toContain('Připraveno');
    expect(wrapper.text()).toContain('zaneprázdněný');
    expect(wrapper.text()).toContain('Připojeno');
    expect(wrapper.text()).toContain('Fáze hlášeného zvuku: mluví');
    expect(wrapper.text()).toContain('Globální archiv všech zařízení a rozhovorů');
    expect(wrapper.text()).toContain('Varování');
    expect(wrapper.text()).toContain('1,50 MiB');
    expect(wrapper.text().replace(/\u00a0/g, ' ')).toContain('1 572 864 B');
    expect(wrapper.text()).toContain('2,00 MiB');
    expect(wrapper.text()).toContain('2 odmítnuté zápisy');
    expect(wrapper.text()).toContain('Některé zápisy nebyly uloženy');
    expect(wrapper.text()).toContain('Poslední odmítnutý zápis se nevešel do tehdy dostupné kapacity');
    expect(wrapper.text()).toContain('Další hostStav neznámý');
    expect(wrapper.text()).toContain('Památka po návštěvěStav připravenosti neznámý');
    expect(wrapper.text()).toContain('Bezpečnostní Stop zůstává dostupný');
    expect(wrapper.text()).not.toContain('canStart');
    expect(wrapper.text()).not.toContain('canStop');
  });

  it('keeps an end request separate from the still-running session state', () => {
    const value = model();
    value.projection.session.current!.endRequested = true;
    const wrapper = mount(TonightView, { props: { model: value } });

    expect(wrapper.text()).toContain('Probíhá');
    expect(wrapper.text()).toContain('Požadavek na ukončení čeká');
    expect(wrapper.text()).not.toContain('Ukončeno');
  });

  it('keeps safe operation controls disabled when the operator snapshot is unavailable', () => {
    const wrapper = mount(TonightView, {
      props: { model: model({ access: 'denied', canNextVisit: false, canSessionCommand: false }) },
    });

    expect(wrapper.text()).toContain('Oprávnění k obsluze není potvrzené');
    expect(wrapper.get('[data-action="next-visit"]').attributes('disabled')).toBeDefined();
    expect(wrapper.get('[data-action="quick-finale"]').attributes('disabled')).toBeDefined();
    expect(wrapper.get('[data-action="guest-leave"]').attributes('disabled')).toBeDefined();
  });

  it('emits only the selected existing director actions and never retries them', async () => {
    const wrapper = mount(TonightView, { props: { model: model() } });

    await wrapper.get('[data-action="next-visit"]').trigger('click');
    await wrapper.get('[data-action="quick-finale"]').trigger('click');
    await wrapper.get('[data-action="guest-leave"]').trigger('click');

    expect(wrapper.emitted('next-visit')).toHaveLength(1);
    expect(wrapper.emitted('quick-finale')).toHaveLength(1);
    expect(wrapper.emitted('guest-leave')).toHaveLength(1);
    expect(wrapper.emitted('refresh')).toBeUndefined();
  });

  it('labels stale, unknown and permission errors without exposing backend text', () => {
    const projection = model().projection;
    projection.session = { freshness: 'unknown', state: 'unknown' };
    projection.memory = { freshness: 'unknown', storageStatus: 'unknown' };
    const wrapper = mount(TonightView, {
      props: {
        model: model({ projection, stale: true, access: 'unknown', error: 'private backend trace must never render' }),
      },
    });

    expect(wrapper.text()).toContain('Stav rozhovoru se ověřuje');
    expect(wrapper.text()).toContain('Údaje mohou být zastaralé');
    expect(wrapper.text()).toContain('Stav společného úložiště není známý');
    expect(wrapper.text()).toContain('Oprávnění k obsluze se ověřuje');
    expect(wrapper.text()).not.toContain('private backend trace');
  });

  it('renders support IDs, client receipt time and command status accessibly', () => {
    const wrapper = mount(TonightView, {
      props: { model: model({ operation: { id: 'operator-command-1', label: 'guest-leave', status: 'Výsledek neznámý.' } }) },
    });
    expect(wrapper.get('[data-operation-status]').text()).toContain('operator-command-1');
    expect(wrapper.get('[data-operation-status]').text()).toContain('Host odešel');
    expect(wrapper.get('[data-operation-status]').text()).not.toContain('guest-leave');
    expect(wrapper.text()).toContain('Serverový snímek relace přijat v tomto zařízení');
    expect(wrapper.text()).toContain('case-1');
    expect(wrapper.find('button[aria-label="Obnovit stav"]').exists()).toBe(true);

    const unknown = mount(TonightView, {
      props: { model: model({ operation: { id: 'operator-command-2', label: 'future-command-from-api', status: 'Výsledek neznámý.' } }) },
    });
    expect(unknown.get('[data-operation-status]').text()).toContain('Povel');
    expect(unknown.get('[data-operation-status]').text()).not.toContain('future-command-from-api');
  });

  it('labels stale device, network and audio observations as last known rather than current', () => {
    const projection = model().projection;
    projection.device.freshness = 'stale';
    projection.network.freshness = 'stale';
    projection.audio.freshness = 'stale';
    const wrapper = mount(TonightView, { props: { model: model({ projection }) } });

    expect(wrapper.text()).toContain('Poslední známé hlášení zařízení: Připraveno · zastaralé');
    expect(wrapper.text()).toContain('Poslední známý stav sítě: Připojeno · zastaralý');
    expect(wrapper.text()).toContain('Poslední známá fáze hlášeného zvuku: mluví · zastaralá');
    expect(wrapper.text()).not.toContain('Připraveno · zaneprázdněný');
  });
});
