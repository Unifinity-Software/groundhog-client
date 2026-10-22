import { describe, expect, it } from 'vitest';
import { detectCli } from '../src/cli';

describe('CLI detection', () => {
  it('uses the configured executable and accepts a current version', async () => {
    expect(await detectCli('/tools/groundhog', '1.2.0', async file => {
      expect(file).toBe('/tools/groundhog');
      return 'groundhog 1.10.0\n';
    })).toEqual({ available: true, version: '1.10.0' });
  });
  it.each(['1.1.9', '1.2.0-preview.1'])('offers an update for %s', async version => {
    expect((await detectCli('groundhog', '1.2.0', async () => version)).available).toBe(false);
  });
  it('handles missing executables and malformed output', async () => {
    expect((await detectCli('missing', '1.2.0', async () => { throw new Error('ENOENT'); })).available).toBe(false);
    expect((await detectCli('groundhog', '1.2.0', async () => 'unknown')).available).toBe(false);
  });
});
