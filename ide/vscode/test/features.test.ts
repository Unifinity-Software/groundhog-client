import { afterEach, describe, expect, it, vi } from 'vitest';
import { GroundhogAuthentication } from '../src/auth';
import { DebugHosting } from '../src/debug';
import { MockBackend } from '../src/mockBackend';
import { NotificationPolicy, tunnelCodeLensTitle, tunnelLiterals } from '../src/notifications';
import { asCurl, localTarget, parseHeaders, RequestActions, signatureWarning } from '../src/requestActions';
import { ActiveRequestStats } from '../src/stats';
import { selectCli } from '../src/cliSelection';
import { accessDraft } from '../src/visitorAccess';
import { AuthStatus, Backend, BackendEvents } from '../src/protocol';

afterEach(() => vi.useRealTimers());
async function stoppedBackend() { const backend = new MockBackend(); await backend.request('agents/stop', { workspace: 'example', name: 'my-tunnel' }); return backend; }
describe('IDE feature behavior', () => {
  it.each(['approved', 'failed', 'expired'] as const)('waits for this login outcome instead of accepting the old valid account (%s)', async outcome => {
    vi.useFakeTimers();
    const events = new BackendEvents();
    let status: AuthStatus = { state: 'signedIn', workspace: 'old' };
    const backend: Backend = {
      capabilities: ['loginPending'], start: async () => {}, dispose: () => events.dispose(),
      on: (name, listener) => events.on(name, listener),
      request: async method => {
        if (method === 'auth/login') {
          status = { ...status, loginId: 'current', loginPending: true };
          return { userCode: 'PUBLIC-CODE', verificationUri: 'https://groundhog.unifinity.com/cli-login', expiresAt: new Date(Date.now() + 600000).toISOString(), loginId: 'current' } as never;
        }
        if (method === 'auth/status') return status as never;
        throw new Error('Unexpected fixture request');
      }
    };
    const auth = new GroundhogAuthentication(backend);
    let settled = false;
    const login = auth.createSession();
    void login.then(() => { settled = true; }, () => { settled = true; });
    await vi.advanceTimersByTimeAsync(0);
    expect(settled).toBe(false);
    expect((await auth.getSessions())[0].account.id).toBe('old');
    // Another attempt or a proactive refresh cannot complete this attempt.
    events.emit('auth/changed', { state: 'signedIn', workspace: 'old', loginPending: false, loginId: 'previous', loginOutcome: 'approved' });
    await vi.advanceTimersByTimeAsync(0); expect(settled).toBe(false);
    status = { state: 'signedIn', workspace: outcome === 'approved' ? 'new' : 'old', loginPending: false, loginId: 'current', loginOutcome: outcome };
    const assertion = outcome === 'approved' ? expect(login).resolves.toMatchObject({ account: { id: 'new' } }) : expect(login).rejects.toThrow('did not complete');
    events.emit('auth/changed', status);
    await assertion;
    auth.dispose(); backend.dispose();
    expect(vi.getTimerCount()).toBe(0);
  });
  it('shows actual local connection transitions in CodeLens instead of stale connected relay state', async () => {
    const backend = new MockBackend(); const { tunnels, agents } = await backend.request('tunnels/list', { workspace: 'example' });
    expect(tunnelCodeLensTitle(tunnels[0], agents)).toBe(`● connected · ${tunnels[0].url}`);
    for (const state of ['Connecting', 'Reconnecting', 'Stopped']) {
      expect(tunnelCodeLensTitle(tunnels[0], [{ ...agents[0], state }])).toBe(`○ ${state === 'Stopped' ? 'offline' : state.toLowerCase()} · ${tunnels[0].url}`);
    }
    expect(tunnelCodeLensTitle(tunnels[1], [])).toBe(`● connected · ${tunnels[1].url}`);
    expect(tunnelCodeLensTitle(tunnels[2], [])).toBe(`○ offline · ${tunnels[2].url}`);
    backend.dispose();
  });
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
    debug.sessionStarted('a', config); await debug.sessionEnded('a', true); expect(request).not.toHaveBeenCalled(); debug.dispose();
  });
  it('preserves same-name concurrent debug session ownership', async () => {
    const backend = await stoppedBackend(); const list = await backend.request('tunnels/list', { workspace: 'example' }); const debug = new DebugHosting(backend); const settings = { configurations: [], stopAfterDebugging: true };
    const a = { name: 'App' }; const b = { name: 'App' };
    await debug.beforeLaunch(a, { tunnel: 'my-tunnel' }, 'example', [], list.tunnels, settings, true);
    await debug.beforeLaunch(b, { tunnel: 'my-tunnel' }, 'example', list.agents, list.tunnels, settings, true);
    const request = vi.spyOn(backend, 'request'); debug.sessionStarted('a', a); await debug.sessionEnded('a', true); expect(request).not.toHaveBeenCalled();
    debug.sessionStarted('b', b); await debug.sessionEnded('b', true); expect(request).toHaveBeenCalledWith('agents/stop', { workspace: 'example', name: 'my-tunnel', expectedProcessId: 1234 }); debug.dispose();
  });
  it('preserves a user replacement agent after debugger-owned PID changes', async () => {
    const backend = await stoppedBackend(); const list = await backend.request('tunnels/list', { workspace: 'example' }); const debug = new DebugHosting(backend); const config = { name: 'App' };
    await debug.beforeLaunch(config, { tunnel: 'my-tunnel' }, 'example', [], list.tunnels, { configurations: [], stopAfterDebugging: true }, true); debug.sessionStarted('first', config);
    const request = vi.spyOn(backend, 'request').mockResolvedValueOnce({ tunnels: list.tunnels, agents: [{ ...list.agents[0], processId: 9999 }] });
    await debug.sessionEnded('first', true); expect(request).not.toHaveBeenCalledWith('agents/stop', expect.anything()); debug.dispose();
  });
  it('does not consume a failed same-name launch and expires its pending ownership with fake time', async () => {
    vi.useFakeTimers(); const backend = await stoppedBackend(); const list = await backend.request('tunnels/list', { workspace: 'example' }); const debug = new DebugHosting(backend); const failed = { name: 'App' }; const retry = { name: 'App' }; const settings = { configurations: [], stopAfterDebugging: true };
    await debug.beforeLaunch(failed, { tunnel: 'my-tunnel' }, 'example', [], list.tunnels, settings, true);
    await debug.beforeLaunch(retry, { tunnel: 'my-tunnel' }, 'example', list.agents, list.tunnels, settings, true);
    debug.sessionStarted('retry', retry); const request = vi.spyOn(backend, 'request'); await debug.sessionEnded('retry', true);
    expect(request).not.toHaveBeenCalledWith('agents/stop', expect.anything());
    await vi.advanceTimersByTimeAsync(120000);
    expect(request).toHaveBeenCalledWith('agents/stop', { workspace: 'example', name: 'my-tunnel', expectedProcessId: 1234 });
    debug.dispose(); expect(vi.getTimerCount()).toBe(0);
  });
  it.each([['Disconnected', false], ['Connected', false], ['Connected', undefined]] as const)('never owns a reused %s agent when created=%s', async (state, created) => {
    const backend = new MockBackend(); const list = await backend.request('tunnels/list', { workspace: 'example' }); const debug = new DebugHosting(backend); const config = { name: 'App' };
    const reused = { ...list.agents[0], state, created };
    const request = vi.spyOn(backend, 'request').mockResolvedValueOnce(reused);
    const launch = debug.beforeLaunch(config, { tunnel: 'my-tunnel' }, 'example', state === 'Disconnected' ? [reused] : [], list.tunnels, { configurations: [], stopAfterDebugging: true }, true);
    if (state === 'Disconnected') await expect(launch).rejects.toThrow('not connected'); else await launch;
    debug.sessionStarted('race', config); await debug.sessionEnded('race', true);
    expect(request).not.toHaveBeenCalledWith('agents/stop', expect.anything()); debug.dispose();
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
    expect(asCurl(detail, 'http://localhost:5173', { Test: "a'b" }, 'body', 'linux')).toContain("'Test: a'\\''b'");
    expect(asCurl(detail, 'http://localhost:5173', { Test: "a'b" }, 'body', 'win32')).toContain("'Test: a''b'");
  });
  it('finds C# tunnel literals and rate-limits error/disconnect notifications with fake time', () => {
    vi.useFakeTimers(); expect(tunnelLiterals('builder.WithGroundhogTunnel("my-tunnel");')).toEqual([{ name: 'my-tunnel', offset: 8 }]);
    const policy = new NotificationPolicy(); expect(policy.connection('t', 'connected', true)).toBe(false); expect(policy.connection('t', 'offline', true)).toBe(true);
    expect(policy.keyChanged('t')).toBe(false); vi.advanceTimersByTime(60000); expect(policy.request('t', 500, true)).toBe(true);
    vi.advanceTimersByTime(60000); expect(policy.request('t', 500, true)).toBe(false); vi.advanceTimersByTime(300000); expect(policy.request('t', 500, true)).toBe(true);
    expect(policy.request('other', 503, false)).toBe(false);
  });
});
