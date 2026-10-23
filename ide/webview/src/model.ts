export interface RequestRow { requestId: string; completed: string; method: string; path: string; statusCode: number; durationMs: number; source: 'local' | 'remote' }
export interface Filters { method: string; status: string; path: string }
export function matches(row: RequestRow, filter: Filters): boolean {
  return (!filter.method || row.method === filter.method) && (!filter.status || Math.floor(row.statusCode / 100).toString() === filter.status) && row.path.toLowerCase().includes(filter.path.toLowerCase());
}
export class RequestModel {
  rows: RequestRow[] = [];
  add(rows: RequestRow[]): void {
    const byId = new Map(this.rows.map(row => [row.requestId, row]));
    for (const row of rows) byId.set(row.requestId, row);
    this.rows = [...byId.values()].sort((a, b) => b.completed.localeCompare(a.completed) || b.requestId.localeCompare(a.requestId)).slice(0, 1000);
  }
}
