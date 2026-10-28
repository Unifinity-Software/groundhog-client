import { Agent, AuthStatus, Backend, BackendEvents, Disposable, Methods, Notifications, RequestSummary, Tunnel, TunnelRef, VisitorAccess } from './protocol';

/** Scripted demo backend; never reads credentials or contacts the hosted service. */
export class MockBackend implements Backend {
  readonly capabilities = ['deviceLogin', 'loginPending', 'localAgents', 'liveRequests', 'requestDetail', 'requestReplay', 'requestExportHttp', 'captureBodies', 'rotateWithoutDowntime', 'visitorAccess', 'demo'];
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
  private captureBodies = false;
  private access: VisitorAccess = { default: { allow: ['members'] }, rules: [{ path: '/webhooks', allow: ['anyone'] }], ipRanges: [], apiKeys: [], managedBy: 'agent' };
  async start(): Promise<void> { if (this.disposed) throw new Error('Mock backend has stopped.'); }
  on<N extends keyof Notifications>(event: N, listener: (value: Notifications[N]) => void): Disposable { return this.events.on(event, listener); }
  async request<M extends keyof Methods>(method: M, params: Methods[M][0]): Promise<Methods[M][1]> {
    await this.start();
    // The switch validates/handles the public method contract; the final cast reconnects generic M to its result.
    const ref = params as TunnelRef;
    let result: unknown = {};
    switch (method) {
      case 'initialize': result = { protocolVersion: 1, cliVersion: '1.3.0-preview.2', capabilities: this.capabilities }; break;
      case 'auth/status': result = this.auth; break;
      case 'auth/login':
        result = { userCode: 'DEMO-CODE', verificationUri: 'https://groundhog.unifinity.com/cli-login', expiresAt: new Date(Date.now() + 600000).toISOString(), loginId: 'demo-login' };
        queueMicrotask(() => { this.auth = { state: 'signedIn', workspace: 'example', loginPending: false, loginId: 'demo-login', loginOutcome: 'approved' }; this.events.emit('auth/changed', this.auth); });
        break;
      case 'auth/logout':
        this.auth = { state: 'signedOut' };
        for (const timer of this.subscriptions.values()) clearInterval(timer);
        this.subscriptions.clear();
        this.events.emit('auth/changed', this.auth);
        break;
      case 'workspaces/list': result = { workspaces: [{ slug: 'example', name: 'Example', role: 'owner' }], selected: this.auth.workspace ?? null }; break;
      case 'workspaces/use': this.auth.workspace = (params as { workspace: string }).workspace; result = { workspace: this.auth.workspace }; this.events.emit('auth/changed', this.auth); break;
      case 'tunnels/list': result = { tunnels: this.tunnels.map(t => ({ ...t, target: this.agents.find(agent => agent.name === t.name)?.target ?? t.target, captureBodies: this.captureBodies, canCaptureBodies: true })), agents: this.agents }; break;
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
        const existing = this.agents.find(agent => agent.name === ref.name);
        if (existing && method === 'agents/start') {
          if ((params as Methods['agents/start'][0]).target && existing.target !== (params as Methods['agents/start'][0]).target) throw new Error('This tunnel is already hosted with another target.');
          result = { ...existing, created: false }; break;
        }
        const tunnel = this.findTunnel(ref); tunnel.state = 'connected'; tunnel.location = 'local';
        const agent: Agent = { name: ref.name, target: (params as Methods['agents/start'][0]).target ?? 'http://localhost:5173', url: tunnel.url, state: 'Connected', source: 'cli', processId: 1234 };
        tunnel.target = agent.target;
        this.agents = this.agents.filter(a => a.name !== ref.name).concat(agent); result = { ...agent, created: true };
        this.events.emit('agents/changed', { agents: this.agents }); break;
      }
      case 'agents/stop':
        if ((params as Methods['agents/stop'][0]).expectedProcessId !== undefined && !this.agents.some(agent => agent.name === ref.name && agent.processId === (params as Methods['agents/stop'][0]).expectedProcessId)) break;
        this.agents = this.agents.filter(a => a.name !== ref.name); this.findTunnel(ref).location = 'offline';
        this.events.emit('agents/changed', { agents: this.agents }); break;
      case 'keys/set': this.findTunnel(ref).hasKey = true; this.events.emit('tunnels/changed', { workspace: ref.workspace }); break;
      case 'requests/subscribe': {
        if (this.subscriptions.size >= 16) throw new Error('Too many subscriptions.');
        const subscriptionId = `demo-${++this.nextSubscription}`;
        this.subscriptions.set(subscriptionId, setInterval(() => this.events.emit('requests/event', { subscriptionId, request: this.generateRequest() }), 1000));
        result = { subscriptionId, requests: [{ ...this.generateRequest(), requestId: subscriptionId + '-truncated' }, { ...this.generateRequest(), requestId: subscriptionId + '-complete' }], source: ref.name === 'service-a' ? 'remote' : 'local' }; break;
      }
      case 'requests/unsubscribe': {
        const id = (params as { subscriptionId: string }).subscriptionId;
        clearInterval(this.subscriptions.get(id)); this.subscriptions.delete(id); break;
      }
      case 'doctor/run': result = { checks: [{ code: 'targetReachable', status: 'passed', message: 'Demo target responded.' }, { code: 'connectionKey', status: 'passed', message: 'A demo connection key is saved.' }] }; break;
      case 'requests/detail': result = { requestId: (params as Methods['requests/detail'][0]).requestId, method: 'POST', path: '/webhooks', headers: { 'Content-Type': 'application/json', 'Webhook-Signature': 'demo-signature' }, body: this.captureBodies ? '{"event":"demo.completed"}' : '', truncated: (params as Methods['requests/detail'][0]).requestId.endsWith('-truncated'), captureBodies: this.captureBodies, canCaptureBodies: true, responseHeaders: { 'Content-Type': 'application/json' }, responseBody: '{"ok":true}', statusCode: 200, responseTruncated: false }; break;
      case 'requests/replay': result = { statusCode: 200, durationMs: 12 }; break;
      case 'requests/exportHttp': result = { text: 'POST ' + (params as Methods['requests/exportHttp'][0]).target + '/webhooks\nContent-Type: application/json\n\n' + ((params as Methods['requests/exportHttp'][0]).body ?? '') }; break;
      case 'captureBodies/set': this.captureBodies = (params as { enabled: boolean }).enabled; result = { enabled: this.captureBodies }; break;
      case 'keys/rotateWithoutDowntime': this.events.emit('tunnels/changed', { workspace: ref.workspace }); break;
      case 'visitorAccess/get': result = this.access; break;
      case 'visitorAccess/set': { const draft = params as Methods['visitorAccess/set'][0]; this.access = { ...this.access, default: draft.default, rules: draft.rules, ipRanges: draft.ipRanges, managedBy: 'dashboard' }; result = this.access; break; }
      case 'visitorAccess/handBack': this.access.managedBy = 'agent'; result = this.access; break;
      case 'visitorAccess/createKey': { const key = { id: `demo-key-${this.access.apiKeys.length + 1}`, name: (params as Methods['visitorAccess/createKey'][0]).label, created: new Date().toISOString() }; this.access.apiKeys.push(key); result = { ...key, key: 'demo-visitor-key' }; break; }
      case 'visitorAccess/deleteKey': this.access.apiKeys = this.access.apiKeys.filter(key => key.id !== (params as Methods['visitorAccess/deleteKey'][0]).id); break;
      case 'visitorAccess/saveKey': break;
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
