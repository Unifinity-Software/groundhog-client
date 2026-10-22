import { gte, valid } from 'semver';
import { CliStatus, detectCli } from './cli';
export interface CliSelection { path: string; status: CliStatus }
export async function selectCli(configured: string, bundled: string, minimum: string, detect = detectCli): Promise<CliSelection> {
  // An explicit override remains authoritative, including its actionable failure.
  if (configured !== 'groundhog') return { path: configured, status: await detect(configured, minimum) };
  const [global, embedded] = await Promise.all([detect(configured, minimum), detect(bundled, minimum)]);
  if (global.available && (!embedded.available || (valid(global.version) && valid(embedded.version) && gte(global.version, embedded.version)))) return { path: configured, status: global };
  if (embedded.available) return { path: bundled, status: embedded };
  return { path: configured, status: global };
}
