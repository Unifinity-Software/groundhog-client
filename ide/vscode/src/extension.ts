import * as vscode from 'vscode';
import { detectCli } from './cli';
import { RpcBackend, spawnSession } from './backend';
import { MockBackend } from './mockBackend';

export async function activate(context: vscode.ExtensionContext): Promise<void> {
  const tree = vscode.window.createTreeView('groundhog.tunnels', {
    treeDataProvider: { getTreeItem: (item: vscode.TreeItem) => item, getChildren: () => [] }
  });
  context.subscriptions.push(tree,
    vscode.commands.registerCommand('groundhog.installCli', () => {
      const terminal = vscode.window.createTerminal('Install Groundhog CLI');
      terminal.sendText('dotnet tool update --global Groundhog.Cli --prerelease');
      terminal.show();
    }),
    vscode.commands.registerCommand('groundhog.signIn', () => {
      const cli = vscode.workspace.getConfiguration('groundhog').get<string>('cliPath', 'groundhog');
      // Shell quoting is avoided for arbitrary paths by using a terminal process directly.
      vscode.window.createTerminal({ name: 'Groundhog sign in', shellPath: cli, shellArgs: ['login'] }).show();
    })
  );
  const cli = vscode.workspace.getConfiguration('groundhog').get<string>('cliPath', 'groundhog');
  if (context.extensionMode === vscode.ExtensionMode.Development && vscode.workspace.getConfiguration('groundhog').get<boolean>('useMockBackend', false)) {
    const backend = new MockBackend(); context.subscriptions.push(backend); await backend.start(); return;
  }
  const status = await detectCli(cli, context.extension.packageJSON.groundhog.minimumCliVersion as string);
  if (!status.available) {
    void vscode.window.showWarningMessage(status.reason, 'Install Groundhog CLI').then(action => {
      if (action) void vscode.commands.executeCommand('groundhog.installCli');
    });
  } else {
    const backend = new RpcBackend(() => spawnSession(cli), context.extension.packageJSON.version as string);
    context.subscriptions.push(backend);
    try { await backend.start(); } catch (error) { void vscode.window.showErrorMessage((error as Error).message); }
  }
}
