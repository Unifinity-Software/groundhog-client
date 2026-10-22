const assert = require('node:assert/strict');
const vscode = require('vscode');
exports.run = async () => {
  const extension = vscode.extensions.getExtension('unifinity.groundhog');
  assert.ok(extension, 'Extension is installed');
  await extension.activate();
  await vscode.commands.executeCommand('workbench.view.extension.groundhog');
  const commands = await vscode.commands.getCommands(true);
  assert.ok(commands.includes('groundhog.installCli'));
  assert.ok(commands.includes('groundhog.signIn'));
};
