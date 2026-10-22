import { Agent, Backend, Tunnel } from './protocol';

export interface ProjectConfig { tunnel: string; target?: string; projectDirectory?: string }
export interface DebugSettings { configurations: string[]; stopAfterDebugging: boolean }
export class DebugHosting {
  private readonly owned = new Map<string, { workspace: string; name: string; sessions: Set<string> }>();
  private readonly pending = new Map<string, { workspace: string; name: string; started: boolean }[]>();
  constructor(private readonly backend: Backend) {}
  async beforeLaunch(config: { name?: string; type?: string; env?: Record<string, string> }, project: ProjectConfig | undefined, workspace: string, agents: Agent[], tunnels: Tunnel[], settings: DebugSettings, trusted: boolean): Promise<{ publicUrl?: string; missingKey?: boolean }> {
    if (!project || !trusted || (settings.configurations.length && !settings.configurations.includes(config.name ?? ''))) return {};
    const tunnel = tunnels.find(t => t.name === project.tunnel && t.workspace === workspace);
    const existing = agents.find(a => a.name === project.tunnel && a.state === 'Connected');
    if (!tunnel || (!existing && !tunnel.hasKey)) return { missingKey: true };
    const agent = existing ?? await this.backend.request('agents/start', { workspace, name: project.tunnel, target: project.target ?? 'http://localhost:5173', ...(project.projectDirectory ? { projectDirectory: project.projectDirectory } : {}) });
    const pending = this.pending.get(config.name ?? '') ?? [];
    pending.push({ workspace, name: project.tunnel, started: !existing }); this.pending.set(config.name ?? '', pending);
    const publicUrl = agent.url ?? tunnel.url;
    if (['coreclr', 'node', 'pwa-node'].includes(config.type ?? '')) config.env = { ...config.env, GROUNDHOG_PUBLIC_URL: publicUrl };
    return { publicUrl };
  }
  sessionStarted(id: string, name: string): void {
    const queue = this.pending.get(name); const pending = queue?.shift(); if (!pending) return;
    const key = `${pending.workspace}/${pending.name}`;
    const owned = this.owned.get(key) ?? { workspace: pending.workspace, name: pending.name, sessions: new Set<string>() };
    if (pending.started || this.owned.has(key)) { owned.sessions.add(id); this.owned.set(key, owned); }
    if (!queue?.length) this.pending.delete(name);
  }
  async sessionEnded(id: string, stopAfterDebugging: boolean): Promise<void> {
    for (const [key, owned] of this.owned) {
      if (!owned.sessions.delete(id) || owned.sessions.size) continue;
      if ([...this.pending.values()].flat().some(pending => pending.workspace === owned.workspace && pending.name === owned.name)) continue;
      this.owned.delete(key);
      if (stopAfterDebugging) await this.backend.request('agents/stop', { workspace: owned.workspace, name: owned.name });
    }
  }
}
