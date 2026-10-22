const path = require('node:path');
const { runTests } = require('@vscode/test-electron');
runTests({ version: '1.143.0', extensionDevelopmentPath: path.resolve(__dirname, '../..'), extensionTestsPath: path.resolve(__dirname, 'suite.cjs'), launchArgs: ['--disable-extensions', '--disable-gpu', '--no-sandbox', '--skip-welcome', '--skip-release-notes', '--disable-workspace-trust'], extensionTestsEnv: { GROUNDHOG_SMOKE: '1' } }).catch(error => { console.error(error); process.exitCode = 1; });
