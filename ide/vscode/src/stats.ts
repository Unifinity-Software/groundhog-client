import { Backend, Disposable, RequestSummary, TunnelRef } from './protocol';
import { SubscriptionBuffer } from './subscription';

/** One lightweight selected-tunnel stream; the status bar works with every panel closed. */
export class ActiveRequestStats implements Disposable {
  private active?: TunnelRef;
  private id?: string;
  private rows: RequestSummary[] = [];
  private generation = 0;
  private disconnected = false;
  private readonly buffer = new SubscriptionBuffer();
  private readonly timer;
  private readonly listeners;
  constructor(private readonly backend: Backend, private readonly changed: (stats: { requests: number; failures: number; disconnected: boolean }) => void) {
    this.listeners = [backend.on('requests/event', event => this.buffer.receive({ type: 'request', value: event }).forEach(e => { if (e.type === 'request') this.add(e.value.request); })), backend.on('connection/event', event => { if (event.subscriptionId === this.id) this.publish(event.state !== 'live'); }), backend.on('backend/state', event => {
      if (event.state === 'ready' && this.active) { this.id = undefined; void this.follow(this.active, true); }
      else if (event.state !== 'ready') this.publish(true);
    })];
    this.timer = setInterval(() => this.publish(), 1000);
  }
  async follow(tunnel: TunnelRef, force = false): Promise<void> {
    if (!force && this.active?.name === tunnel.name && this.active.workspace === tunnel.workspace) return;
    const generation = ++this.generation; const previous = this.id; this.id = undefined; this.active = tunnel; this.rows = []; this.buffer.reset(); this.publish();
    if (previous) await this.backend.request('requests/unsubscribe', { subscriptionId: previous }).catch(() => {});
    try {
      const result = await this.backend.request('requests/subscribe', { ...tunnel, source: 'auto' });
      if (generation !== this.generation) { await this.backend.request('requests/unsubscribe', { subscriptionId: result.subscriptionId }); return; }
      this.id = result.subscriptionId; this.rows = result.requests;
      for (const event of this.buffer.bind(result.subscriptionId)) { if (event.type === 'request') this.add(event.value.request); }
      this.publish(false);
    } catch { if (generation === this.generation) this.publish(true); }
  }
  private add(row: RequestSummary): void { this.rows = [row, ...this.rows.filter(r => r.requestId !== row.requestId)].slice(0, 1000); this.publish(); }
  matches(subscriptionId: string): boolean { return this.id === subscriptionId; }
  private publish(disconnected = this.disconnected): void {
    this.disconnected = disconnected;
    this.rows = this.rows.filter(row => Date.parse(row.completed) > Date.now() - 60000);
    this.changed({ requests: this.rows.length, failures: this.rows.filter(row => row.statusCode >= 500).length, disconnected });
  }
  dispose(): void {
    this.generation++; clearInterval(this.timer); this.listeners.forEach(listener => listener.dispose());
    if (this.id) void this.backend.request('requests/unsubscribe', { subscriptionId: this.id }).catch(() => {});
  }
}
