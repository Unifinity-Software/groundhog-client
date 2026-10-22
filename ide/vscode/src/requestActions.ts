import { Backend, RequestDetail, TunnelRef } from './protocol';

export function localTarget(value: string): string {
  const url = new URL(value);
  if (!['http:', 'https:'].includes(url.protocol) || !['localhost', '127.0.0.1', '[::1]'].includes(url.hostname) || url.username || url.password || url.search || url.hash) throw new Error('Replay target must be an HTTP(S) loopback URL without credentials, query or fragment.');
  return url.toString().replace(/\/$/, '');
}
export function signatureWarning(headers: Record<string, string>): boolean { return Object.keys(headers).some(name => name.toLowerCase().endsWith('-signature')); }
export function parseHeaders(text: string): Record<string, string> {
  const value: unknown = JSON.parse(text);
  if (!value || typeof value !== 'object' || Array.isArray(value) || Object.values(value).some(item => typeof item !== 'string')) throw new Error('Headers must be a JSON object containing string values.');
  return value as Record<string, string>;
}
export function asCurl(detail: RequestDetail, target: string, headers: Record<string, string>, body: string): string {
  const quote = (value: string) => "'" + value.replace(/'/g, "'\\''") + "'";
  const url = localTarget(target) + '/' + detail.path.replace(/^\/+/, '');
  return ['curl', '--request', quote(detail.method), quote(url), ...Object.entries(headers).flatMap(([name, value]) => ['--header', quote(name + ': ' + value)]), ...(body ? ['--data-raw', quote(body)] : [])].join(' ');
}
export class RequestActions {
  constructor(private readonly backend: Backend) {}
  async replay(tunnel: TunnelRef, detail: RequestDetail, target: string, headers: Record<string, string>, body: string) {
    if (detail.truncated) throw new Error('This request was truncated and cannot be sent again.');
    return this.backend.request('requests/replay', { ...tunnel, requestId: detail.requestId, target: localTarget(target), headers, body, confirm: true });
  }
}
