import { PassThrough } from 'node:stream';
import { createMessageConnection, MessageConnection, StreamMessageReader, StreamMessageWriter } from 'vscode-jsonrpc/node';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { RpcBackend, ProtocolVersionError } from '../src/backend';
import { MockBackend } from '../src/mockBackend';
import { Methods } from '../src/protocol';

const disposables: { dispose(): void }[] = [];
afterEach(() => { disposables.splice(0).reverse().forEach(d => d.dispose()); vi.useRealTimers(); });
function wire(serverSetup: (connection: MessageConnection) => void) {
  const incoming = new PassThrough(); const outgoing = new PassThrough();
  const server = createMessageConnection(new StreamMessageReader(incoming), new StreamMessageWriter(outgoing));
  const connection = createMessageConnection(new StreamMessageReader(outgoing), new StreamMessageWriter(incoming));
  serverSetup(server); server.listen();
  const session = { connection, dispose: () => { connection.dispose(); server.dispose(); incoming.destroy(); outgoing.destroy(); } };
  disposables.push(session);
  return { session, server };
}

describe('typed RPC backend', () => {
  it('exchanges requests and notifications over Content-Length streams with the mock', async () => {
    const mock = new MockBackend(); disposables.push(mock);
    const { session, server } = wire(server => {
      server.onRequest(async (method, params) => {
        if (method === 'initialize') expect(params).toEqual({ clientVersion: '0.1.0', protocolVersions: [1], clientName: 'Groundhog for VS Code' });
        return mock.request(method as keyof Methods, (params ?? {}) as Methods[keyof Methods][0]);
      });
      mock.on('auth/changed', value => { void server.sendNotification('auth/changed', value); });
    });
    const backend = new RpcBackend(() => session, '0.1.0'); disposables.push(backend);
    await backend.start();
    expect(backend.capabilities).toContain('liveRequests');
    expect((await backend.request('tunnels/list', { workspace: 'example' })).tunnels.map(t => t.location)).toEqual(['local', 'remote', 'offline']);
    const changed = new Promise(resolve => backend.on('auth/changed', resolve));
    await backend.request('auth/logout', {});
    expect(await changed).toEqual({ state: 'signedOut' });
    expect(server).toBeDefined();
  });
  it('restarts with exponential backoff and stops recovery after disposal', async () => {
    vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout', 'Date'] });
    const sessions: ReturnType<typeof wire>[] = [];
    const create = vi.fn(() => {
      const pair = wire(server => server.onRequest('initialize', () => ({ protocolVersion: 1, cliVersion: '1.2.0', capabilities: [] })));
      sessions.push(pair); return pair.session;
    });
    const backend = new RpcBackend(create, '0.1.0'); disposables.push(backend);
    await backend.start();
    sessions[0].session.connection.dispose();
    await vi.advanceTimersByTimeAsync(249); expect(create).toHaveBeenCalledTimes(1);
    await vi.advanceTimersByTimeAsync(1); await backend.start(); expect(create).toHaveBeenCalledTimes(2);
    sessions[1].session.connection.dispose();
    await vi.advanceTimersByTimeAsync(499); expect(create).toHaveBeenCalledTimes(2);
    await vi.advanceTimersByTimeAsync(1); await backend.start(); expect(create).toHaveBeenCalledTimes(3);
    backend.dispose(); await vi.advanceTimersByTimeAsync(60000); expect(create).toHaveBeenCalledTimes(3);
  });
  it('reports protocol mismatch clearly without a restart loop', async () => {
    vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout'] });
    const create = vi.fn(() => wire(server => server.onRequest('initialize', () => ({ protocolVersion: 2, cliVersion: '2.0.0', capabilities: [] }))).session);
    const backend = new RpcBackend(create, '0.1.0'); disposables.push(backend);
    await expect(backend.start()).rejects.toBeInstanceOf(ProtocolVersionError);
    await vi.advanceTimersByTimeAsync(60000); expect(create).toHaveBeenCalledTimes(1);
  });
  it('cancels requests on timeout instead of retrying mutations', async () => {
    vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout'] });
    let begin!: () => void; let cancelled!: () => void;
    const started = new Promise<void>(resolve => { begin = resolve; });
    const cancellation = new Promise<void>(resolve => { cancelled = resolve; });
    const { session } = wire(server => {
      server.onRequest('initialize', () => ({ protocolVersion: 1, cliVersion: '1.2.0', capabilities: [] }));
      server.onRequest('tunnels/create', (_, token) => new Promise(resolve => { begin(); token.onCancellationRequested(() => { cancelled(); resolve({}); }); }));
    });
    const backend = new RpcBackend(() => session, '0.1.0', 100); disposables.push(backend); await backend.start();
    const request = backend.request('tunnels/create', { workspace: 'example', name: 'new' });
    const rejection = expect(request).rejects.toThrow('timed out');
    await started;
    await vi.advanceTimersByTimeAsync(100); await rejection;
    await cancellation;
  });
});

it('mock request generator stops on unsubscribe and dispose using fake time', async () => {
  vi.useFakeTimers(); const mock = new MockBackend(); disposables.push(mock);
  const listener = vi.fn(); mock.on('requests/event', listener);
  const { subscriptionId, requests } = await mock.request('requests/subscribe', { workspace: 'example', name: 'my-tunnel' });
  expect(requests).toHaveLength(2);
  await vi.advanceTimersByTimeAsync(3000); expect(listener).toHaveBeenCalledTimes(3);
  await mock.request('requests/unsubscribe', { subscriptionId });
  await vi.advanceTimersByTimeAsync(10000); expect(listener).toHaveBeenCalledTimes(3);
  mock.dispose(); expect(vi.getTimerCount()).toBe(0);
});
