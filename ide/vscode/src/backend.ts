import { spawn } from 'node:child_process';
import { CancellationTokenSource, createMessageConnection, MessageConnection, StreamMessageReader, StreamMessageWriter } from 'vscode-jsonrpc/node';
import { Backend, BackendEvents, Disposable, InitializeResult, Methods, Notifications } from './protocol';

export interface BackendSession { connection: MessageConnection; dispose(): void }
export type SessionFactory = () => BackendSession;
export function spawnSession(cliPath: string): BackendSession {
  const process = spawn(cliPath, ['ide', '--stdio'], { windowsHide: true, stdio: ['pipe', 'pipe', 'pipe'] });
  const connection = createMessageConnection(new StreamMessageReader(process.stdout), new StreamMessageWriter(process.stdin));
  // Never forward arbitrary backend diagnostics or protocol payloads to user-facing logs.
  process.stderr.resume();
  process.on('error', () => connection.dispose());
  process.on('exit', () => connection.dispose());
  return { connection, dispose: () => { connection.dispose(); process.stdin.end(); process.kill(); } };
}

export class ProtocolVersionError extends Error {
  constructor() { super('This Groundhog CLI uses an unsupported IDE protocol. Update Groundhog CLI and the extension.'); }
}

/** One process per extension host. Failed mutations are never replayed. */
export class RpcBackend implements Backend {
  capabilities: readonly string[] = [];
  private readonly events = new BackendEvents();
  private session?: BackendSession;
  private starting?: Promise<void>;
  private restartTimer?: ReturnType<typeof setTimeout>;
  private stopped = false;
  private attempts = 0;
  private connectedAt = 0;
  constructor(private readonly createSession: SessionFactory, private readonly version: string, private readonly timeoutMs = 15000) {}

  on<N extends keyof Notifications>(event: N, listener: (value: Notifications[N]) => void): Disposable { return this.events.on(event, listener); }
  start(): Promise<void> {
    if (this.stopped) return Promise.reject(new Error('Groundhog backend has stopped.'));
    if (this.starting) return this.starting;
    if (this.session) return Promise.resolve();
    this.starting = this.connect().finally(() => { this.starting = undefined; });
    return this.starting;
  }
  private async connect(): Promise<void> {
    const session = this.createSession();
    this.session = session;
    for (const event of ['auth/changed', 'tunnels/changed', 'agents/changed', 'requests/event', 'connection/event'] as const) {
      session.connection.onNotification(event, value => this.events.emit(event, value));
    }
    session.connection.onDispose(() => this.disconnected(session));
    session.connection.onClose(() => this.disconnected(session));
    session.connection.listen();
    try {
      const result = await this.send('initialize', { clientVersion: this.version, protocolVersions: [1] });
      if (result.protocolVersion !== 1) throw new ProtocolVersionError();
      this.capabilities = (result as InitializeResult).capabilities;
      this.connectedAt = Date.now();
      this.events.emit('backend/state', { state: 'ready' });
    } catch (error) {
      if (error instanceof ProtocolVersionError || (error as { code?: number }).code === -32001) {
        this.stopped = true;
        this.events.emit('backend/state', { state: 'stopped', message: new ProtocolVersionError().message });
        session.dispose();
        throw new ProtocolVersionError();
      }
      session.dispose();
      this.disconnected(session);
      // Deliberately exclude arbitrary backend payloads from errors exposed by the client.
      // eslint-disable-next-line preserve-caught-error
      throw new Error('Groundhog backend could not start. Check the CLI path and try again.');
    }
  }
  private disconnected(session: BackendSession): void {
    if (this.session !== session) return;
    this.session = undefined;
    this.capabilities = [];
    session.dispose();
    if (this.stopped) return;
    if (this.connectedAt && Date.now() - this.connectedAt > 30000) this.attempts = 0;
    this.events.emit('backend/state', { state: 'reconnecting', message: 'Groundhog backend disconnected. Reconnecting…' });
    const delay = Math.min(30000, 250 * 2 ** this.attempts++);
    this.restartTimer = setTimeout(() => { this.restartTimer = undefined; void this.start().catch(() => { /* connect schedules recovery */ }); }, delay);
  }
  async request<M extends keyof Methods>(method: M, params: Methods[M][0]): Promise<Methods[M][1]> {
    await this.start();
    return this.send(method, params);
  }
  private async send<M extends keyof Methods>(method: M, params: Methods[M][0]): Promise<Methods[M][1]> {
    const connection = this.session?.connection;
    if (!connection) throw new Error('Groundhog backend is reconnecting. Try again shortly.');
    const cancellation = new CancellationTokenSource();
    let timer: ReturnType<typeof setTimeout> | undefined;
    try {
      return await Promise.race([
        connection.sendRequest<Methods[M][1]>(method, params, cancellation.token),
        new Promise<never>((_, reject) => { timer = setTimeout(() => { cancellation.cancel(); reject(new Error('Groundhog request timed out. Try again.')); }, method === 'keys/rotateWithoutDowntime' ? Math.max(this.timeoutMs, 135000) : method === 'agents/start' || method === 'agents/restart' ? Math.max(this.timeoutMs, 45000) : this.timeoutMs); })
      ]);
    } finally { clearTimeout(timer); cancellation.dispose(); }
  }
  dispose(): void {
    this.stopped = true;
    clearTimeout(this.restartTimer);
    this.session?.dispose();
    this.session = undefined;
    this.events.dispose();
  }
}
