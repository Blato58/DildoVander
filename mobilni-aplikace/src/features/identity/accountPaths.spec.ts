import { describe, expect, it } from 'vitest';
import { isTwinAccountPath, isShowAccountPath, isChronicleAdminPath } from './accountPaths';
describe('canonical account Twin paths', () => {
  it.each(['case.s3', 'case..s3', 'a'.repeat(96)])('accepts canonical source %s', id => {
    expect(isTwinAccountPath(`/v1/twin/instances/${id}`)).toBe(true);
    expect(isTwinAccountPath(`/v1/twin/instances/${id}/media/start`)).toBe(true);
  });
  it.each(['.', '..', 'case:s3', 'a'.repeat(97), '%2e%2e', 'case/s3', 'case?s3'])('rejects unsafe or noncanonical source %s', id => {
    expect(isTwinAccountPath(`/v1/twin/instances/${id}`)).toBe(false);
  });
});

describe('bounded account Show operator paths', () => {
  const root = '/v1/show/devices/case.s3';
  const id = '7fc470e2-e25e-46a2-b51b-97d50275ae77';
  it('allows only explicit ambient preview creation and one operation read', () => {
    expect(isShowAccountPath(`${root}/ambient-previews`, 'POST')).toBe(true);
    expect(isShowAccountPath(`${root}/ambient-previews/${id}`)).toBe(true);
    for (const path of [`${root}/ambient-previews`, `${root}/ambient-previews/invalid`, `${root}/ambient-previews/${id}?x=1`]) {
      expect(isShowAccountPath(path)).toBe(false);
    }
    expect(isShowAccountPath(`${root}/ambient-previews/${id}`, 'POST')).toBe(false);
    expect(isShowAccountPath(`${root}/ambient-previews`, 'DELETE')).toBe(false);
  });
  it('accepts exact reads and commands without firmware cue access', () => {
    expect(isShowAccountPath(root)).toBe(true);
    for (const suffix of ['identities', 'prophecies', 'duels']) expect(isShowAccountPath(`${root}/${suffix}?eveningId=${id}`)).toBe(true);
    expect(isShowAccountPath(`${root}/duels/${id}`)).toBe(true);
    expect(isShowAccountPath(`${root}/duels?eveningId=Legacy%20evening`)).toBe(true);
    for (const suffix of ['commands', 'end-evening', 'offline-outcomes', 'duels', `prophecies/${id}/reveal`, `duels/${id}/start`, `duels/${id}/cancel`, `duels/${id}/grants`, `duels/${id}/grants/revoke`]) expect(isShowAccountPath(`${root}/${suffix}`, 'POST')).toBe(true);
  });
  it.each(['/v1/show/device-cues', '/v1/show/offline-outcomes', '/v1/show/devices/../commands', `${root}/commands?x=other`, `${root}/duels/${id}/arbitrary`, `${root}/duels?eveningId=${id}&other=1`, `${root}/duels?eveningId=%00`])('rejects unapproved path %s', path => {
    expect(isShowAccountPath(path, 'POST')).toBe(false);
    expect(isShowAccountPath(path)).toBe(false);
  });
  it('rejects wrong methods and constrains private chronicle query', () => {
    expect(isShowAccountPath(`${root}/commands`, 'DELETE')).toBe(false);
    expect(isChronicleAdminPath(`/v1/admin/show/chronicle?deviceId=case.s3&eveningId=${id}`)).toBe(true);
    expect(isChronicleAdminPath('/v1/admin/show/chronicle?eveningId=Legacy%20evening&deviceId=case.s3')).toBe(true);
    expect(isChronicleAdminPath('/v1/admin/show/chronicle?deviceId=case.s3&eveningId=%0a')).toBe(false);
    expect(isChronicleAdminPath(`/v1/admin/show/chronicle?deviceId=case.s3&eveningId=${id}&x=1`)).toBe(false);
  });
});
