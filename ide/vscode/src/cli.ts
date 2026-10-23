import { execFile } from 'node:child_process';
import { promisify } from 'node:util';
import { gte, valid } from 'semver';

export type CliStatus = { available: true; version: string } | { available: false; reason: string };
export type VersionRunner = (file: string) => Promise<string>;
const run: VersionRunner = async file => (await promisify(execFile)(file, ['--version'], { timeout: 5000, windowsHide: true })).stdout;

export async function detectCli(file: string, minimum: string, execute: VersionRunner = run): Promise<CliStatus> {
  try {
    if (!valid(minimum)) return { available: false, reason: 'The extension has an invalid minimum CLI version. Reinstall the Groundhog extension.' };
    const output = await execute(file);
    const match = output.match(/\b\d+\.\d+\.\d+(?:-[\w.-]+)?(?:\+[\w.-]+)?/);
    if (!match || !valid(match[0])) return { available: false, reason: 'Groundhog CLI returned an unreadable version.' };
    if (!gte(match[0], minimum)) {
      return { available: false, reason: `Groundhog CLI ${minimum} or later is required (found ${match[0]}).` };
    }
    return { available: true, version: match[0] };
  } catch {
    return { available: false, reason: `Groundhog CLI was not found at “${file}”. Install it or update Groundhog: CLI Path in settings.` };
  }
}
