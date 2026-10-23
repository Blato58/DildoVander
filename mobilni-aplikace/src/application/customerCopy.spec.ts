import { describe, expect, it } from 'vitest';
import { nextTick, watchEffect } from 'vue';
import { createInterfaceLanguagePreference, type InterfaceLanguageStorage } from './interfaceLanguage';
import {
  createCustomerCopy,
  customerCopyCs,
  customerCopyEn,
  resolveCustomerCopy,
  type CustomerCopyCatalogSource,
  type CustomerCopyKey,
} from './customerCopy';

function createMemoryStorage(): InterfaceLanguageStorage & { values: Map<string, string> } {
  const values = new Map<string, string>();
  return {
    values,
    getItem: key => values.get(key) ?? null,
    setItem: (key, value) => { values.set(key, value); },
  };
}

describe('customer copy catalog', () => {
  it('keeps English exhaustive and aligned with every Czech catalog key', () => {
    expect(Object.keys(customerCopyEn).sort()).toEqual(Object.keys(customerCopyCs).sort());
  });

  it('provides the conversation preference copy in both interface languages', () => {
    expect(customerCopyCs['preferences.intro']).toContain('rozběhnutý rozhovor se nemění');
    expect(customerCopyCs['preferences.limits']).toContain('Souhlas s AI se uděluje zvlášť');
    expect(customerCopyEn['preferences.intro']).toContain('an active conversation stays unchanged');
    expect(customerCopyEn['preferences.limits']).toContain('AI consent is separate');
  });

  it('keeps the synthetic demo privacy and device boundaries explicit in both languages', () => {
    expect(customerCopyCs['demo.disclaimer']).toContain('nepřipojuje kufr');
    expect(customerCopyCs['demo.disclaimer']).toContain('nepoužívá mikrofon');
    expect(customerCopyCs['demo.disclaimer']).toContain('neukládá ani nikam neodesílá');
    expect(customerCopyCs['demo.disclaimer']).toContain('samostatném výslovném souhlasu');
    expect(customerCopyEn['demo.disclaimer']).toContain('does not connect to a suitcase');
    expect(customerCopyEn['demo.disclaimer']).toContain('use a microphone');
    expect(customerCopyEn['demo.disclaimer']).toContain('not saved or sent anywhere');
    expect(customerCopyEn['demo.disclaimer']).toContain('separate, explicit consent');
  });

  it('interpolates parameters as plain text and leaves missing placeholders visible', () => {
    const resolved = resolveCustomerCopy('result.last-verified', 'en', {
      guidance: '<img src=x onerror=alert(1)>',
    });

    expect(resolved.text).toBe('Last verified status: <img src=x onerror=alert(1)>');
    expect(resolved.text).not.toContain('{{guidance}}');
    expect(resolveCustomerCopy('result.last-verified', 'en').text)
      .toBe('Last verified status: {{guidance}}');
  });

  it('reports a Czech fallback for unsupported locale and a missing localized entry', () => {
    const unsupported = resolveCustomerCopy('conversation.mandatory-consent', 'fr');
    expect(unsupported).toMatchObject({ language: 'cs', usedCzechFallback: true, fallbackReason: 'unsupported-language' });
    expect(unsupported.text).toContain('zvuku mého hlasu');

    const catalog: CustomerCopyCatalogSource = { en: {} as Partial<Record<CustomerCopyKey, string>> };
    const missing = resolveCustomerCopy('deletion.irreversible', 'en', {}, catalog);
    expect(missing).toMatchObject({ language: 'cs', usedCzechFallback: true, fallbackReason: 'missing-translation' });
    expect(missing.text).toContain('nelze vrátit zpět');
  });

  it('reacts to the active account-scoped and signed-out device preferences', async () => {
    const storage = createMemoryStorage();
    const preference = createInterfaceLanguagePreference(storage);
    const copy = createCustomerCopy(preference);
    const observed: string[] = [];
    const stop = watchEffect(() => observed.push(copy.text('account-data.title')));

    preference.setScope('account-a');
    preference.setLanguage('en');
    await nextTick();
    expect(copy.text('account-data.title')).toBe('My data');

    preference.setScope('account-b');
    await nextTick();
    expect(copy.text('account-data.title')).toBe('Moje data');

    preference.setScope(null);
    preference.setLanguage('en');
    await nextTick();
    preference.setScope('account-a');
    await nextTick();
    expect(copy.text('account-data.title')).toBe('My data');
    expect(observed).toContain('Moje data');
    expect(observed).toContain('My data');
    expect(storage.values.get('dildovander.interface-language.v1.account.account-a')).toBe('en');
    expect(storage.values.has('dildovander.interface-language.v1.account.account-b')).toBe(false);
    expect(storage.values.get('dildovander.interface-language.v1.device')).toBe('en');
    stop();
  });

  it('keeps mandatory conversation consent, deletion effects, export scope, and ownership distinctions explicit', () => {
    expect(customerCopyCs['conversation.mandatory-consent']).toContain('odesíláním zvuku mého hlasu');
    expect(customerCopyCs['conversation.mandatory-consent']).toContain('společnosti OpenAI');
    expect(customerCopyCs['conversation.mandatory-consent']).toContain('připravené informace');
    expect(customerCopyCs['conversation.mandatory-consent-required']).toContain('rozhovor nespustíme');
    expect(customerCopyCs['conversation.private-memory-optional']).toContain('volitelné');
    expect(customerCopyCs['deletion.irreversible']).toContain('nelze vrátit zpět');
    expect(customerCopyCs['deletion.accepted']).toContain('na všech zařízeních');
    expect(customerCopyCs['export.session-bound']).toContain('relaci, která export připravila');
    expect(customerCopyCs['export.contents']).toContain('pouze právě dostupná povolená fakta');
    expect(customerCopyCs['export.contents']).toContain('propojené soukromé památky jiných vlastníků');
    expect(customerCopyCs['export.delete-confirmation']).toContain('pouze technickou úlohu exportu');
    expect(customerCopyCs['export.delete-confirmation']).toContain('Zdrojová data, postava ani památky se tím nesmažou');
    expect(customerCopyCs['signin.contact-support-after']).toContain('pouze vlastník služby');
    expect(customerCopyCs['signin.recovery-effects']).toContain('původní klíče odebrány');
    expect(customerCopyCs['signin.recovery-code-target']).toContain('soukromý kód');
    expect(customerCopyCs['new-character.intro']).toContain('Historie rozhovorů a památky předchozí postavy zůstanou uložené.');
    expect(customerCopyCs['new-character.consent']).toContain('společnosti OpenAI');
    expect(customerCopyCs['new-character.consent']).toContain('Bez zaškrtnutí uložíte profil bez AI přípravy.');
    expect(customerCopyCs['memories.available-only']).toContain('není automaticky časově omezené');
    expect(customerCopyCs['archive.linked-readonly']).toContain('pouze ke čtení');
    expect(customerCopyCs['archive.owner-access']).toContain('může spravovat');
  });
});
