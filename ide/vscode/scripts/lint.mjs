import { ESLint } from 'eslint';
import { fileURLToPath } from 'node:url';
const lint = new ESLint({ cwd: fileURLToPath(new URL('../../', import.meta.url)), overrideConfigFile: fileURLToPath(new URL('../eslint.config.mjs', import.meta.url)) });
const results = await lint.lintFiles(['vscode/src', 'vscode/test', 'webview/src']);
const output = (await lint.loadFormatter('stylish')).format(results);
if (output) console.log(output);
if (results.some(result => result.errorCount)) process.exitCode = 1;
