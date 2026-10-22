import * as vscode from 'vscode';
import { detectCli } from './cli';

export async function activate(context: vscode.ExtensionContext): Promise<void> {
  const tree = vscode.window.createTreeView('groundhog.tunnels', {
    treeDataProvider: { getTreeItem: (item: vscode.TreeItem) => item, getChildren: () => [] }
  });
  context.subscriptions.push(tree,
    vscode.commands.registerCommand('groundhog.installCli', () => {
      const terminal = vscode.window.createTerminal('Install Groundhog CLI');
      terminal.sendText('dotnet tool update --global Groundhog.Cli');
      terminal.show();
    }),
    vscode.commands.registerCommand('groundhog.signIn', () => {
      const cli = vscode.workspace.getConfiguration('groundhog').get<string>('cliPath', 'groundhog');
      // Shell quoting is avoided for arbitrary paths by using a terminal process directly.
      vscode.window.createTerminal({ name: 'Groundhog sign in', shellPath: cli, shellArgs: ['login'] }).show();
    })
  );
  const cli = vscode.workspace.getConfiguration('groundhog').get<string>('cliPath', 'groundhog');
  const status = await detectCli(cli, context.extension.packageJSON.groundhog.minimumCliVersion as string);
  if (!status.available) {
    void vscode.window.showWarningMessage(status.reason, 'Install Groundhog CLI').then(action => {
      if (action) void vscode.commands.executeCommand('groundhog.installCli');
    });
  }
}
