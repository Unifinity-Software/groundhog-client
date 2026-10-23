const assert = require('node:assert/strict');
const vscode = require('vscode');
exports.run = async () => {
  await vscode.workspace.getConfiguration('groundhog').update('useMockBackend', false, vscode.ConfigurationTarget.Global);
  await vscode.workspace.getConfiguration('groundhog').update('cliPath', 'groundhog', vscode.ConfigurationTarget.Global);
  const extension = vscode.extensions.getExtension('unifinity.groundhog');
  assert.ok(extension); await extension.activate();
  assert.ok((await vscode.commands.getCommands(true)).includes('groundhog.runDiagnostics'), 'Compatible real CLI enabled the complete extension');
  await vscode.commands.executeCommand('groundhog.refresh');
  assert.equal(await vscode.authentication.getSession('groundhog', [], { silent: true }), undefined, 'Fresh bundled backend safely reports signed out');
};
