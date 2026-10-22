export class NotificationPolicy {
  private readonly lastNotice = new Map<string, number>();
  private readonly lastFailure = new Map<string, number>();
  private readonly states = new Map<string, string>();
  connection(name: string, state: string, enabled: boolean): boolean {
    const previous = this.states.get(name); this.states.set(name, state);
    return enabled && previous === 'connected' && state !== 'connected' && this.permit(name);
  }
  keyChanged(name: string): boolean { return this.permit(name); }
  request(name: string, status: number, enabled: boolean): boolean {
    if (status < 500) return false;
    const previous = this.lastFailure.get(name); this.lastFailure.set(name, Date.now());
    return enabled && (previous === undefined || Date.now() - previous >= 300000) && this.permit(name);
  }
  private permit(name: string): boolean {
    if (Date.now() - (this.lastNotice.get(name) ?? -Infinity) < 60000) return false;
    this.lastNotice.set(name, Date.now()); return true;
  }
}
export function tunnelLiterals(text: string): { name: string; offset: number }[] {
  return [...text.matchAll(/\bWithGroundhogTunnel\s*\(\s*"([a-z0-9][a-z0-9-]*)"/g)].map(match => ({ name: match[1], offset: match.index }));
}
