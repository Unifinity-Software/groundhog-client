import * as vscode from 'vscode';
import { randomBytes } from 'node:crypto';
import { Backend, RequestSummary, Tunnel } from './protocol';
import { StreamEvent, SubscriptionBuffer } from './subscription';

export class InspectorPanels implements vscode.Disposable {
  private readonly panels = new Map<string, vscode.WebviewPanel>();
  constructor(private readonly backend: Backend, private readonly extensionUri: vscode.Uri, private readonly onRequests: (tunnel: Tunnel, rows: RequestSummary[]) => void) {}
  async open(tunnel: Tunnel): Promise<void> {
    const key = `${tunnel.workspace}/${tunnel.name}`;
    const previous = this.panels.get(key); if (previous) { previous.reveal(); return; }
    const panel = vscode.window.createWebviewPanel('groundhog.requests', `Groundhog requests: ${tunnel.name}`, vscode.ViewColumn.Beside, { enableScripts: true, localResourceRoots: [vscode.Uri.joinPath(this.extensionUri, 'dist')] });
    this.panels.set(key, panel);
    let subscriptionId: string | undefined;
    let disposed = false;
    const rows: RequestSummary[] = [];
    const buffer = new SubscriptionBuffer();
    const post = (message: unknown) => { if (!disposed) void panel.webview.postMessage(message); };
    const deliver = (event: StreamEvent) => {
      if (event.type === 'connection') { post({ type: 'connection', ...event.value }); return; }
      const request = event.value.request;
      const previous = rows.findIndex(row => row.requestId === request.requestId); if (previous >= 0) rows.splice(previous, 1);
      rows.unshift(request); if (rows.length > 1000) rows.pop(); this.onRequests(tunnel, rows);
      post({ type: 'request', request });
    };
    const subscriptions = [
      this.backend.on('requests/event', event => buffer.receive({ type: 'request', value: event }).forEach(deliver)),
      this.backend.on('connection/event', event => buffer.receive({ type: 'connection', value: event }).forEach(deliver)),
      this.backend.on('backend/state', event => {
        if (event.state === 'ready') { subscriptionId = undefined; buffer.reset(); void subscribe(); }
        else post({ type: 'connection', state: event.state, message: event.message });
      })
    ];
    const subscribe = async () => {
      try {
        const result = await this.backend.request('requests/subscribe', { workspace: tunnel.workspace, name: tunnel.name, source: 'auto' });
        if (disposed) { await this.backend.request('requests/unsubscribe', { subscriptionId: result.subscriptionId }); return; }
        subscriptionId = result.subscriptionId; rows.splice(0, rows.length, ...result.requests); this.onRequests(tunnel, rows);
        post({ type: 'requests', requests: rows }); post({ type: 'connection', state: 'live', source: rows[0]?.source ?? 'local', demo: this.backend.capabilities.includes('demo') });
        buffer.bind(subscriptionId).forEach(deliver);
      } catch { buffer.reset(); post({ type: 'connection', state: 'stopped', message: 'Request stream unavailable. Close this panel and try again.' }); }
    };
    panel.webview.onDidReceiveMessage((message: { type?: string }) => {
      if (message?.type === 'ready') {
        if (subscriptionId) { post({ type: 'requests', requests: rows }); post({ type: 'connection', state: 'live', source: rows[0]?.source ?? 'local', demo: this.backend.capabilities.includes('demo') }); }
        else void subscribe();
      }
    });
    panel.onDidDispose(() => {
      disposed = true; this.panels.delete(key); subscriptions.forEach(s => s.dispose());
      if (subscriptionId) void this.backend.request('requests/unsubscribe', { subscriptionId }).catch(() => {});
    });
    const nonce = randomBytes(18).toString('base64');
    const script = panel.webview.asWebviewUri(vscode.Uri.joinPath(this.extensionUri, 'dist', 'inspector.js'));
    const style = panel.webview.asWebviewUri(vscode.Uri.joinPath(this.extensionUri, 'dist', 'inspector.css'));
    panel.webview.html = `<!doctype html><html lang="en"><head><meta charset="UTF-8"><meta name="viewport" content="width=device-width,initial-scale=1"><meta http-equiv="Content-Security-Policy" content="default-src 'none'; style-src ${panel.webview.cspSource}; script-src 'nonce-${nonce}';"><link rel="stylesheet" href="${style}"><title>Groundhog requests</title></head><body><main id="app"></main><script nonce="${nonce}" src="${script}"></script></body></html>`;
  }
  dispose(): void { for (const panel of this.panels.values()) panel.dispose(); this.panels.clear(); }
}
