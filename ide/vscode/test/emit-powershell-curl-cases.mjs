import { build } from 'esbuild';
import { Buffer } from 'node:buffer';
import process from 'node:process';
import { fileURLToPath, URL } from 'node:url';

// Load the current implementation in memory; no copied quoting logic or generated files.
const result = await build({ entryPoints: [fileURLToPath(new URL('../src/requestActions.ts', import.meta.url))], bundle: true, platform: 'node', format: 'esm', write: false, logLevel: 'silent' });
const { asCurl } = await import('data:text/javascript;base64,' + Buffer.from(result.outputFiles[0].text).toString('base64'));
const detail = { method: 'POST', path: '/webhooks' };
const payload = "'; Write-Output PWN; #";
const cases = [
  { field: 'body', command: asCurl(detail, 'http://localhost:5173', {}, payload, 'win32') },
  { field: 'header', command: asCurl(detail, 'http://localhost:5173', { Test: payload }, '', 'win32') },
  { field: 'path', command: asCurl({ ...detail, path: '/' + payload }, 'http://localhost:5173', {}, '', 'win32') }
];
// Remove an escape from actual output so the parser check also proves it detects an extra command.
const unsafeControl = cases[0].command.replace("''; Write-Output", "'; Write-Output");
if (unsafeControl === cases[0].command) throw new Error('The unsafe curl control needs updating.');
process.stdout.write(JSON.stringify({ cases, unsafeControl }) + '\n');
