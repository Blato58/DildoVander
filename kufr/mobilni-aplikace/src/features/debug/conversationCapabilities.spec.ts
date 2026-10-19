import { describe, expect, it } from 'vitest';
import { migrateConversationEngine, parseEngineCapabilities, validGuestMemoryId } from './conversationCapabilities';
export const capabilityFixture = () => ({ schemaVersion: 1 as const, primaryEngine: 'gpt-live' as const,
  engines: (['gpt-live', 'realtime'] as const).map(engine => ({ engine,
    model: engine === 'gpt-live' ? 'gpt-live-1' : 'gpt-realtime-2.1', ready: true, voices: ['cedar', 'marin'],
    speed: { supported: engine === 'realtime', min: engine === 'realtime' ? .8 : 1, max: engine === 'realtime' ? 1.2 : 1, default: 1 },
    inputModes: ['audio'], cameraSources: ['phone', 'suitcase'] })) });
describe('canonical conversation capabilities', () => {
  it('keeps exactly the two model identities and fails closed on incompatible metadata', () => {
    expect(parseEngineCapabilities(capabilityFixture())).not.toBeNull();
    for (const edit of [(data: ReturnType<typeof capabilityFixture>) => { data.engines[0]!.model = 'old-model'; },
      (data: ReturnType<typeof capabilityFixture>) => { data.engines[1]!.speed.default = NaN; },
      (data: ReturnType<typeof capabilityFixture>) => { data.engines.pop(); }]) {
      const data = capabilityFixture(); edit(data); expect(parseEngineCapabilities(data)).toBeNull();
    }
  });
  it('migrates only the equivalent PTT preference and requires deliberate choice for other retired engines', () => {
    expect(migrateConversationEngine(null).engine).toBe('gpt-live');
    expect(migrateConversationEngine('realtime-ptt').engine).toBe('realtime');
    expect(migrateConversationEngine('chained').engine).toBeUndefined();
    expect(migrateConversationEngine('realtime').engine).toBe('realtime');
    expect(validGuestMemoryId('Guest-042.a')).toBe(true); expect(validGuestMemoryId('guest name')).toBe(false);
  });
});
