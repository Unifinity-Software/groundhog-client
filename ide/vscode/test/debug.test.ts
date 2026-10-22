import { describe, expect, it, vi } from 'vitest';
import { DebugHosting } from '../src/debug';
import { MockBackend } from '../src/mockBackend';

const settings = { configurations: [], stopAfterDebugging: false };
describe('debug hosting', () => {
  it('connects before launch, injects URL, reuses an existing agent and preserves it by default', async () => {
    const backend = new MockBackend(); const debug = new DebugHosting(backend);
    const list = await backend.request('tunnels/list', { workspace: 'example' }); const request = vi.spyOn(backend, 'request');
    const config = { name: 'App', type: 'coreclr', env: { EXISTING: 'value' } };
    await debug.beforeLaunch(config, { tunnel: 'my-tunnel', target: 'http://localhost:5173' }, 'example', [], list.tunnels, settings, true);
    expect(request).toHaveBeenCalledWith('agents/start', { workspace: 'example', name: 'my-tunnel', target: 'http://localhost:5173' });
    expect(config.env).toEqual({ EXISTING: 'value', GROUNDHOG_PUBLIC_URL: 'https://my-tunnel.groundhog.unifinity.com' });
    debug.sessionStarted('first', config); await debug.sessionEnded('first', false);
    expect(request).not.toHaveBeenCalledWith('agents/stop', expect.anything());
    request.mockClear(); await debug.beforeLaunch(config, { tunnel: 'my-tunnel' }, 'example', list.agents, list.tunnels, settings, true);
    expect(request).not.toHaveBeenCalled();
    debug.sessionStarted('existing', config); await debug.sessionEnded('existing', true); expect(request).not.toHaveBeenCalled(); debug.dispose();
  });
  it('stops only an agent started by debugging after its last related session', async () => {
    const backend = new MockBackend(); const debug = new DebugHosting(backend); const list = await backend.request('tunnels/list', { workspace: 'example' });
    const a = { name: 'A', type: 'node' }; const b = { name: 'B', type: 'node' };
    await debug.beforeLaunch(a, { tunnel: 'my-tunnel' }, 'example', [], list.tunnels, settings, true); debug.sessionStarted('a', a);
    await debug.beforeLaunch(b, { tunnel: 'my-tunnel' }, 'example', list.agents, list.tunnels, settings, true); debug.sessionStarted('b', b);
    const request = vi.spyOn(backend, 'request'); await debug.sessionEnded('a', true); expect(request).not.toHaveBeenCalled();
    await debug.sessionEnded('b', true); expect(request).toHaveBeenCalledWith('agents/stop', { workspace: 'example', name: 'my-tunnel', expectedProcessId: 1234 }); debug.dispose();
  });
  it('continues without a missing key, and honors trust/configuration filters', async () => {
    const backend = new MockBackend(); const debug = new DebugHosting(backend); const list = await backend.request('tunnels/list', { workspace: 'example' }); const request = vi.spyOn(backend, 'request');
    expect(await debug.beforeLaunch({ name: 'App' }, { tunnel: 'service-a' }, 'example', [], list.tunnels, settings, true)).toEqual({ missingKey: true });
    await debug.beforeLaunch({ name: 'App' }, { tunnel: 'my-tunnel' }, 'example', [], list.tunnels, settings, false);
    await debug.beforeLaunch({ name: 'Other' }, { tunnel: 'my-tunnel' }, 'example', [], list.tunnels, { ...settings, configurations: ['App'] }, true);
    expect(request).not.toHaveBeenCalled();
  });
});
