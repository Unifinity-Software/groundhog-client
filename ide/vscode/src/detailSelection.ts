import { RequestDetail } from './protocol';

/** Actions identify the detail rendered in the browser, including across asynchronous selection changes. */
export class DetailSelection {
  private generation = 0;
  private disposed = false;
  value?: { detail: RequestDetail; target: string };
  constructor(private readonly load: (id: string) => Promise<RequestDetail>, private readonly target: () => Promise<string | undefined>) {}
  async select(id: string): Promise<typeof this.value> {
    const generation = ++this.generation;
    const detail = await this.load(id);
    const target = await this.target() ?? 'http://localhost:5173';
    if (this.disposed || generation !== this.generation) return;
    return this.value = { detail, target };
  }
  displayed(id: unknown): RequestDetail {
    if (!this.value || id !== this.value.detail.requestId) throw new Error('The selected request changed. Choose its row again before continuing.');
    return this.value.detail;
  }
  dispose(): void { this.disposed = true; this.generation++; this.value = undefined; }
}
