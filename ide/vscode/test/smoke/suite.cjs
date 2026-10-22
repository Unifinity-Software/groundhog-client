const assert = require('node:assert/strict');
const vscode = require('vscode');
exports.run = async () => {
  const extension = vscode.extensions.getExtension('unifinity.groundhog');
  assert.ok(extension, 'Extension is installed');
  await vscode.workspace.getConfiguration('groundhog').update('useMockBackend', true, vscode.ConfigurationTarget.Global);
  await extension.activate();
  await vscode.commands.executeCommand('workbench.view.extension.groundhog');
  await vscode.commands.executeCommand('workbench.action.closeAuxiliaryBar');
  const commands = await vscode.commands.getCommands(true);
  assert.ok(commands.includes('groundhog.installCli'));
  assert.ok(commands.includes('groundhog.signIn'));
  await vscode.commands.executeCommand('groundhog.requests', { kind: 'tunnel', tunnel: { workspace: 'example', name: 'my-tunnel', url: 'https://my-tunnel.groundhog.unifinity.com', state: 'connected', actions: ['rename', 'delete'], hasKey: true } });
  assert.ok(vscode.window.tabGroups.all.some(group => group.tabs.some(tab => tab.label === 'Groundhog requests: my-tunnel')), 'Inspector webview opened');
  const capture = await fetch(process.env.GROUNDHOG_SMOKE_CAPTURE_URL, { method: 'POST' });
  assert.equal(capture.status, 200, await capture.text());
};
