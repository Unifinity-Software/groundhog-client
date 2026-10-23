import { afterEach, beforeEach, expect, it, vi } from 'vitest';
import * as vscode from 'vscode';
import { MockBackend } from '../src/mockBackend';
import { VisitorAccessPanels } from '../src/visitorAccess';

beforeEach(() => vi.clearAllMocks());
afterEach(() => { vi.restoreAllMocks(); Object.assign(vscode.workspace, { isTrusted: true }); });

it.each(['Copy key', 'Save to user secrets'])('shows a newly created key once in the host modal and supports %s without exposing it to the webview', async choice => {
  const backend = new MockBackend();
  const tunnel = (await backend.request('tunnels/list', { workspace: 'example' })).tunnels[0];
  let receive!: (message: Record<string, unknown>) => Promise<void>;
  const postMessage = vi.fn(async () => true);
  const panel = {
    webview: { postMessage, asWebviewUri: (uri: vscode.Uri) => uri.toString(), cspSource: 'test-origin', html: '', onDidReceiveMessage: (callback: typeof receive) => { receive = callback; } },
    onDidDispose: vi.fn(), dispose: vi.fn(),
  };
  vi.mocked(vscode.window.createWebviewPanel).mockReturnValue(panel as unknown as vscode.WebviewPanel);
  const panels = new VisitorAccessPanels(backend, vscode.Uri.parse('file:///extension'));
  await panels.open(tunnel); await receive({ type: 'ready' });
  const key = 'synthetic-one-time-visitor-api-key';
  const request = vi.spyOn(backend, 'request').mockResolvedValueOnce({ id: 'visitor-key', name: 'Webhook', created: '2026-10-22T00:00:00Z', key });
  vi.mocked(vscode.window.showInformationMessage).mockResolvedValueOnce(choice as never);
  vi.mocked(vscode.workspace.findFiles).mockResolvedValue([{ fsPath: 'C:/project/App.csproj' } as vscode.Uri]);
  await receive({ type: 'createKey', label: 'Webhook' });
  expect(vscode.window.showInformationMessage).toHaveBeenCalledExactlyOnceWith('Your visitor API key is shown once. Copy it or save it now.', { modal: true, detail: key }, 'Copy key', 'Save to user secrets');
  if (choice === 'Copy key') expect(vscode.env.clipboard.writeText).toHaveBeenCalledExactlyOnceWith(key);
  else expect(request).toHaveBeenCalledWith('visitorAccess/saveKey', { workspace: 'example', name: tunnel.name, project: 'C:/project/App.csproj', parameter: 'groundhog-api-key', key, confirm: true });
  await receive({ type: 'ready' });
  expect(vscode.window.showInformationMessage).toHaveBeenCalledTimes(1);
  expect(JSON.stringify(postMessage.mock.calls)).not.toContain(key);
  expect(panel.webview.html).not.toContain(key);
  panels.dispose(); backend.dispose();
});

it('keeps the local user-secrets trust check after showing the one-time key', async () => {
  const backend = new MockBackend();
  const tunnel = (await backend.request('tunnels/list', { workspace: 'example' })).tunnels[0];
  let receive!: (message: Record<string, unknown>) => Promise<void>;
  const postMessage = vi.fn(async () => true);
  const panel = { webview: { postMessage, asWebviewUri: (uri: vscode.Uri) => uri.toString(), html: '', cspSource: 'test-origin', onDidReceiveMessage: (callback: typeof receive) => { receive = callback; } }, onDidDispose: vi.fn(), dispose: vi.fn() };
  vi.mocked(vscode.window.createWebviewPanel).mockReturnValue(panel as unknown as vscode.WebviewPanel);
  vi.mocked(vscode.window.showInformationMessage).mockResolvedValueOnce('Save to user secrets' as never);
  Object.assign(vscode.workspace, { isTrusted: false });
  const panels = new VisitorAccessPanels(backend, vscode.Uri.parse('file:///extension'));
  await panels.open(tunnel);
  const request = vi.spyOn(backend, 'request');
  await receive({ type: 'createKey', label: 'Webhook' });
  expect(request.mock.calls.some(([method]) => method === 'visitorAccess/saveKey')).toBe(false);
  expect(postMessage).toHaveBeenCalledWith({ type: 'operation', message: 'Trust this workspace before saving a visitor API key to user secrets.' });
  panels.dispose(); backend.dispose();
});
