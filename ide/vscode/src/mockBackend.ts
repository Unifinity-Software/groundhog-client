import { Agent, AuthStatus, Backend, BackendEvents, Disposable, Methods, Notifications, RequestSummary, Tunnel, TunnelRef } from './protocol';

/** Scripted demo backend; never reads credentials or contacts the hosted service. */
export class MockBackend implements Backend {
  readonly capabilities = ['deviceLogin', 'localAgents', 'liveRequests'];
  private readonly events = new BackendEvents();
  private auth: AuthStatus = { state: 'signedIn', workspace: 'example' };
  private tunnels: Tunnel[] = ['my-tunnel', 'service-a', 'service-b'].map((name, i) => ({
    workspace: 'example', name, url: `https://${name}.groundhog.unifinity.com`,
    state: i < 2 ? 'connected' : 'disconnected', location: (['local', 'remote', 'offline'] as const)[i], actions: ['rename', 'delete'], hasKey: i !== 1
  }));
  private agents: Agent[] = [{ name: 'my-tunnel', target: 'http://localhost:5173', url: 'https://my-tunnel.groundhog.unifinity.com', state: 'Connected', source: 'cli', processId: 1234 }];
  private readonly subscriptions = new Map<string, ReturnType<typeof setInterval>>();
  private nextSubscription = 0;
  private nextRequest = 0;
  private disposed = false;
  async start(): Promise<void> { if (this.disposed) throw new Error('Mock backend has stopped.'); }
  on<N extends keyof Notifications>(event: N, listener: (value: Notifications[N]) => void): Disposable { return this.events.on(event, listener); }
  async request<M extends keyof Methods>(method: M, params: Methods[M][0]): Promise<Methods[M][1]> {
    await this.start();
    // The switch validates/handles the public method contract; the final cast reconnects generic M to its result.
    const ref = params as TunnelRef;
    let result: unknown = {};
    switch (method) {
      case 'initialize': result = { protocolVersion: 1, cliVersion: '1.2.0', capabilities: this.capabilities }; break;
      case 'auth/status': result = this.auth; break;
      case 'auth/login':
        result = { userCode: 'DEMO-CODE', verificationUri: 'https://groundhog.unifinity.com/cli-login', expiresAt: new Date(Date.now() + 600000).toISOString() };
        queueMicrotask(() => { this.auth = { state: 'signedIn', workspace: 'example' }; this.events.emit('auth/changed', this.auth); });
        break;
      case 'auth/logout':
        this.auth = { state: 'signedOut' };
        for (const timer of this.subscriptions.values()) clearInterval(timer);
        this.subscriptions.clear();
        this.events.emit('auth/changed', this.auth);
        break;
      case 'workspaces/list': result = { workspaces: [{ slug: 'example', name: 'Example', role: 'owner' }], selected: this.auth.workspace ?? null }; break;
      case 'workspaces/use': this.auth.workspace = (params as { workspace: string }).workspace; result = { workspace: this.auth.workspace }; this.events.emit('auth/changed', this.auth); break;
      case 'tunnels/list': result = { tunnels: this.tunnels, agents: this.agents }; break;
      case 'tunnels/create':
        if (this.tunnels.some(t => t.name === ref.name)) throw new Error('A tunnel with this name already exists.');
        result = { ...ref, url: `https://${ref.name}.groundhog.unifinity.com`, state: 'disconnected', location: 'offline', hasKey: true, actions: ['rename', 'delete'] };
        this.tunnels.push(result as Tunnel); this.events.emit('tunnels/changed', { workspace: ref.workspace }); break;
      case 'tunnels/rename': {
        const { newName, confirm } = params as Methods['tunnels/rename'][0];
        if (!confirm) throw new Error('Confirm the rename first.');
        const tunnel = this.findTunnel(ref);
        tunnel.name = newName; tunnel.url = `https://${newName}.groundhog.unifinity.com`;
        result = tunnel; this.events.emit('tunnels/changed', { workspace: ref.workspace }); break;
      }
      case 'tunnels/delete':
        if (!(params as Methods['tunnels/delete'][0]).confirm) throw new Error('Confirm deletion first.');
        this.tunnels = this.tunnels.filter(t => t.name !== ref.name); this.events.emit('tunnels/changed', { workspace: ref.workspace }); break;
      case 'agents/start': case 'agents/restart': {
        const tunnel = this.findTunnel(ref); tunnel.state = 'connected'; tunnel.location = 'local';
        const agent: Agent = { name: ref.name, target: (params as Methods['agents/start'][0]).target, url: tunnel.url, state: 'Connected', source: 'cli', processId: 1234 };
        this.agents = this.agents.filter(a => a.name !== ref.name).concat(agent); result = agent;
        this.events.emit('agents/changed', { agents: this.agents }); break;
      }
      case 'agents/stop':
        this.agents = this.agents.filter(a => a.name !== ref.name); this.findTunnel(ref).location = 'offline';
        this.events.emit('agents/changed', { agents: this.agents }); break;
      case 'keys/set': this.findTunnel(ref).hasKey = true; this.events.emit('tunnels/changed', { workspace: ref.workspace }); break;
      case 'requests/subscribe': {
        if (this.subscriptions.size >= 16) throw new Error('Too many subscriptions.');
        const subscriptionId = `demo-${++this.nextSubscription}`;
        this.subscriptions.set(subscriptionId, setInterval(() => this.events.emit('requests/event', { subscriptionId, request: this.generateRequest() }), 1000));
        result = { subscriptionId, requests: [this.generateRequest(), this.generateRequest()] }; break;
      }
      case 'requests/unsubscribe': {
        const id = (params as { subscriptionId: string }).subscriptionId;
        clearInterval(this.subscriptions.get(id)); this.subscriptions.delete(id); break;
      }
      case 'doctor/run': result = { checks: [{ code: 'targetReachable', status: 'passed', message: 'Demo target responded.' }, { code: 'connectionKey', status: 'passed', message: 'A demo connection key is saved.' }] }; break;
      default: throw new Error('This demo does not support that operation.');
    }
    return structuredClone(result) as Methods[M][1];
  }
  private findTunnel(ref: TunnelRef): Tunnel {
    const tunnel = this.tunnels.find(t => t.workspace === ref.workspace && t.name === ref.name);
    if (!tunnel) throw new Error('Tunnel not found.');
    return tunnel;
  }
  private generateRequest(): RequestSummary {
    const n = ++this.nextRequest;
    return { requestId: `demo-request-${n}`, completed: new Date().toISOString(), method: n % 3 ? 'GET' : 'POST', path: n % 3 ? '/api/health' : '/webhooks', statusCode: n % 7 ? 200 : 500, durationMs: 8 + (n * 7) % 120, source: 'local' };
  }
  dispose(): void { this.disposed = true; for (const timer of this.subscriptions.values()) clearInterval(timer); this.subscriptions.clear(); this.events.dispose(); }
}
