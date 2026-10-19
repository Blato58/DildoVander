import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { stripTypeScriptTypes } from 'node:module';
import { test } from 'node:test';

async function moduleUrl(name) {
  const source = await readFile(new URL(`../src/features/debug/${name}.ts`, import.meta.url), 'utf8');
  return `data:text/javascript;base64,${Buffer.from(stripTypeScriptTypes(source, { mode: 'transform' })).toString('base64')}`;
}
const source = await readFile(new URL('../src/features/debug/DebugPanel.ts', import.meta.url), 'utf8');
const code = stripTypeScriptTypes(source, { mode: 'transform' })
  .replace(/'\.\.\/\.\.\/\.\.\/\.\.\/design-system\/ui\.js'/, JSON.stringify(new URL('../../design-system/ui.js', import.meta.url).href))
  .replace(/import \{ nativeDebugBridge \} from '\.\/NativeDebugBridge';/, 'const nativeDebugBridge = () => ({});')
  .replace(/'\.\/DebugJournal'/, JSON.stringify(await moduleUrl('DebugJournal')))
  .replace(/'\.\/ServerLogs'/, JSON.stringify(await moduleUrl('ServerLogs')));
const { DebugPanel, bridge, migrateConversationEngine, validGuestMemoryId } = await import(`data:text/javascript;base64,${Buffer.from(code + '\nexport { bridge };').toString('base64')}`);

function deferred() {
  let resolve, reject;
  const promise = new Promise((yes, no) => { resolve = yes; reject = no; });
  return { promise, resolve, reject };
}
function setup(context) {
  const previousDocument = globalThis.document;
  globalThis.document = { hidden: false };
  context.after(() => { globalThis.document = previousDocument; });
  const elements = new Map();
  const find = selector => {
    if (!elements.has(selector)) elements.set(selector, { value: '', textContent: '', hidden: true, replaceChildren() {}, append() {} });
    return elements.get(selector);
  };
  const panel = Object.create(DebugPanel.prototype);
  Object.assign(panel, { native: true, pending: false, cancellationPending: false,
    actionGeneration: 0, statusSequence: 0, photoSequence: 0,
    voice: { configured: true, running: false, session: true, phase: 'awaiting', mode: 'openai' },
    element: { querySelector: find, querySelectorAll: () => [] },
    journal: { record() {} }, renderVoice() {}, renderConversation() {}, renderLog() {}, renderMemory() {}, renderConversationControls() {},
    async refreshVoiceAsync() {} });
  bridge.stop = async () => {};
  bridge.status = async () => ({ phase: 'completed' });
  panel.selectedCapability = () => ({ ready: true, voices: ['cedar'], model: 'gpt-live-1' });
  return { panel, find };
}

test('late diagnostic from a previous guest cannot unlock a newer operation', async context => {
  const { panel, find } = setup(context);
  const oldAnswer = deferred();
  const newOpening = deferred();
  bridge.speakSystem = () => oldAnswer.promise;
  bridge.startConversation = () => newOpening.promise;
  find('[data-conversation-answer]').value = 'První host';
  const oldAction = panel.actionAsync('system');
  await panel.actionAsync('conversation-stop');
  panel.voice = { configured: true, running: false, session: false, phase: 'idle', mode: 'fake' };
  find('[data-conversation-answer]').value = 'Nový host – rozepsaná odpověď';
  find('[data-conversation-voice]').value = 'cedar';
  find('[data-conversation-speed]').value = '1';
  find('[data-conversation-engine]').value = 'gpt-live';
  const nextAction = panel.actionAsync('conversation-ai');
  assert.equal(panel.pending, true);
  oldAnswer.resolve();
  await oldAction;
  assert.equal(panel.pending, true);
  assert.equal(find('[data-conversation-answer]').value, 'Nový host – rozepsaná odpověď');
  newOpening.resolve();
  await nextAction;
  assert.equal(panel.pending, false);
});

test('cancel remains serialized while an old diagnostic finishes', async context => {
  const { panel, find } = setup(context);
  const answer = deferred(), stop = deferred();
  bridge.speakSystem = () => answer.promise;
  let stops = 0;
  bridge.stop = () => { stops++; return stop.promise; };
  find('[data-conversation-answer]').value = 'Host';
  const oldAction = panel.actionAsync('system');
  const cancellation = panel.actionAsync('conversation-stop');
  await panel.actionAsync('conversation-stop');
  answer.resolve();
  await oldAction;
  assert.equal(panel.pending, true);
  assert.equal(panel.cancellationPending, true);
  assert.equal(stops, 1);
  stop.resolve();
  await cancellation;
  assert.equal(panel.pending, false);
  assert.equal(panel.cancellationPending, false);
});

