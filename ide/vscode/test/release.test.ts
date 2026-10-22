import { execFileSync } from 'node:child_process';
import { expect, it } from 'vitest';

it('release preflight requires matching tag and exact published backend versions', () => {
  const script = `
    import assert from 'node:assert/strict';
    import manifest from './package.json' with {type:'json'};
    let requests = 0;
    process.env.GITHUB_REF_NAME = 'vscode-v' + manifest.version;
    globalThis.fetch = async (url, options) => { requests++; assert.equal(options.method,'HEAD'); assert.ok(url.includes('/' + manifest.groundhog.bundledCliVersion + '/')); return {ok:true}; };
    await import('./scripts/check-release.mjs?published');
    assert.ok(requests > 0);
    globalThis.fetch = async () => ({ok:false,status:404});
    await assert.rejects(import('./scripts/check-release.mjs?unpublished'), /Publish Groundhog.Cli/);
    process.env.GITHUB_REF_NAME = 'vscode-v0.0.0';
    globalThis.fetch = async () => { throw new Error('Must fail before network access'); };
    await assert.rejects(import('./scripts/check-release.mjs?wrong-tag'), /exactly match/);
    console.log('release guards passed');
  `;
  expect(execFileSync(process.execPath, ['--input-type=module', '-e', script], { encoding: 'utf8', windowsHide: true })).toContain('release guards passed');
});
