const { createHash } = require('node:crypto');
const { mkdir, writeFile, rm } = require('node:fs/promises');
const { join } = require('node:path');
const { readZip, readVSIXPackage } = require('@vscode/vsce/out/zip');
const targets = ['win32-x64', 'win32-arm64', 'linux-x64', 'linux-arm64', 'darwin-x64', 'darwin-arm64'];

module.exports = async ({ github, context, core, exec }) => {
  const { recovery_run_id: input, recovery_tag: tag } = context.payload.inputs;
  if (context.ref !== 'refs/heads/master' || !/^[1-9]\d*$/.test(input) || !/^vscode-v\d+\.\d+\.\d+$/.test(tag))
    throw new Error('Recovery requires master, an original release run ID and its exact version tag.');
  const repo = context.repo;
  const { data: run } = await github.rest.actions.getWorkflowRun({ ...repo, run_id: Number(input) });
  const sha = (await exec.getExecOutput('git', ['rev-parse', `${tag}^{commit}`], { silent: true })).stdout.trim();
  if (run.event !== 'push' || run.path !== '.github/workflows/release-vscode.yml' || run.head_branch !== tag ||
      run.head_sha !== sha || run.status !== 'completed' || run.conclusion !== 'success')
    throw new Error('The original run must be a successful release of this exact tag and source.');
  await exec.exec('git', ['merge-base', '--is-ancestor', sha, 'origin/master']);
  const prs = await github.paginate(github.rest.repos.listPullRequestsAssociatedWithCommit, { ...repo, commit_sha: sha });
  const pr = prs.find(p => p.merged_at && p.base.ref === 'master' && p.merge_commit_sha === sha);
  if (!pr) throw new Error('The original source must be a tested master merge.');
  const { data } = await github.rest.actions.listWorkflowRuns({ ...repo, workflow_id: 'build.yml', head_sha: pr.head.sha, event: 'pull_request' });
  const latest = data.workflow_runs.filter(r => r.head_sha === pr.head.sha).sort((a, b) => b.id - a.id)[0];
  if (!latest || latest.status !== 'completed' || latest.conclusion !== 'success') throw new Error('The latest original PR Build must have passed.');
  const original = JSON.parse((await exec.getExecOutput('git', ['show', `${sha}:ide/vscode/package.json`], { silent: true })).stdout);
  if (tag !== `vscode-v${original.version}` || original.name !== 'groundhog' || original.publisher !== 'unifinity')
    throw new Error('Original manifest identity must match the approved tag.');
  const artifacts = await github.paginate(github.rest.actions.listWorkflowRunArtifacts, { ...repo, run_id: run.id });
  const directory = join(process.cwd(), 'release-packages');
  await mkdir(directory); // Fail on an existing directory; never mix runs or local packages.
  for (const target of targets) {
    const name = `groundhog-${target}`;
    const matches = artifacts.filter(a => a.name === name);
    const artifact = matches[0];
    if (matches.length !== 1 || artifact.expired || artifact.workflow_run?.head_sha !== sha ||
        !/^sha256:[a-f0-9]{64}$/.test(artifact.digest) || artifact.size_in_bytes > 256 * 1024 * 1024)
      throw new Error(`Missing, expired or ambiguous original artifact: ${name}`);
    let response;
    try {
      response = await github.rest.actions.downloadArtifact({ ...repo, artifact_id: artifact.id, archive_format: 'zip' });
    } catch (error) {
      throw new Error(`Original artifact download failed (HTTP ${Number.isInteger(error.status) ? error.status : 'unknown'}).`);
    }
    const bytes = Buffer.from(response.data);
    if (`sha256:${createHash('sha256').update(bytes).digest('hex')}` !== artifact.digest)
      throw new Error(`Original artifact digest mismatch: ${name}`);
    const archive = join(directory, `${name}.zip`);
    await writeFile(archive, bytes);
    try {
      const filename = `${name}.vsix`;
      const files = await readZip(archive, entry => entry === filename);
      if (!files.has(filename)) throw new Error(`Original artifact lacks ${filename}`);
      const path = join(directory, filename);
      await writeFile(path, files.get(filename));
      const { manifest, xmlManifest } = await readVSIXPackage(path);
      const metadata = xmlManifest.PackageManifest.Metadata[0];
      const identity = metadata.Identity[0].$;
      const preRelease = metadata.Properties[0].Property.some(p => p.$.Id === 'Microsoft.VisualStudio.Code.PreRelease' && p.$.Value === 'true');
      if (manifest.publisher !== original.publisher || manifest.name !== original.name || manifest.version !== original.version ||
          identity.Id !== original.name || identity.Publisher !== original.publisher || identity.Version !== original.version ||
          identity.TargetPlatform !== target || preRelease !== original.groundhog.preRelease ||
          JSON.stringify(manifest.groundhog) !== JSON.stringify(original.groundhog))
        throw new Error(`Original VSIX identity mismatch: ${name}`);
      core.info(`Verified original artifact ${artifact.id}: ${name}, ${artifact.digest}`);
    } finally {
      await rm(archive);
    }
  }
  core.setOutput('pre_release', String(original.groundhog.preRelease === true));
};
