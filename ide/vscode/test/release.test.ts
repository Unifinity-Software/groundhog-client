import { execFileSync } from 'node:child_process';
import { createRequire } from 'node:module';
import { expect, it, vi } from 'vitest';

const require = createRequire(process.cwd() + '/test/release.test.ts');

it('rejects Marketplace HTTP 404 before the installed vsce can report Published', async () => {
  const { RestClient } = require('typed-rest-client/RestClient');
  const { GalleryApi } = require('azure-devops-node-api/GalleryApi');
  const util = require('@vscode/vsce/out/util');
  const zip = require('@vscode/vsce/out/zip');
  const fs = require('node:fs');
  const manifest = require('../package.json');
  const rest = new RestClient('offline-regression');
  let status = 404;
  let published = false;
  let skipped: boolean;
  const messages: string[] = [];
  // Use the actual installed HTTP response parser and Gallery SDK; only the transport is fake.
  rest.client.sendStream = async () => ({ message: { statusCode: status, headers: {} }, readBody: async () => '{"message":"private server body"}' });
  const api = {
    getExtension: async () => ({ versions: [] }),
    updateExtension: (...args: unknown[]) => GalleryApi.prototype.updateExtension.apply({
      vsoClient: { getVersioningData: async () => ({ requestUrl: 'https://example.com/offline', apiVersion: '7.2' }) },
      createRequestOptions: () => ({}), rest, formatResponse: (value: unknown) => value
    }, args)
  };
  const originalUpload = RestClient.prototype.uploadStream;
  const guard = require.resolve('../scripts/strict-marketplace.cjs');
  try {
    vi.spyOn(util, 'getGalleryAPI').mockResolvedValue(api);
    vi.spyOn(zip, 'readVSIXPackage').mockResolvedValue({ manifest, xmlManifest: { PackageManifest: { Metadata: [{ Identity: [{ $: { TargetPlatform: 'win32-x64' } }] }] } } });
    vi.spyOn(fs, 'createReadStream').mockReturnValue({});
    vi.spyOn(util.log, 'info').mockImplementation(() => {});
    vi.spyOn(util.log, 'done').mockImplementation((...args: unknown[]) => {
      if (String(args[0]).startsWith('Published ')) published = true;
      if (String(args[0]).includes('Skipping publish.')) skipped = true;
    });
    vi.spyOn(console, 'log').mockImplementation((text: string) => { messages.push(text); });
    const publish = () => require('@vscode/vsce/out/publish').publish({ packagePath: ['offline.vsix'], skipDuplicate: true, pat: 'synthetic-not-a-credential' });
    await publish();
    expect(published).toBe(true); // Demonstrates the installed dependency's false success without the guard.
    require(guard);
    for (const code of [201, 404, 409, 500]) {
      status = code;
      published = false;
      skipped = false;
      if (code === 201 || code === 409) await publish();
      else await expect(publish()).rejects.toThrow(`Marketplace upload PUT failed (HTTP ${code}).`);
      expect(published).toBe(code === 201);
      expect(skipped).toBe(code === 409);
    }
    expect(messages).toEqual([201, 404, 409, 500].map(code => `Marketplace upload PUT HTTP ${code}`));
  } finally {
    RestClient.prototype.uploadStream = originalUpload;
    delete require.cache[require.resolve(guard)];
    vi.restoreAllMocks();
  }
});

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
