import { createHash } from 'node:crypto';
import { createRequire } from 'node:module';
import { mkdtemp, readFile, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { expect, it } from 'vitest';

const require = createRequire(process.cwd() + '/test/recovery.test.ts');
const recover = require('../scripts/recover-marketplace.cjs');
const manifest = require('../package.json');
const tag = `vscode-v${manifest.version}`;
const targets = ['win32-x64', 'win32-arm64', 'linux-x64', 'linux-arm64', 'darwin-x64', 'darwin-arm64'];

async function zip(entries: Record<string, Buffer | string>): Promise<Buffer> {
  const { ZipFile } = require('yazl');
  const archive = new ZipFile();
  const chunks: Buffer[] = [];
  const complete = new Promise<Buffer>((resolve, reject) => {
    archive.outputStream.on('data', (chunk: Buffer) => chunks.push(chunk));
    archive.outputStream.on('end', () => resolve(Buffer.concat(chunks)));
    archive.outputStream.on('error', reject);
  });
  for (const [name, value] of Object.entries(entries)) archive.addBuffer(Buffer.from(value), name);
  archive.end();
  return complete;
}

it('recovers only digest-matched artifacts from the successful approved tag and tested merge', async () => {
  const sha = 'a'.repeat(40);
  const archives = await Promise.all(targets.map(async target => zip({ [`groundhog-${target}.vsix`]: await zip({
    'extension/package.json': JSON.stringify(manifest),
    'extension.vsixmanifest': `<PackageManifest><Metadata><Identity Id="groundhog" Publisher="unifinity" Version="${manifest.version}" TargetPlatform="${target}"/><Properties><Property Id="Microsoft.VisualStudio.Code.PreRelease" Value="${manifest.groundhog.preRelease}"/></Properties></Metadata></PackageManifest>`
  }) })));
  const artifacts = archives.map((bytes, id) => ({ id, name: `groundhog-${targets[id]}`, size_in_bytes: bytes.length, expired: false,
    workflow_run: { head_sha: sha }, digest: `sha256:${createHash('sha256').update(bytes).digest('hex')}` }));
  let runSha = sha;
  let downloads = 0;
  const actions = {
    getWorkflowRun: async () => ({ data: { id: 123, event: 'push', path: '.github/workflows/release-vscode.yml', head_branch: tag, head_sha: runSha, status: 'completed', conclusion: 'success' } }),
    listWorkflowRuns: async () => ({ data: { workflow_runs: [{ id: 1, head_sha: 'pr-head', status: 'completed', conclusion: 'success' }] } }),
    listWorkflowRunArtifacts: () => {},
    downloadArtifact: async ({ artifact_id }: { artifact_id: number }) => { downloads++; return { data: archives[artifact_id] }; }
  };
  const repos = { listPullRequestsAssociatedWithCommit: () => {} };
  const outputs: string[] = [];
  const options = {
    context: { ref: 'refs/heads/master', repo: { owner: 'public-owner', repo: 'public-repo' }, payload: { inputs: { recovery_run_id: '123', recovery_tag: tag } } },
    github: { rest: { actions, repos }, paginate: async (method: unknown) => method === actions.listWorkflowRunArtifacts ? artifacts : [{ merged_at: 'now', base: { ref: 'master' }, merge_commit_sha: sha, head: { sha: 'pr-head' } }] },
    exec: { exec: async () => 0, getExecOutput: async (_cmd: string, args: string[]) => ({ stdout: args[0] === 'show' ? JSON.stringify(manifest) : sha }) },
    core: { info: () => {}, setOutput: (key: string, value: string) => outputs.push(`${key}=${value}`) }
  };
  // Isolate output paths without changing the process working directory used by other tests.
  const folder = await mkdtemp(join(tmpdir(), 'groundhog-recovery-'));
  const originalCwd = process.cwd;
  process.cwd = () => folder;
  try {
    runSha = 'b'.repeat(40);
    await expect(recover(options)).rejects.toThrow('exact tag and source');
    expect(downloads).toBe(0);
    runSha = sha;
    artifacts[0].digest = `sha256:${'0'.repeat(64)}`;
    await expect(recover(options)).rejects.toThrow('digest mismatch');
    await rm(join(folder, 'release-packages'), { recursive: true });
    artifacts[0].digest = `sha256:${createHash('sha256').update(archives[0]).digest('hex')}`;
    await recover(options);
    for (const target of targets) expect((await readFile(join(folder, 'release-packages', `groundhog-${target}.vsix`))).length).toBeGreaterThan(0);
    expect(outputs).toEqual([`pre_release=${manifest.groundhog.preRelease}`]);
    await expect(recover(options)).rejects.toThrow(); // Never mix a previous download directory into a recovery.
  } finally {
    process.cwd = originalCwd;
    await rm(folder, { recursive: true, force: true });
  }
});
