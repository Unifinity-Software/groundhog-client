import { readFile } from 'node:fs/promises';
import { valid } from 'semver';
const manifest = JSON.parse(await readFile(new URL('../package.json', import.meta.url), 'utf8'));
const tag = process.env.GITHUB_REF_NAME;
if (tag !== `vscode-v${manifest.version}`) throw new Error('The vscode-v tag must exactly match the extension manifest version.');
for (const cli of new Set([manifest.groundhog.minimumCliVersion, manifest.groundhog.bundledCliVersion])) {
  if (!valid(cli)) throw new Error('The minimum and bundled CLI versions must be valid exact semantic versions.');
  const version = cli.toLowerCase();
  const response = await fetch(`https://api.nuget.org/v3-flatcontainer/groundhog.cli/${version}/groundhog.cli.${version}.nupkg`, { method: 'HEAD', signal: AbortSignal.timeout(15000) });
  if (!response.ok) throw new Error(`Publish Groundhog.Cli ${cli} to NuGet before releasing this extension (HTTP ${response.status}).`);
  console.log(`Verified published supporting CLI ${cli}.`);
}
