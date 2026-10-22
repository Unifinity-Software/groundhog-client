import * as vscode from 'vscode';
import { Backend, RequestSummary, Tunnel } from './protocol';
import { DetailSelection } from './detailSelection';
import { StreamEvent, SubscriptionBuffer } from './subscription';
import { asCurl, parseHeaders, RequestActions, signatureWarning } from './requestActions';
import { toDataURL } from 'qrcode';
import { webviewHtml } from './webview';

export class InspectorPanels implements vscode.Disposable {
  private readonly panels = new Map<string, vscode.WebviewPanel>();
  constructor(private readonly backend: Backend, private readonly extensionUri: vscode.Uri, private readonly onRequests: (tunnel: Tunnel, rows: RequestSummary[]) => void, private readonly target: (name: string) => Promise<string | undefined> = async () => undefined) {}
  async open(tunnel: Tunnel): Promise<void> {
    const key = `${tunnel.workspace}/${tunnel.name}`;
    const previous = this.panels.get(key); if (previous) { previous.reveal(); return; }
    const panel = vscode.window.createWebviewPanel('groundhog.requests', `Groundhog requests: ${tunnel.name}`, vscode.ViewColumn.Beside, { enableScripts: true, localResourceRoots: [vscode.Uri.joinPath(this.extensionUri, 'dist')] });
    this.panels.set(key, panel);
    let subscriptionId: string | undefined;
    let subscribing: Promise<void> | undefined;
    let generation = 0;
    let source: 'local' | 'remote' | undefined;
    let disposed = false;
    const rows: RequestSummary[] = [];
    const selection = new DetailSelection(requestId => this.backend.request('requests/detail', { workspace: tunnel.workspace, name: tunnel.name, requestId }), () => this.target(tunnel.name));
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
        if (event.state === 'ready') { generation++; subscriptionId = undefined; buffer.reset(); void subscribe(); }
        else post({ type: 'connection', state: event.state, message: event.message });
      })
    ];
    const subscribe = (): Promise<void> => {
      if (disposed) return Promise.resolve();
      if (subscribing) return subscribing;
      const startedGeneration = generation;
      subscribing = loadSubscription(startedGeneration).finally(() => { subscribing = undefined; if (!disposed && generation !== startedGeneration) void subscribe(); });
      return subscribing;
    };
    const loadSubscription = async (startedGeneration: number) => {
      try {
        const result = await this.backend.request('requests/subscribe', { workspace: tunnel.workspace, name: tunnel.name, source: 'auto' });
        if (disposed || generation !== startedGeneration) { await this.backend.request('requests/unsubscribe', { subscriptionId: result.subscriptionId }); return; }
        subscriptionId = result.subscriptionId; source = result.source ?? result.requests[0]?.source;
        rows.splice(0, rows.length, ...result.requests.sort((a, b) => b.completed.localeCompare(a.completed))); this.onRequests(tunnel, rows);
        post({ type: 'requests', requests: rows }); post({ type: 'connection', state: 'live', source, demo: this.backend.capabilities.includes('demo') });
        buffer.bind(subscriptionId).forEach(deliver);
      } catch { buffer.reset(); post({ type: 'connection', state: 'stopped', message: 'Request stream unavailable. Close this panel and try again.' }); }
    };
    const showDetail = async (requestId: string) => {
      if (!this.backend.capabilities.includes('requestDetail')) throw new Error('Request detail requires a newer Groundhog CLI or relay.');
      const selected = await selection.select(requestId);
      if (selected) post({ type: 'detail', ...selected });
    };
    panel.webview.onDidReceiveMessage(async (message: { type?: string; requestId?: unknown; target?: unknown; headers?: unknown; body?: unknown; enabled?: unknown }) => {
      try {
      if (message?.type === 'ready') {
        post({ type: 'captureState', enabled: tunnel.captureBodies ?? false, canChange: tunnel.canCaptureBodies ?? false });
        post({ type: 'qr', dataUrl: await toDataURL(tunnel.url, { width: 160, margin: 2 }), url: tunnel.url });
        if (subscriptionId) { post({ type: 'requests', requests: rows }); post({ type: 'connection', state: 'live', source, demo: this.backend.capabilities.includes('demo') }); }
        else void subscribe();
      } else if (message?.type === 'detail') {
        if (typeof message.requestId !== 'string' || !rows.some(row => row.requestId === message.requestId)) return;
        await showDetail(message.requestId);
      } else if (message?.type === 'capture' && typeof message.enabled === 'boolean') {
        const result = await this.backend.request('captureBodies/set', { workspace: tunnel.workspace, name: tunnel.name, enabled: message.enabled });
        post({ type: 'captureState', enabled: result.enabled, canChange: true });
        if (selection.value) await showDetail(selection.value.detail.requestId);
      } else if (['replay', 'curl', 'export'].includes(message?.type ?? '')) {
        const detail = selection.displayed(message.requestId);
        if (typeof message.target !== 'string' || typeof message.headers !== 'string' || typeof message.body !== 'string' || message.headers.length > 65536 || message.body.length > 262144) throw new Error('The edited request is too large or invalid.');
        const headers = parseHeaders(message.headers); const body = message.body;
        if (message.type === 'curl') { await vscode.env.clipboard.writeText(asCurl(detail, message.target, headers, body)); post({ type: 'operation', message: 'Copied as curl.' }); }
        if (message.type === 'export') {
          const result = await this.backend.request('requests/exportHttp', { workspace: tunnel.workspace, name: tunnel.name, requestId: detail.requestId, target: message.target, headers, body });
          await vscode.window.showTextDocument(await vscode.workspace.openTextDocument({ language: 'http', content: result.text }));
        }
        if (message.type === 'replay') {
          if (!vscode.workspace.isTrusted) throw new Error('Trust this workspace before sending requests to a local app.');
          const warning = signatureWarning(headers) ? ' A signature header is present. Signed webhooks often reject old timestamps.' : '';
          if (await vscode.window.showWarningMessage('Send this request again to your local app?' + warning, { modal: true }, 'Send again') !== 'Send again') return;
          const result = await new RequestActions(this.backend).replay(tunnel, detail, message.target, headers, body);
          post({ type: 'operation', message: `Local app returned ${result.statusCode} in ${result.durationMs} ms.` });
        }
      }
      } catch (error) { post({ type: 'operation', message: (error as Error).message }); }
    });
    panel.onDidDispose(() => {
      disposed = true; generation++; selection.dispose(); this.panels.delete(key); subscriptions.forEach(s => s.dispose());
      if (subscriptionId) void this.backend.request('requests/unsubscribe', { subscriptionId }).catch(() => {});
    });
    panel.webview.html = webviewHtml(panel.webview, this.extensionUri, 'inspector.js');
  }
  dispose(): void { for (const panel of this.panels.values()) panel.dispose(); this.panels.clear(); }
}
