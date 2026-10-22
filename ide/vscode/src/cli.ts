import { execFile } from 'node:child_process';
import { promisify } from 'node:util';

export type CliStatus = { available: true; version: string } | { available: false; reason: string };
export type VersionRunner = (file: string) => Promise<string>;
const run: VersionRunner = async file => (await promisify(execFile)(file, ['--version'], { timeout: 5000, windowsHide: true })).stdout;

export async function detectCli(file: string, minimum: string, execute: VersionRunner = run): Promise<CliStatus> {
  try {
    const output = await execute(file);
    const match = output.match(/\b(\d+)\.(\d+)\.(\d+)(-[\w.-]+)?/);
    if (!match) return { available: false, reason: 'Groundhog CLI returned an unreadable version.' };
    const actual = match.slice(1, 4).map(Number);
    const required = minimum.split('.').map(Number);
    const comparison = actual.map((n, i) => n - required[i]).find(n => n !== 0) ?? 0;
    if (comparison < 0 || (comparison === 0 && match[4])) {
      return { available: false, reason: `Groundhog CLI ${minimum} or later is required (found ${match[0]}).` };
    }
    return { available: true, version: match[0] };
  } catch {
    return { available: false, reason: `Groundhog CLI was not found at “${file}”. Install it or update Groundhog: CLI Path in settings.` };
  }
}
