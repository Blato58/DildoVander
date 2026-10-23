import { describe, expect, it } from 'vitest';
import { defaultSignedInDestination, getCustomerJourneyAction } from './customerJourney';

const customer = { id: 'customer', displayName: 'Zákazník', roles: ['Customer'] };
const staff = { id: 'staff', displayName: 'Obsluha', roles: ['Operator'] };
const mixed = { id: 'mixed', displayName: 'Obsluha', roles: ['Customer', 'Operator'] };
const completed = 'completed';
const locator = 'e513a7ce-1f2a-4320-9ca2-463fb179b0f4';

describe('customer journey actions', () => {
  it('defaults a new customer to creating their character and a returning customer to conversation', () => {
    expect(defaultSignedInDestination(customer)).toBe('/me/character');
    expect(getCustomerJourneyAction({ account: customer })).toMatchObject({
      key: 'create-character',
      label: 'Vytvořit postavu',
      route: '/me/character',
      canStartConversation: false,
    });

    expect(getCustomerJourneyAction({ account: customer, profile: { status: 'ready' } })).toMatchObject({
      key: 'continue-conversation',
      route: '/me/conversation',
      canStartConversation: true,
    });
  });

  it('keeps staff and mixed-role accounts on the staff default while honoring safe explicit returns', () => {
    expect(defaultSignedInDestination(staff)).toBe('/settings');
    expect(defaultSignedInDestination(mixed)).toBe('/settings');
    expect(defaultSignedInDestination(staff, '/suitcase')).toBe('/suitcase');
    expect(defaultSignedInDestination(mixed, '//evil.example')).toBe('/settings');
    expect(getCustomerJourneyAction({ account: mixed, profile: { status: 'ready' } })).toMatchObject({
      key: 'staff-home',
      route: '/settings',
      canStartConversation: false,
    });
  });

  it('preserves a customer keepsake deep link but falls back for an invalid explicit return', () => {
    const keepsake = '/keepsake/?account=1&k=' + locator;
    expect(defaultSignedInDestination(customer, keepsake)).toBe(keepsake);
    expect(defaultSignedInDestination(customer, '/mevil/character')).toBe('/me/character');
    expect(defaultSignedInDestination(customer, '/keepsake/?account=1&k=' + locator + '&return=//evil.example'))
      .toBe('/me/character');
    expect(defaultSignedInDestination(mixed, keepsake)).toBe(keepsake);
  });

  it.each(['recovering', 'queued', 'expired', 'ended', 'cancelled', 'future_server_state'])(
    'prioritizes an active or unknown nonterminal session (%s) over a linked result',
    sessionState => {
      expect(getCustomerJourneyAction({
        account: customer,
        sessionState,
        profile: { status: 'ready' },
        latestAuthorizedResult: { locator },
      })).toMatchObject({
        key: 'resume-session',
        route: '/me/conversation',
        meaning: 'active-session',
        canStartConversation: false,
      });
    },
  );

  it('offers an account-linked result only from a separately supplied association locator', () => {
    expect(getCustomerJourneyAction({
      account: customer,
      sessionState: completed,
      profile: { status: 'ready' },
      latestAuthorizedResult: { locator },
    })).toMatchObject({
      key: 'open-latest-result',
      route: '/keepsake/?account=1&k=' + locator,
      meaning: 'latest-result',
      canStartConversation: true,
    });

    expect(getCustomerJourneyAction({
      account: customer,
      sessionState: completed,
      profile: { status: 'ready' },
    })).toMatchObject({
      key: 'continue-conversation',
      route: '/me/conversation',
    });
  });

  it('keeps a saved profile usable without requiring AI preparation or consent', () => {
    expect(getCustomerJourneyAction({ account: customer, profile: { status: 'saved' } })).toMatchObject({
      key: 'continue-conversation',
      label: 'Pokračovat k rozhovoru',
      canStartConversation: true,
    });
  });

  it('shows profile review and preparation guidance without claiming readiness', () => {
    expect(getCustomerJourneyAction({ account: customer, profile: { status: 'needs_clarification' } })).toMatchObject({
      key: 'review-character',
      route: '/me/character',
      canStartConversation: false,
    });
    expect(getCustomerJourneyAction({ account: customer, profile: { status: 'queued' } })).toMatchObject({
      key: 'review-preparation',
      meaning: 'preparation-pending',
      canStartConversation: false,
    });
  });

  it.each([
    { loading: true },
    { error: true },
    { stale: true },
  ])('does not treat unsettled inputs as ready or enable a new start: %o', state => {
    expect(getCustomerJourneyAction({
      account: customer,
      profile: { status: 'ready' },
      ...state,
    })).toMatchObject({
      key: 'refresh-session',
      route: '/me/conversation',
      meaning: 'verification-required',
      canStartConversation: false,
    });
  });

  it('keeps result navigation internal and rejects unsafe locator input', () => {
    const uppercaseLocator = locator.toUpperCase();
    const valid = getCustomerJourneyAction({
      account: customer,
      profile: { status: 'ready' },
      latestAuthorizedResult: { locator: uppercaseLocator },
    });
    expect(valid.route).toBe('/keepsake/?account=1&k=' + uppercaseLocator);
    expect(valid.route).toMatch(/^\/keepsake\/\?account=1&k=/);

    const invalid = getCustomerJourneyAction({
      account: customer,
      profile: { status: 'ready' },
      latestAuthorizedResult: { locator: locator + '&redirect=https://evil.example' },
    });
    expect(invalid.route).toBe('/me/conversation');
  });

  it('requires an account before selecting a journey action', () => {
    expect(getCustomerJourneyAction({ account: null })).toMatchObject({
      key: 'sign-in',
      route: '/signin',
      canStartConversation: false,
    });
  });
});
