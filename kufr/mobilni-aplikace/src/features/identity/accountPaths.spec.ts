import { describe, expect, it } from 'vitest';
import { isTwinAccountPath } from './accountPaths';
describe('canonical account Twin paths', () => {
  it.each(['case.s3', 'case..s3', 'a'.repeat(96)])('accepts canonical source %s', id => {
    expect(isTwinAccountPath(`/v1/twin/instances/${id}`)).toBe(true);
    expect(isTwinAccountPath(`/v1/twin/instances/${id}/media/start`)).toBe(true);
  });
  it.each(['.', '..', 'case:s3', 'a'.repeat(97), '%2e%2e', 'case/s3', 'case?s3'])('rejects unsafe or noncanonical source %s', id => {
    expect(isTwinAccountPath(`/v1/twin/instances/${id}`)).toBe(false);
  });
});
