export const conversationPreferencePath = '/app/v1/customer/conversation-preferences';

export type ConversationLanguage = 'cs-CZ' | 'en-US';
export type ConversationTone = 'gentle' | 'cheeky' | 'roast';

export interface ConversationPreferenceView {
  schemaVersion: 1;
  profileId: string;
  profileRevision: number;
  preferenceRevision: number;
  conversationLanguage: ConversationLanguage;
  tone: ConversationTone;
  persisted: boolean;
}

export function parseConversationPreferences(
  value: unknown,
  profileId: string,
  profileRevision: number,
): ConversationPreferenceView | null {
  if (!value || typeof value !== 'object' || Array.isArray(value)) return null;
  const candidate = value as Record<string, unknown>;
  if (candidate.schemaVersion !== 1 || candidate.profileId !== profileId
    || candidate.profileRevision !== profileRevision || profileRevision < 1
    || !Number.isSafeInteger(profileRevision)
    || !Number.isSafeInteger(candidate.preferenceRevision) || (candidate.preferenceRevision as number) < 0
    || (candidate.conversationLanguage !== 'cs-CZ' && candidate.conversationLanguage !== 'en-US')
    || (candidate.tone !== 'gentle' && candidate.tone !== 'cheeky' && candidate.tone !== 'roast')
    || typeof candidate.persisted !== 'boolean') return null;
  return candidate as unknown as ConversationPreferenceView;
}
