import * as vscode from 'vscode';
import { afterEach, expect, it, vi } from 'vitest';
import { MockBackend } from '../src/mockBackend';
import { TunnelExplorer } from '../src/explorer';
import { GroundhogDiagnostics } from '../src/diagnostics';
afterEach(() => vi.useRealTimers());

it('shows relay request rates before a panel is opened and prefers the active live rate when present', async () => {
  vi.useFakeTimers({ toFake: ['Date'] }); vi.setSystemTime(0);
  const backend = new MockBackend(); const explorer = new TunnelExplorer(backend);
  const tunnel = { ...(await backend.request('tunnels/list', { workspace: 'example' })).tunnels[1], requestsLastMinute: 23 };
  expect(explorer.getTreeItem({ kind: 'tunnel', tunnel }).tooltip).toContain('23 req/min');
  explorer.setRequestRate(tunnel.name, 4);
  expect(explorer.getTreeItem({ kind: 'tunnel', tunnel }).tooltip).toContain('4 req/min');
  vi.setSystemTime(60001);
  expect(explorer.getTreeItem({ kind: 'tunnel', tunnel }).tooltip).toContain('23 req/min');
  explorer.dispose(); backend.dispose();
});

it('keeps diagnostic quick fixes bound to their tunnel and project folder', async () => {
  const backend = new MockBackend(); const diagnostics = new GroundhogDiagnostics(backend);
  const tunnel = (await backend.request('tunnels/list', { workspace: 'example' })).tunnels[2];
  const folder = { uri: { toString: () => 'file:///project-b' } } as vscode.WorkspaceFolder;
  const document = { uri: vscode.Uri.joinPath(folder.uri, 'groundhog.json'), getText: () => '{"tunnel":"service-b","target":"http://localhost:5000"}', positionAt: (character: number) => ({ line: 0, character }) } as vscode.TextDocument;
  vi.mocked(vscode.workspace.openTextDocument).mockResolvedValue(document);
  vi.spyOn(backend, 'request').mockResolvedValueOnce({ checks: [{ code: 'connectionKey', status: 'failed', message: 'Missing key.' }, { code: 'localCertificate', status: 'failed', message: 'Untrusted certificate.' }] });
  await diagnostics.run(folder, tunnel, 'http://localhost:5000');
  const collection = vi.mocked(vscode.languages.createDiagnosticCollection).mock.results.at(-1)!.value;
  const values = vi.mocked(collection.set).mock.calls.at(-1)![1] as vscode.Diagnostic[];
  const provider = vi.mocked(vscode.languages.registerCodeActionsProvider).mock.calls.at(-1)![1];
  const actions = await provider.provideCodeActions(document, {} as vscode.Range, { diagnostics: values, only: undefined, triggerKind: vscode.CodeActionTriggerKind.Invoke }, {} as vscode.CancellationToken);
  expect(actions).toHaveLength(2);
  for (const action of actions as vscode.CodeAction[]) expect(action.command?.arguments).toEqual([{ kind: 'tunnel', tunnel, folder }]);
  diagnostics.dispose(); backend.dispose();
});