test('a stale failure status cannot replace next guest feedback after cancellation', async context => {
  const { panel, find } = setup(context);
  const status = deferred(), current = deferred();
  bridge.speakSystem = async () => { throw new Error('old operation'); };
  bridge.status = () => status.promise;
  const oldAction = panel.actionAsync('system');
  await Promise.resolve();
  await panel.actionAsync('conversation-stop');
  bridge.speakSystem = () => current.promise;
  const nextAction = panel.actionAsync('system');
  status.resolve({ phase: 'failed' });
  await oldAction;
  assert.equal(find('[data-voice-error]').hidden, true);
  assert.equal(panel.pending, true);
  current.resolve();
  // Normal successful system tests read their final status for the journal.
  bridge.status = async () => ({ phase: 'completed' });
  await nextAction;
});

test('background invalidation discards a captured photo and keeps newer work guarded', async context => {
  const { panel } = setup(context);
  const photo = deferred();
  bridge.captureCostumePhoto = () => photo.promise;
  const oldAction = panel.actionAsync('costume-photo');
  panel.actionGeneration++;
  panel.photoSequence++;
  panel.pending = true; // A newer foreground action owns the busy state.
  globalThis.document.hidden = true;
  photo.resolve({ draftId: 'old-guest-photo' });
  await oldAction;
  assert.equal(panel.photoDraftId, undefined);
  assert.equal(panel.pending, true);
});


test('engine preference migration preserves explicit retained engines and blocks retired unknown selections', () => {
  assert.deepEqual(migrateConversationEngine(null), { engine: 'gpt-live' });
  assert.deepEqual(migrateConversationEngine('realtime'), { engine: 'realtime' });
  assert.deepEqual(migrateConversationEngine('gpt-live'), { engine: 'gpt-live' });
  assert.equal(migrateConversationEngine('realtime-ptt').engine, 'realtime');
  assert.ok(migrateConversationEngine('realtime-ptt').message);
  for (const value of ['chained', 'responses', 'future-engine', '']) {
    assert.equal(migrateConversationEngine(value).engine, undefined);
    assert.ok(migrateConversationEngine(value).message);
  }
});

test('duplicate start taps open exactly one retained provider session', async context => {
  const { panel, find } = setup(context);
  panel.voice.session = false;
  const opening = deferred();
  let starts = 0;
  bridge.startConversation = options => {
    starts++;
    assert.equal(options.engine, 'gpt-live');
    assert.equal(options.expectedMode, 'openai');
    return opening.promise;
  };
  find('[data-conversation-engine]').value = 'gpt-live';
  find('[data-conversation-voice]').value = 'cedar';
  find('[data-conversation-speed]').value = '1';
  const first = panel.actionAsync('conversation-ai');
  await panel.actionAsync('conversation-ai');
  assert.equal(starts, 1);
  opening.resolve();
  await first;
});

test('camera cancellation leaves active voice and request ownership intact', async context => {
  const { panel } = setup(context);
  panel.voice = { configured: true, running: true, session: true, phase: 'listening', mode: 'openai' };
  const generation = panel.actionGeneration;
  let stops = 0;
  bridge.stop = async () => { stops++; };
  bridge.captureCostumePhoto = async () => ({ draftId: null });
  await panel.actionAsync('costume-photo');
  assert.equal(panel.voice.session, true);
  assert.equal(panel.voice.running, true);
  assert.equal(panel.actionGeneration, generation);
  assert.equal(panel.photoPending, false);
  assert.equal(stops, 0);
});

test('optional image upload failure retains current draft for retry without stopping voice', async context => {
  const { panel } = setup(context);
  panel.voice.session = true;
  panel.photoDraftId = 'approved-photo';
  bridge.attachCostumePhoto = async () => { throw new Error('provider unavailable'); };
  await panel.actionAsync('costume-attach');
  assert.equal(panel.photoDraftId, 'approved-photo');
  assert.equal(panel.photoAttaching, false);
  assert.equal(panel.voice.session, true);
  assert.match(panel.photoMessage, /Rozhovor pokračuje/);
});

test('late image acknowledgement from an ended guest cannot replace next guest state', async context => {
  const { panel } = setup(context);
  panel.photoDraftId = 'old-photo';
  const upload = deferred();
  bridge.attachCostumePhoto = () => upload.promise;
  const operation = panel.actionAsync('costume-attach');
  await panel.actionAsync('conversation-stop');
  panel.photoDraftId = 'new-photo';
  panel.photoMessage = 'New guest draft';
  upload.resolve({ status: 'analysed', imageId: 'old-image', revision: 1 });
  await operation;
  assert.equal(panel.photoDraftId, 'new-photo');
  assert.equal(panel.photoMessage, 'New guest draft');
});

