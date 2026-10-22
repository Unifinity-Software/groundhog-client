import { beforeEach, describe, expect, it, vi } from 'vitest';
import { ActionUI, hostedUrl, TunnelActions, tunnelLocation } from '../src/actions';
import { MockBackend } from '../src/mockBackend';
import { Tunnel } from '../src/protocol';

let backend: MockBackend; let ui: ActionUI; let tunnel: Tunnel; let actions: TunnelActions;
beforeEach(async () => {
  backend = new MockBackend(); tunnel = (await backend.request('tunnels/list', { workspace: 'example' })).tunnels[0];
  ui = { input: vi.fn(async () => 'http://localhost:5173'), confirm: vi.fn(async () => true), trust: vi.fn(async () => true), open: vi.fn(async () => {}), copy: vi.fn(async () => {}), target: vi.fn(async () => 'http://localhost:5173') };
  actions = new TunnelActions(backend, ui);
});
describe('tunnel actions', () => {
  it('maps local, remote, offline and unknown state safely', async () => {
    const list = await backend.request('tunnels/list', { workspace: 'example' });
    expect(list.tunnels.map(t => tunnelLocation(t, list.agents))).toEqual(['local', 'remote', 'offline']);
    expect(tunnelLocation({ ...tunnel, location: undefined, state: 'new-state' }, [])).toBe('offline');
  });
  it('starts, restarts and stops the selected tunnel with explicit workspace', async () => {
    const request = vi.spyOn(backend, 'request');
    await actions.start(tunnel); expect(request).toHaveBeenLastCalledWith('agents/start', { workspace: 'example', name: 'my-tunnel', target: 'http://localhost:5173' });
    await actions.start(tunnel, true); expect(request).toHaveBeenLastCalledWith('agents/restart', { workspace: 'example', name: 'my-tunnel', target: 'http://localhost:5173' });
    await actions.stop(tunnel); expect(request).toHaveBeenLastCalledWith('agents/stop', { workspace: 'example', name: 'my-tunnel' });
  });
  it('masks and saves a missing key once before hosting, and honors trust', async () => {
    tunnel.hasKey = false;
    const request = vi.spyOn(backend, 'request');
    vi.mocked(ui.input).mockResolvedValueOnce('demo-connection-key');
    await actions.start(tunnel);
    expect(ui.input).toHaveBeenNthCalledWith(1, 'Connection key for my-tunnel', undefined, true);
    expect(request).toHaveBeenNthCalledWith(1, 'keys/set', { workspace: 'example', name: 'my-tunnel', key: 'demo-connection-key' });
    request.mockClear(); vi.mocked(ui.trust).mockResolvedValue(false);
    await actions.start(tunnel); expect(request).not.toHaveBeenCalled();
  });
  it('opens/copies only hosted HTTPS URLs', async () => {
    await actions.open(tunnel); await actions.copy(tunnel);
    expect(ui.open).toHaveBeenCalledWith(tunnel.url); expect(ui.copy).toHaveBeenCalledWith(tunnel.url);
    expect(hostedUrl('https://groundhog.unifinity.com.evil.invalid/')).toBe(false);
    await expect(actions.open({ ...tunnel, url: 'javascript:alert(1)' })).rejects.toThrow('invalid');
  });
  it('creates, confirms rename/delete and sends no destructive call after cancellation', async () => {
    const request = vi.spyOn(backend, 'request');
    vi.mocked(ui.input).mockResolvedValueOnce('new-tunnel'); await actions.create('example');
    expect(request).toHaveBeenLastCalledWith('tunnels/create', { workspace: 'example', name: 'new-tunnel' });
    vi.mocked(ui.input).mockResolvedValueOnce('renamed'); await actions.rename(tunnel);
    expect(request).toHaveBeenLastCalledWith('tunnels/rename', { workspace: 'example', name: 'my-tunnel', newName: 'renamed', confirm: true });
    await actions.delete(tunnel); expect(request).toHaveBeenLastCalledWith('tunnels/delete', { workspace: 'example', name: 'my-tunnel', confirm: true });
    request.mockClear(); vi.mocked(ui.confirm).mockResolvedValue(false); await actions.delete(tunnel); expect(request).not.toHaveBeenCalled();
  });
});
