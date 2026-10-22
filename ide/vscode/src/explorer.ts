import * as vscode from 'vscode';
import { Agent, AuthStatus, Backend, Tunnel, Workspace } from './protocol';
import { tunnelLocation } from './actions';

export type ExplorerNode = { kind: 'workspace'; workspace: Workspace } | { kind: 'tunnel'; tunnel: Tunnel };
export class TunnelExplorer implements vscode.TreeDataProvider<ExplorerNode>, vscode.Disposable {
  private readonly changes = new vscode.EventEmitter<ExplorerNode | undefined>();
  readonly onDidChangeTreeData = this.changes.event;
  readonly updated = new vscode.EventEmitter<void>();
  workspaces: Workspace[] = [];
  workspace = '';
  tunnels: Tunnel[] = [];
  agents: Agent[] = [];
  auth: AuthStatus = { state: 'signedOut' };
  private readonly subscriptions;
  private refreshing?: Promise<void>;
  private readonly rates = new Map<string, number>();
  setRequestRate(name: string, count: number): void { this.rates.set(name, count); this.changes.fire(undefined); }
  constructor(private readonly backend: Backend) {
    this.subscriptions = [backend.on('auth/changed', () => { void this.refresh().catch(() => {}); }), backend.on('tunnels/changed', () => { void this.refresh().catch(() => {}); }), backend.on('agents/changed', event => { this.agents = event.agents; this.changes.fire(undefined); this.updated.fire(); }), backend.on('backend/state', event => { if (event.state === 'ready') void this.refresh().catch(() => {}); })];
  }
  refresh(): Promise<void> {
    if (this.refreshing) return this.refreshing;
    this.refreshing = this.load().finally(() => { this.refreshing = undefined; }); return this.refreshing;
  }
  private async load(): Promise<void> {
    this.auth = await this.backend.request('auth/status', {});
    await vscode.commands.executeCommand('setContext', 'groundhog.signedIn', this.auth.state === 'signedIn');
    if (this.auth.state !== 'signedIn') { this.workspaces = []; this.tunnels = []; this.agents = []; this.workspace = ''; }
    else {
      const list = await this.backend.request('workspaces/list', {}); this.workspaces = list.workspaces;
      this.workspace = list.selected ?? list.workspaces[0]?.slug ?? '';
      if (this.workspace) {
        const state = await this.backend.request('tunnels/list', { workspace: this.workspace }); this.tunnels = state.tunnels; this.agents = state.agents;
      }
    }
    this.changes.fire(undefined); this.updated.fire();
  }
  getChildren(node?: ExplorerNode): ExplorerNode[] {
    if (!node) return this.workspaces.filter(w => w.slug === this.workspace).map(workspace => ({ kind: 'workspace', workspace }));
    return node.kind === 'workspace' ? this.tunnels.map(tunnel => ({ kind: 'tunnel', tunnel })) : [];
  }
  getTreeItem(node: ExplorerNode): vscode.TreeItem {
    if (node.kind === 'workspace') { const item = new vscode.TreeItem(node.workspace.name, vscode.TreeItemCollapsibleState.Expanded); item.description = node.workspace.slug; item.contextValue = 'workspace'; return item; }
    const tunnel = node.tunnel; const location = tunnelLocation(tunnel, this.agents);
    const symbol = { local: '●', remote: '◐', offline: '○' }[location];
    const item = new vscode.TreeItem(`${symbol} ${tunnel.name}`, vscode.TreeItemCollapsibleState.None);
    item.description = tunnel.url; item.tooltip = `${tunnel.name} · ${location} · ${this.rates.get(tunnel.name) ?? 0} req/min\n${tunnel.url}`;
    item.contextValue = `tunnel.${location}${this.agents.some(a => a.name === tunnel.name && a.source === 'aspire') ? '.aspire' : ''}${tunnel.actions.includes('rename') ? '.rename' : ''}${tunnel.actions.includes('delete') ? '.delete' : ''}`;
    item.command = { command: 'groundhog.requests', title: 'Inspect requests', arguments: [node] };
    return item;
  }
  dispose(): void { this.subscriptions.forEach(s => s.dispose()); this.changes.dispose(); this.updated.dispose(); }
}
