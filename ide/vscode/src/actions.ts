import { Agent, Backend, Tunnel } from './protocol';

export interface ActionUI {
  input(title: string, value?: string, password?: boolean): Promise<string | undefined>;
  confirm(message: string): Promise<boolean>;
  trust(): Promise<boolean>;
  open(url: string): Promise<void>;
  copy(value: string): Promise<void>;
  target(tunnel: string): Promise<string | undefined>;
  projectDirectory?(): string | undefined;
}
export function hostedUrl(value: string): boolean {
  try {
    const url = new URL(value);
    return url.protocol === 'https:' && !url.username && !url.password && !url.port &&
      (url.hostname === 'groundhog.unifinity.com' || /^[a-z0-9](?:[a-z0-9-]*[a-z0-9])?\.groundhog\.unifinity\.com$/.test(url.hostname));
  } catch { return false; }
}
export class TunnelActions {
  constructor(private readonly backend: Backend, private readonly ui: ActionUI) {}
  async start(tunnel: Tunnel, restart = false): Promise<Agent | undefined> {
    if (!await this.ui.trust()) return;
    if (!tunnel.hasKey && !await this.enterKey(tunnel)) return;
    const target = await this.ui.input('Local app URL', await this.ui.target(tunnel.name) ?? 'http://localhost:5173');
    if (!target) return;
    const projectDirectory = this.ui.projectDirectory?.();
    return this.backend.request(restart ? 'agents/restart' : 'agents/start', { workspace: tunnel.workspace, name: tunnel.name, target, ...(projectDirectory ? { projectDirectory } : {}) });
  }
  async stop(tunnel: Tunnel): Promise<void> { await this.backend.request('agents/stop', this.ref(tunnel)); }
  async open(tunnel: Tunnel): Promise<void> { if (!hostedUrl(tunnel.url)) throw new Error('Groundhog returned an invalid public URL.'); await this.ui.open(tunnel.url); }
  async copy(tunnel: Tunnel): Promise<void> { if (!hostedUrl(tunnel.url)) throw new Error('Groundhog returned an invalid public URL.'); await this.ui.copy(tunnel.url); }
  async enterKey(tunnel: Tunnel): Promise<boolean> {
    const key = await this.ui.input(`Connection key for ${tunnel.name}`, undefined, true);
    if (!key) return false;
    await this.backend.request('keys/set', { ...this.ref(tunnel), key }); tunnel.hasKey = true; return true;
  }
  async rename(tunnel: Tunnel): Promise<void> {
    const name = await this.ui.input('New tunnel name', tunnel.name);
    if (!name || name === tunnel.name || !await this.ui.confirm(`Rename ${tunnel.name} to ${name}? Its public URL changes and running agents may disconnect.`)) return;
    await this.backend.request('tunnels/rename', { ...this.ref(tunnel), newName: name, confirm: true });
  }
  async delete(tunnel: Tunnel): Promise<void> {
    if (await this.ui.confirm(`Delete ${tunnel.name}? Its public URL will stop serving traffic.`)) await this.backend.request('tunnels/delete', { ...this.ref(tunnel), confirm: true });
  }
  async create(workspace: string): Promise<void> {
    const name = await this.ui.input('New tunnel name'); if (name) await this.backend.request('tunnels/create', { workspace, name });
  }
  private ref(tunnel: Tunnel) { return { workspace: tunnel.workspace, name: tunnel.name }; }
}
export function tunnelLocation(tunnel: Tunnel, agents: Agent[]): 'local' | 'remote' | 'offline' {
  if (agents.some(a => a.name === tunnel.name && ['Connected', 'Reconnecting', 'Connecting'].includes(a.state))) return 'local';
  return tunnel.location ?? (tunnel.state.toLowerCase() === 'connected' ? 'remote' : 'offline');
}
