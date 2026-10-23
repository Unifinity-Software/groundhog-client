import * as vscode from 'vscode';
import { Backend, Tunnel, VisitorAccess } from './protocol';
import { webviewHtml } from './webview';

export function accessDraft(value: unknown): { default: { allow: string[] }; rules: VisitorAccess['rules']; ipRanges: string[] } {
  if (!value || typeof value !== 'object') throw new Error('Enter visitor access rules before saving.');
  const draft = value as { default?: { allow?: unknown }; rules?: unknown; ipRanges?: unknown };
  const allow = (input: unknown): input is string[] => Array.isArray(input) && input.length > 0 && input.length <= 4 && input.every(item => ['anyone', 'members', 'apikeys', 'ips'].includes(item)) && (!input.includes('anyone') || input.length === 1);
  if (!allow(draft.default?.allow) || !Array.isArray(draft.rules) || draft.rules.length > 32 || !Array.isArray(draft.ipRanges) || draft.ipRanges.length > 64 || !draft.ipRanges.every(item => typeof item === 'string' && item.length <= 64) || !draft.rules.every(rule => rule && typeof rule.path === 'string' && rule.path.startsWith('/') && rule.path.length <= 2048 && allow(rule.allow))) throw new Error('Check the allowed visitors, path rules and IP ranges before saving.');
  return { default: { allow: draft.default.allow }, rules: draft.rules, ipRanges: draft.ipRanges };
}

export class VisitorAccessPanels implements vscode.Disposable {
  private readonly panels = new Set<vscode.WebviewPanel>();
  constructor(private readonly backend: Backend, private readonly extensionUri: vscode.Uri) {}
  async open(tunnel: Tunnel): Promise<void> {
    if (!this.backend.capabilities.includes('visitorAccess')) throw new Error('Visitor access editing is not available in this Groundhog CLI or workspace.');
    const panel = vscode.window.createWebviewPanel('groundhog.access', `Groundhog access: ${tunnel.name}`, vscode.ViewColumn.Beside, { enableScripts: true, localResourceRoots: [vscode.Uri.joinPath(this.extensionUri, 'dist')] });
    this.panels.add(panel); panel.onDidDispose(() => this.panels.delete(panel));
    const ref = { workspace: tunnel.workspace, name: tunnel.name };
    const load = async () => { await panel.webview.postMessage({ type: 'access', access: await this.backend.request('visitorAccess/get', ref) }); };
    panel.webview.onDidReceiveMessage(async (message: { type?: string; label?: unknown; id?: unknown }) => {
      try {
        if (message?.type === 'ready') await load();
        if (message?.type === 'save') {
          const draft = accessDraft(message);
          if (await vscode.window.showWarningMessage(`Replace visitor access rules for ${tunnel.name}? These rules control who can reach the public URL.`, { modal: true }, 'Save rules') !== 'Save rules') return;
          await this.backend.request('visitorAccess/set', { ...ref, ...draft, confirm: true }); await load();
          await panel.webview.postMessage({ type: 'operation', message: 'Access rules saved.' });
        }
        if (message?.type === 'handBack') {
          if (await vscode.window.showWarningMessage(`Allow agents to replace the visitor access rules for ${tunnel.name}?`, { modal: true }, 'Hand control back') !== 'Hand control back') return;
          await this.backend.request('visitorAccess/handBack', { ...ref, confirm: true }); await load();
        }
        if (message?.type === 'deleteKey' && typeof message.id === 'string') {
          if (await vscode.window.showWarningMessage('Revoke this visitor API key? Requests using it will be denied.', { modal: true }, 'Revoke') !== 'Revoke') return;
          await this.backend.request('visitorAccess/deleteKey', { ...ref, id: message.id, confirm: true }); await load();
        }
        if (message?.type === 'createKey' && typeof message.label === 'string' && message.label.trim() && message.label.length <= 100) {
          const result = await this.backend.request('visitorAccess/createKey', { ...ref, label: message.label.trim(), confirm: true });
          await this.presentKey(tunnel, result.key); await load();
        }
      } catch (error) { await panel.webview.postMessage({ type: 'operation', message: (error as Error).message }); }
    });
    panel.webview.html = webviewHtml(panel.webview, this.extensionUri, 'access.js');
  }
  private async presentKey(tunnel: Tunnel, key: string): Promise<void> {
    const choice = await vscode.window.showInformationMessage('Your visitor API key is shown once. Copy it or save it now.', { modal: true, detail: key }, 'Copy key', 'Save to user secrets');
    if (choice === 'Copy key') await vscode.env.clipboard.writeText(key);
    if (choice === 'Save to user secrets') {
      if (!vscode.workspace.isTrusted) throw new Error('Trust this workspace before saving a visitor API key to user secrets.');
      const projects = await vscode.workspace.findFiles('**/*.csproj', '**/{bin,obj,node_modules}/**', 50);
      const selected = projects.length === 1 ? projects[0] : (await vscode.window.showQuickPick(projects.map(uri => ({ label: vscode.workspace.asRelativePath(uri), uri })), { placeHolder: 'Choose the .NET project for user secrets' }))?.uri;
      if (!selected) { await vscode.env.clipboard.writeText(key); void vscode.window.showInformationMessage('No project was selected. The API key was copied to the clipboard.'); return; }
      try { await this.backend.request('visitorAccess/saveKey', { workspace: tunnel.workspace, name: tunnel.name, project: selected.fsPath, parameter: 'groundhog-api-key', key, confirm: true }); }
      catch (error) { await vscode.env.clipboard.writeText(key); void vscode.window.showWarningMessage((error as Error).message + ' The API key was copied to the clipboard.'); }
    }
  }
  dispose(): void { for (const panel of this.panels) panel.dispose(); this.panels.clear(); }
}
