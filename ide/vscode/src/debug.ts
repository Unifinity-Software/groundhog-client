import { randomUUID } from 'node:crypto';
import { Agent, Backend, Disposable, Tunnel } from './protocol';

export interface ProjectConfig { tunnel: string; target?: string; projectDirectory?: string }
export interface DebugSettings { configurations: string[]; stopAfterDebugging: boolean }
export interface LaunchConfig { name?: string; type?: string; env?: Record<string, string>; __groundhogLaunchId?: string }
interface OwnedAgent { workspace: string; name: string; processId: number; sessions: Set<string>; pending: Set<string>; stopRequested: boolean }
interface PendingLaunch { owner: string; timer: ReturnType<typeof setTimeout> }

/** Correlate launches by an opaque per-configuration token, never by display name. */
export class DebugHosting implements Disposable {
  private readonly owned = new Map<string, OwnedAgent>();
  private readonly pending = new Map<string, PendingLaunch>();
  private readonly sessions = new Map<string, string>();
  constructor(private readonly backend: Backend, private readonly pendingTimeoutMs = 120000) {}
  async beforeLaunch(config: LaunchConfig, project: ProjectConfig | undefined, workspace: string, agents: Agent[], tunnels: Tunnel[], settings: DebugSettings, trusted: boolean): Promise<{ publicUrl?: string; missingKey?: Tunnel }> {
    if (!project || !trusted || (settings.configurations.length && !settings.configurations.includes(config.name ?? ''))) return {};
    const tunnel = tunnels.find(t => t.name === project.tunnel && t.workspace === workspace);
    const existing = agents.find(a => a.name === project.tunnel);
    if (!tunnel) return {};
    if (!existing && !tunnel.hasKey) return { missingKey: tunnel };
    const agent = existing?.state === 'Connected' ? existing : await this.backend.request('agents/start', { workspace, name: project.tunnel, target: tunnel.target ?? project.target ?? 'http://localhost:5173', ...(project.projectDirectory ? { projectDirectory: project.projectDirectory } : {}) });
    if (agent.state !== 'Connected') throw new Error('Groundhog has an existing local agent that is not connected. Restart hosting before debugging with a tunnel.');
    const ownerKey = workspace + '/' + project.tunnel + '/' + agent.processId;
    let owner = this.owned.get(ownerKey);
    if (agent.created === true) {
      owner ??= { workspace, name: project.tunnel, processId: agent.processId, sessions: new Set(), pending: new Set(), stopRequested: false };
      this.owned.set(ownerKey, owner);
    }
    config.__groundhogLaunchId = randomUUID();
    if (owner && agent.source !== 'aspire') {
      const id = config.__groundhogLaunchId;
      owner.pending.add(id);
      const timer = setTimeout(() => { void this.expire(id).catch(() => {}); }, this.pendingTimeoutMs);
      timer.unref?.();
      this.pending.set(id, { owner: ownerKey, timer });
      // Keep failed-launch metadata bounded even if an adapter never starts or terminates.
      if (this.pending.size > 64) await this.expire(this.pending.keys().next().value!);
    }
    const publicUrl = agent.url ?? tunnel.url;
    if (['coreclr', 'node', 'pwa-node'].includes(config.type ?? '')) config.env = { ...config.env, GROUNDHOG_PUBLIC_URL: publicUrl };
    return { publicUrl };
  }
  sessionStarted(id: string, configuration: LaunchConfig): void {
    const launch = configuration.__groundhogLaunchId;
    if (!launch) return;
    const pending = this.pending.get(launch);
    if (!pending) return;
    clearTimeout(pending.timer); this.pending.delete(launch);
    const owner = this.owned.get(pending.owner); if (!owner) return;
    owner.pending.delete(launch); owner.sessions.add(id); this.sessions.set(id, pending.owner);
  }
  async sessionEnded(id: string, stopAfterDebugging: boolean, configuration?: LaunchConfig): Promise<void> {
    let ownerKey = this.sessions.get(id); this.sessions.delete(id);
    const launch = configuration?.__groundhogLaunchId;
    const pending = launch ? this.pending.get(launch) : undefined;
    if (pending && launch) {
      clearTimeout(pending.timer); this.pending.delete(launch);
      this.owned.get(pending.owner)?.pending.delete(launch); ownerKey ??= pending.owner;
    }
    if (!ownerKey) return;
    const owner = this.owned.get(ownerKey); if (!owner) return;
    owner.sessions.delete(id); owner.stopRequested = stopAfterDebugging;
    await this.finish(ownerKey);
  }
  private async expire(id: string): Promise<void> {
    const pending = this.pending.get(id); if (!pending) return;
    clearTimeout(pending.timer); this.pending.delete(id);
    this.owned.get(pending.owner)?.pending.delete(id);
    await this.finish(pending.owner);
  }
  private async finish(key: string): Promise<void> {
    const owner = this.owned.get(key);
    if (!owner || owner.sessions.size || owner.pending.size) return;
    this.owned.delete(key);
    if (!owner.stopRequested) return;
    const current = await this.backend.request('tunnels/list', { workspace: owner.workspace });
    if (!current.agents.some(agent => agent.name === owner.name && agent.processId === owner.processId && agent.source === 'cli')) return;
    await this.backend.request('agents/stop', { workspace: owner.workspace, name: owner.name, expectedProcessId: owner.processId });
  }
  dispose(): void { for (const pending of this.pending.values()) clearTimeout(pending.timer); this.pending.clear(); this.sessions.clear(); this.owned.clear(); }
}
