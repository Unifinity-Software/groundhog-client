import { afterEach, describe, expect, it, vi } from 'vitest';
import { GroundhogAuthentication } from '../src/auth';
import { DebugHosting } from '../src/debug';
import { MockBackend } from '../src/mockBackend';
import { NotificationPolicy, tunnelLiterals } from '../src/notifications';
import { asCurl, localTarget, parseHeaders, RequestActions, signatureWarning } from '../src/requestActions';
import { ActiveRequestStats } from '../src/stats';
import { selectCli } from '../src/cliSelection';
import { accessDraft } from '../src/visitorAccess';

afterEach(() => vi.useRealTimers());
describe('IDE feature behavior', () => {
  it('selects a newer global CLI, bundled fallback and authoritative configured override', async () => {
    const detect = vi.fn(async (path: string) => ({ available: true as const, version: path === 'groundhog' ? '1.4.0' : '1.3.0-preview.1' }));
    expect((await selectCli('groundhog', '/bundle', '1.3.0-preview.1', detect)).path).toBe('groundhog');
    detect.mockImplementation(async path => path === 'groundhog' ? { available: false, reason: 'missing' } as never : { available: true, version: '1.3.0-preview.1' });
    expect((await selectCli('groundhog', '/bundle', '1.3.0-preview.1', detect)).path).toBe('/bundle');
    expect((await selectCli('/custom', '/bundle', '1.3.0-preview.1', detect)).path).toBe('/custom');
  });
  it('validates visitor access form data before any mutation', () => {
    expect(accessDraft({ default: { allow: ['members'] }, rules: [{ path: '/webhooks', allow: ['anyone'] }], ipRanges: [] }).rules).toHaveLength(1);
    expect(() => accessDraft({ default: { allow: ['anyone', 'members'] }, rules: [], ipRanges: [] })).toThrow('Check');
    expect(() => accessDraft(null)).toThrow('Enter');
  });
  it('reuses a connected Aspire agent without requiring a CLI connection key', async () => {
    const backend = new MockBackend(); const list = await backend.request('tunnels/list', { workspace: 'example' }); const request = vi.spyOn(backend, 'request');
    const debug = new DebugHosting(backend); const config = { name: 'App', type: 'coreclr', env: {} };
    const result = await debug.beforeLaunch(config, { tunnel: 'my-tunnel' }, 'example', [{ ...list.agents[0], source: 'aspire' }], [{ ...list.tunnels[0], hasKey: false }], { configurations: [], stopAfterDebugging: true }, true);
    expect(result.publicUrl).toBe('https://my-tunnel.groundhog.unifinity.com'); expect(request).not.toHaveBeenCalled();
    debug.sessionStarted('a', 'App'); await debug.sessionEnded('a', true); expect(request).not.toHaveBeenCalled();
  });
  it('preserves same-name concurrent debug session ownership', async () => {
    const backend = new MockBackend(); const list = await backend.request('tunnels/list', { workspace: 'example' }); const debug = new DebugHosting(backend); const settings = { configurations: [], stopAfterDebugging: true };
    await debug.beforeLaunch({ name: 'App' }, { tunnel: 'my-tunnel' }, 'example', [], list.tunnels, settings, true);
    await debug.beforeLaunch({ name: 'App' }, { tunnel: 'my-tunnel' }, 'example', list.agents, list.tunnels, settings, true);
    const request = vi.spyOn(backend, 'request'); debug.sessionStarted('a', 'App'); await debug.sessionEnded('a', true); expect(request).not.toHaveBeenCalled();
    debug.sessionStarted('b', 'App'); await debug.sessionEnded('b', true); expect(request).toHaveBeenCalledWith('agents/stop', { workspace: 'example', name: 'my-tunnel' });
  });
  it('completes sign-in if auth changed between status read and listener registration', async () => {
    const backend = new MockBackend(); const auth = new GroundhogAuthentication(backend);
    vi.spyOn(auth, 'getSessions').mockResolvedValue([]);
    await expect(auth.createSession()).resolves.toMatchObject({ id: 'groundhog-cli', accessToken: '' });
    auth.dispose(); backend.dispose();
  });
  it('updates and ages selected-tunnel status without opening an inspector', async () => {
    vi.useFakeTimers(); const backend = new MockBackend(); const changes = vi.fn(); const stats = new ActiveRequestStats(backend, changes);
    const request = vi.spyOn(backend, 'request'); await stats.follow({ workspace: 'example', name: 'my-tunnel' }); await stats.follow({ workspace: 'example', name: 'my-tunnel' });
    expect(request.mock.calls.filter(([method]) => method === 'requests/subscribe')).toHaveLength(1);
    expect(changes).toHaveBeenLastCalledWith({ requests: 2, failures: 0, disconnected: false });
    backend.dispose(); await vi.advanceTimersByTimeAsync(61000); expect(changes).toHaveBeenLastCalledWith({ requests: 0, failures: 0, disconnected: false });
    stats.dispose(); expect(vi.getTimerCount()).toBe(0);
  });
  it('restricts replay to loopback, preserves edited content and rejects truncated requests', async () => {
    const backend = new MockBackend(); const actions = new RequestActions(backend); const detail = await backend.request('requests/detail', { workspace: 'example', name: 'my-tunnel', requestId: 'demo' });
    expect(signatureWarning(detail.headers)).toBe(true); expect(() => localTarget('https://example.com')).toThrow('loopback'); expect(() => parseHeaders('{"X":5}')).toThrow('string');
    const request = vi.spyOn(backend, 'request'); await actions.replay({ workspace: 'example', name: 'my-tunnel' }, detail, 'http://localhost:5173', { Edited: 'yes' }, 'new-body');
    expect(request).toHaveBeenLastCalledWith('requests/replay', { workspace: 'example', name: 'my-tunnel', requestId: 'demo', target: 'http://localhost:5173', headers: { Edited: 'yes' }, body: 'new-body', confirm: true });
    await expect(actions.replay({ workspace: 'example', name: 'my-tunnel' }, { ...detail, truncated: true }, 'http://localhost:5173', {}, '')).rejects.toThrow('truncated');
    expect(asCurl(detail, 'http://localhost:5173', { Test: "a'b" }, 'body')).toContain("'Test: a'\\''b'");
  });
  it('finds C# tunnel literals and rate-limits error/disconnect notifications with fake time', () => {
    vi.useFakeTimers(); expect(tunnelLiterals('builder.WithGroundhogTunnel("my-tunnel");')).toEqual([{ name: 'my-tunnel', offset: 8 }]);
    const policy = new NotificationPolicy(); expect(policy.connection('t', 'connected', true)).toBe(false); expect(policy.connection('t', 'offline', true)).toBe(true);
    expect(policy.keyChanged('t')).toBe(false); vi.advanceTimersByTime(60000); expect(policy.request('t', 500, true)).toBe(true);
    vi.advanceTimersByTime(60000); expect(policy.request('t', 500, true)).toBe(false); vi.advanceTimersByTime(300000); expect(policy.request('t', 500, true)).toBe(true);
    expect(policy.request('other', 503, false)).toBe(false);
  });
});