test('status polling is serialized and a prior generation cannot overwrite a new guest', async context => {
  const { panel } = setup(context);
  delete panel.refreshVoiceAsync;
  const response = deferred();
  let calls = 0;
  bridge.status = () => { calls++; return response.promise; };
  const first = panel.refreshVoiceAsync();
  await panel.refreshVoiceAsync();
  assert.equal(calls, 1);
  panel.actionGeneration++;
  panel.voice = { phase: 'listening', session: true, effectiveModel: 'gpt-live-1' };
  response.resolve({ phase: 'failed', session: false });
  await first;
  assert.equal(panel.voice.phase, 'listening');
  assert.equal(panel.voice.effectiveModel, 'gpt-live-1');
});


test('capability contract rejects unknown versions, models and incomplete settings', context => {
  const { panel, find } = setup(context);
  delete panel.selectedCapability;
  const capability = { engine: 'gpt-live', model: 'gpt-live-1', ready: true, voices: ['cedar'],
    speed: { supported: false, min: 1, max: 1, default: 1 }, inputModes: ['audio'], cameraSources: ['phone'] };
  find('[data-conversation-engine]').value = 'gpt-live';
  panel.voice.server = { engines: { schemaVersion: 1, primaryEngine: 'gpt-live', engines: [capability] } };
  assert.equal(panel.selectedCapability(), capability);
  for (const engines of [
    { schemaVersion: 2, engines: [capability] },
    { schemaVersion: 1, engines: null },
    { schemaVersion: 1, engines: [null] },
    { schemaVersion: 1, engines: [{ ...capability, model: 'other-model' }] },
    { schemaVersion: 1, engines: [{ ...capability, speed: null }] },
  ]) {
    panel.voice.server.engines = engines;
    assert.equal(panel.selectedCapability(), undefined);
  }
});

test('camera completion after guest ends is discarded instead of becoming the next guest draft', async context => {
  const { panel } = setup(context);
  panel.voice.conversationId = 'old-conversation';
  panel.voice.sessionGeneration = 1;
  const capture = deferred();
  let discards = 0;
  bridge.captureCostumePhoto = () => capture.promise;
  bridge.discardCostumePhoto = async () => { discards++; };
  const operation = panel.actionAsync('costume-photo');
  panel.voice.session = false;
  capture.resolve({ draftId: 'old-guest-photo' });
  await operation;
  assert.equal(panel.photoDraftId, undefined);
  assert.equal(discards, 1);
  assert.match(panel.photoMessage, /Původní rozhovor skončil/);
});

test('native status failure preserves the ability to end an active conversation', async context => {
  const { panel } = setup(context);
  delete panel.refreshVoiceAsync;
  panel.voice.session = true;
  panel.voice.running = true;
  bridge.status = async () => { throw new Error('native unavailable'); };
  await panel.refreshVoiceAsync();
  assert.equal(panel.voice.session, true);
  assert.equal(panel.voice.running, true);
  assert.equal(panel.voice.errorCode, 'native_status_unavailable');
});


test('an initial approved image starts voice first and remains retryable after optional analysis failure', async context => {
  const { panel, find } = setup(context);
  panel.voice.session = false;
  panel.photoDraftId = 'approved-before-start';
  find('[data-conversation-engine]').value = 'gpt-live';
  find('[data-conversation-voice]').value = 'cedar';
  find('[data-conversation-speed]').value = '1';
  const calls = [];
  bridge.startConversation = async options => {
    calls.push('start');
    assert.equal(options.photoDraftId, undefined);
  };
  panel.refreshVoiceAsync = async () => {
    panel.voice.session = true;
    panel.voice.conversationId = 'new-guest';
    panel.voice.sessionGeneration = 2;
  };
  bridge.attachCostumePhoto = async ({draftId}) => {
    calls.push('attach');
    assert.equal(draftId, 'approved-before-start');
    throw new Error('analysis unavailable');
  };
  await panel.actionAsync('conversation-ai');
  assert.deepEqual(calls, ['start', 'attach']);
  assert.equal(panel.photoDraftId, 'approved-before-start');
  assert.equal(panel.voice.session, true);
});

test('photo revocation is serialized and a failed cancellation remains actionable', async context => {
  const { panel } = setup(context);
  panel.photoUsed = true;
  const revocation = deferred();
  let calls = 0;
  bridge.discardCostumePhoto = () => { calls++; return revocation.promise; };
  const operation = panel.actionAsync('costume-discard');
  await panel.actionAsync('costume-discard');
  assert.equal(calls, 1);
  revocation.reject(new Error('offline'));
  await operation;
  assert.equal(panel.photoDiscarding, false);
  assert.equal(panel.photoCancellationFailed, true);
  assert.equal(panel.voice.session, true);
  assert.match(panel.photoMessage, /Server nepotvrdil/);
});


