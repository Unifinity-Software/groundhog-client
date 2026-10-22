import * as vscode from 'vscode';
import { Backend, Tunnel } from './protocol';

export class GroundhogDiagnostics implements vscode.Disposable {
  private readonly collection = vscode.languages.createDiagnosticCollection('groundhog');
  private readonly provider;
  constructor(private readonly backend: Backend) {
    this.provider = vscode.languages.registerCodeActionsProvider({ language: 'json', pattern: '**/groundhog.json' }, {
      provideCodeActions: (_document, _range, context) => context.diagnostics.filter(diagnostic => diagnostic.source === 'Groundhog').flatMap(diagnostic => {
        const fixes: Record<string, [string, string]> = { connectionKey: ['Enter connection key', 'groundhog.enterKey'], localCertificate: ['Use local CA', 'groundhog.configureLocalCa'], targetReachable: ['Start my app', 'groundhog.startApp'] };
        const fix = fixes[String(diagnostic.code)]; if (!fix) return [];
        const action = new vscode.CodeAction(fix[0], vscode.CodeActionKind.QuickFix); action.diagnostics = [diagnostic]; action.command = { title: fix[0], command: fix[1] }; return [action];
      })
    }, { providedCodeActionKinds: [vscode.CodeActionKind.QuickFix] });
  }
  async run(folder: vscode.WorkspaceFolder | undefined, tunnel: Tunnel, target: string): Promise<void> {
    if (!folder) return;
    const uri = vscode.Uri.joinPath(folder.uri, 'groundhog.json');
    let document: vscode.TextDocument;
    try { document = await vscode.workspace.openTextDocument(uri); } catch { return; }
    const result = await this.backend.request('doctor/run', { workspace: tunnel.workspace, name: tunnel.name, target });
    const text = document.getText();
    const diagnostics = result.checks.filter(check => check.status !== 'passed').map(check => {
      const field = check.code === 'connectionKey' ? 'tunnel' : 'target';
      const offset = Math.max(0, text.indexOf('"' + field + '"'));
      const range = new vscode.Range(document.positionAt(offset), document.positionAt(offset + field.length + 2));
      const diagnostic = new vscode.Diagnostic(range, check.message, check.status === 'failed' ? vscode.DiagnosticSeverity.Error : vscode.DiagnosticSeverity.Warning);
      diagnostic.code = check.code; diagnostic.source = 'Groundhog'; return diagnostic;
    });
    this.collection.set(uri, diagnostics);
  }
  dispose(): void { this.provider.dispose(); this.collection.dispose(); }
}
