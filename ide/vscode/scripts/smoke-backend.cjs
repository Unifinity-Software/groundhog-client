const path = require('node:path');
const fs = require('node:fs/promises');
const { spawn, execFileSync } = require('node:child_process');
const { createInterface } = require('node:readline');
const assert = require('node:assert/strict');
const semver = require('semver');
const { createMessageConnection, StreamMessageReader, StreamMessageWriter } = require('vscode-jsonrpc/node');
const manifest = require('../package.json');

async function main() {
  if (!process.argv[2]) throw new Error('Pass the bundled CLI executable path.');
  const executable = path.resolve(process.argv[2]);
  const base = path.resolve(__dirname, '../.vscode-test');
  await fs.mkdir(base, { recursive: true });
  const temporary = await fs.mkdtemp(path.join(base, 'backend-smoke-'));
  const env = { ...process.env, GROUNDHOG_HOME: path.join(temporary, 'home'), DOTNET_BUNDLE_EXTRACT_BASE_DIR: path.join(temporary, 'extract') };
  async function childSession(args, test) {
    const child = spawn(executable, args, { env, windowsHide: true, stdio: ['pipe', 'pipe', 'pipe'] });
    child.stderr.resume();
    const exited = new Promise((resolve, reject) => { child.once('error', reject); child.once('exit', code => resolve(code)); });
    const timer = setTimeout(() => child.kill(), 15000);
    try { await test(child); child.stdin.end(); assert.equal(await exited, 0, 'Bundled CLI exits cleanly after EOF'); }
    finally { clearTimeout(timer); if (child.exitCode === null) child.kill(); }
  }
  try {
    const output = execFileSync(executable, ['--version'], { env, windowsHide: true, encoding: 'utf8', timeout: 15000 });
    const version = output.match(/\b\d+\.\d+\.\d+(?:-[\w.-]+)?(?:\+[\w.-]+)?/)?.[0];
    assert.ok(version && semver.gte(version, manifest.groundhog.minimumCliVersion), 'Bundle version supports this extension');
    assert.ok(semver.eq(version, manifest.groundhog.bundledCliVersion), 'Bundle version exactly matches the release manifest');
    await childSession(['ide', '--stdio'], async child => {
      const connection = createMessageConnection(new StreamMessageReader(child.stdout), new StreamMessageWriter(child.stdin));
      connection.listen();
      try {
        const result = await connection.sendRequest('initialize', { clientVersion: manifest.version, protocolVersions: [1] });
        assert.equal(result.protocolVersion, 1);
        assert.equal((await connection.sendRequest('auth/status', {})).state, 'signedOut');
      } finally { connection.dispose(); }
    });
    await childSession(['mcp'], async child => {
      let next = 0;
      const pending = new Map();
      const lines = createInterface({ input: child.stdout });
      lines.on('line', line => {
        try {
          const message = JSON.parse(line); const request = pending.get(message.id);
          if (request) { pending.delete(message.id); message.error ? request.reject(new Error('MCP returned error ' + message.error.code)) : request.resolve(message.result); }
        } catch { for (const request of pending.values()) request.reject(new Error('Malformed MCP response')); }
      });
      child.once('exit', () => { for (const request of pending.values()) request.reject(new Error('MCP exited before replying')); });
      const call = (method, params) => new Promise((resolve, reject) => {
        const id = ++next; pending.set(id, { resolve, reject }); child.stdin.write(JSON.stringify({ jsonrpc: '2.0', id, method, params }) + '\n');
      });
      try {
        const initialized = await call('initialize', { protocolVersion: '2025-11-25', capabilities: {}, clientInfo: { name: 'groundhog-bundle-smoke', version: '1.0' } });
        assert.ok(initialized.serverInfo);
        child.stdin.write(JSON.stringify({ jsonrpc: '2.0', method: 'notifications/initialized' }) + '\n');
        const tools = (await call('tools/list', {})).tools.map(tool => tool.name);
        assert.ok(tools.includes('list_tunnels'));
        assert.ok(tools.includes('request_detail'));
        assert.ok(!tools.includes('replay_request'), 'MCP is read-only by default');
      } finally { lines.close(); }
    });
    console.log('Bundled CLI ' + version + ': version, IDE initialize/auth and read-only MCP passed.');
  } finally {
    if (!temporary.startsWith(base + path.sep)) throw new Error('Refusing cleanup outside the task smoke directory.');
    await fs.rm(temporary, { recursive: true, force: true });
  }
}
main().catch(error => { console.error(error.message); process.exitCode = 1; });
