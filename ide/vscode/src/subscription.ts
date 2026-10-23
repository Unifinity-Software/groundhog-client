import { Notifications } from './protocol';
export type StreamEvent = { type: 'request'; value: Notifications['requests/event'] } | { type: 'connection'; value: Notifications['connection/event'] };
/** JSON-RPC notifications may precede the subscribe response. Keep that race bounded. */
export class SubscriptionBuffer {
  private pending: StreamEvent[] = [];
  private id?: string;
  reset(): void { this.id = undefined; this.pending = []; }
  receive(event: StreamEvent): StreamEvent[] {
    if (this.id) return event.value.subscriptionId === this.id ? [event] : [];
    this.pending.push(event); if (this.pending.length > 1000) this.pending.shift(); return [];
  }
  bind(id: string): StreamEvent[] { this.id = id; const events = this.pending.filter(e => e.value.subscriptionId === id); this.pending = []; return events; }
}