const memoryConversationId = '00000000-0000-4000-8000-000000000090';
function memoryResult() {
  return { identity: null, epoch: 9, deletionEpoch: 2, records: [], totalRecords: 0, hasMore: false,
    memoryStatus: { provider: 'local', configured: true, degraded: false, configurationStatus: 'configured',
      pending: 0, failed: 0, deleting: 0, storedBytes: 0, capacityBytes: 20000, rejectedWrites: 0 } };
}

test('returning guest binding requires explicit verification and is used only for one new conversation', async context => {
  const { panel, find } = setup(context);
  panel.voice.session = false;
  find('[data-guest-id]').value = 'guest-042';
  find('[data-conversation-engine]').value = 'gpt-live';
  find('[data-conversation-voice]').value = 'cedar';
  find('[data-conversation-speed]').value = '1';
  let starts = 0;
  bridge.startConversation = async options => {
    starts++;
    assert.deepEqual(options.guestMemory, { guestId: 'guest-042', identityVerified: true,
      longTermAllowed: true, cloudExportAllowed: false });
  };
  await panel.actionAsync('conversation-ai');
  assert.equal(starts, 0);
  find('[data-guest-verified]').checked = true;
  find('[data-guest-long-term]').checked = true;
  find('[data-guest-cloud]').checked = false;
  await panel.actionAsync('conversation-ai');
  assert.equal(starts, 1);
  assert.equal(find('[data-guest-id]').value, '');
  assert.equal(find('[data-guest-verified]').checked, false);
});

test('memory deletion needs a read, explicit confirmation and the exact inspected epoch after conversation ends', async context => {
  const { panel, find } = setup(context);
  panel.voice.session = false;
  panel.memorySequence = 0;
  find('[data-memory-conversation]').value = memoryConversationId;
  bridge.inspectMemory = async () => memoryResult();
  let deleted = 0;
  bridge.deleteConversationMemory = async options => {
    deleted++;
    assert.deepEqual(options, { conversationId: memoryConversationId, expectedEpoch: 9 });
    return { localDeletion: 'complete', remoteDeletion: 'pending' };
  };
  await panel.actionAsync('memory-delete');
  assert.equal(deleted, 0);
  await panel.actionAsync('memory-inspect');
  await panel.actionAsync('memory-delete');
  assert.equal(deleted, 0);
  find('[data-memory-delete-confirm]').checked = true;
  panel.voice.session = true;
  await panel.actionAsync('memory-delete');
  assert.equal(deleted, 0);
  panel.voice.session = false;
  await panel.actionAsync('memory-delete');
  assert.equal(deleted, 1);
  assert.match(panel.memoryMessage, /ještě čeká na potvrzení/);
  assert.equal(find('[data-memory-delete-confirm]').checked, false);
});

test('stale memory inspection cannot populate a replacement guest or leak the old identity', async context => {
  const { panel, find } = setup(context);
  panel.memorySequence = 0;
  find('[data-memory-conversation]').value = memoryConversationId;
  const read = deferred();
  bridge.inspectMemory = () => read.promise;
  const operation = panel.actionAsync('memory-inspect');
  await panel.actionAsync('conversation-stop');
  read.resolve({ ...memoryResult(), identity: { guestId: 'old-guest' } });
  await operation;
  assert.equal(panel.memoryInspection, undefined);
  assert.equal(panel.memoryPending, false);
});


test('guest IDs preserve exact opaque codes and reject whitespace or Unicode without normalization', async context => {
  for (const id of ['host-042', 'Host_042.A', 'x', 'a'.repeat(80)]) assert.equal(validGuestMemoryId(id), true, id);
  for (const id of ['', ' host-042', 'host-042 ', 'host 042', 'host\t042', 'host\n042', 'host-042\n', 'host-042\r\n', 'čaroděj', 'host💫', 'a'.repeat(81)]) {
    assert.equal(validGuestMemoryId(id), false, id);
  }
  const { panel, find } = setup(context);
  panel.voice.session = false;
  find('[data-conversation-engine]').value = 'gpt-live';
  find('[data-conversation-voice]').value = 'cedar';
  find('[data-conversation-speed]').value = '1';
  let submitted;
  bridge.startConversation = async options => { submitted = options.guestMemory?.guestId; };
  for (const id of [' host-042', 'host 042', 'čaroděj']) {
    find('[data-guest-id]').value = id;
    find('[data-guest-verified]').checked = true;
    await panel.actionAsync('conversation-ai');
    assert.equal(submitted, undefined);
    assert.equal(find('[data-guest-id]').value, id);
  }
  find('[data-guest-id]').value = 'Host_042.A';
  find('[data-guest-verified]').checked = true;
  await panel.actionAsync('conversation-ai');
  assert.equal(submitted, 'Host_042.A');
});
