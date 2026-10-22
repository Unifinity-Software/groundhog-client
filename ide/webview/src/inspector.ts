import { hostAdapter } from './adapter';
import { RequestModel, RequestRow, matches } from './model';

const host = hostAdapter();
const model = new RequestModel();
let paused = false;
let frozenRows: RequestRow[] = [];
const root = document.getElementById('app')!;
root.innerHTML = `<header><div><h1>Requests</h1><span id="source" role="status">Connecting…</span></div><button id="pause" type="button">Pause</button></header><form aria-label="Filter requests"><label>Method<select id="method"><option value="">All methods</option>${['GET', 'POST', 'PUT', 'PATCH', 'DELETE', 'HEAD', 'OPTIONS'].map(m => `<option>${m}</option>`).join('')}</select></label><label>Status<select id="status"><option value="">All statuses</option>${[2, 3, 4, 5].map(s => `<option value="${s}">${s}xx</option>`).join('')}</select></label><label class="search">Path<input id="path" type="search" placeholder="Filter paths"></label></form><p id="count" role="status">Waiting for requests</p><div class="table-wrap"><table aria-label="Captured requests"><thead><tr><th scope="col">Time</th><th scope="col">Method</th><th scope="col">Path</th><th scope="col">Status</th><th scope="col">Duration</th></tr></thead><tbody id="rows"></tbody></table></div><p id="empty">Requests appear here when traffic reaches this tunnel.</p><section id="detail" aria-label="Request detail" hidden></section>`;
const method = document.getElementById('method') as HTMLSelectElement;
const status = document.getElementById('status') as HTMLSelectElement;
const path = document.getElementById('path') as HTMLInputElement;
const tbody = document.getElementById('rows')!;
function render(): void {
  const rows = (paused ? frozenRows : model.rows).filter(row => matches(row, { method: method.value, status: status.value, path: path.value }));
  tbody.replaceChildren(...rows.map(row => {
    const tr = document.createElement('tr');
    tr.tabIndex = 0; tr.dataset.id = row.requestId;
    tr.setAttribute('aria-label', `${row.method} ${row.path}, status ${row.statusCode}`);
    for (const value of [new Date(row.completed).toLocaleTimeString(), row.method, row.path, String(row.statusCode), `${row.durationMs} ms`]) {
      const cell = document.createElement('td'); cell.textContent = value; tr.append(cell);
    }
    tr.addEventListener('click', () => host.postMessage({ type: 'detail', requestId: row.requestId }));
    tr.addEventListener('keydown', event => { if (event.key === 'Enter' || event.key === ' ') { event.preventDefault(); tr.click(); } });
    return tr;
  }));
  document.getElementById('count')!.textContent = `${rows.length} of ${model.rows.length} requests${paused ? ' · paused' : ''}`;
  document.getElementById('empty')!.hidden = rows.length > 0;
}
for (const control of [method, status, path]) control.addEventListener('input', render);
document.querySelector('form')!.addEventListener('submit', event => event.preventDefault());
document.getElementById('pause')!.addEventListener('click', () => {
  paused = !paused;
  if (paused) frozenRows = [...model.rows];
  document.getElementById('pause')!.textContent = paused ? 'Resume' : 'Pause';
  if (!paused) render();
});
host.onMessage(message => {
  const data = message as { type?: string; requests?: RequestRow[]; request?: RequestRow; state?: string; source?: string; message?: string; demo?: boolean };
  if (data.type === 'requests' && Array.isArray(data.requests)) { model.add(data.requests); if (!paused) render(); }
  if (data.type === 'request' && data.request) { model.add([data.request]); if (!paused) render(); }
  if (data.type === 'connection') document.getElementById('source')!.textContent = data.state === 'live' ? `${data.demo ? 'Demo data · ' : ''}${data.source === 'remote' ? 'Service' : 'Local'} · live` : data.state === 'reconnecting' ? 'Reconnecting…' : data.message ?? 'Stopped';
});
host.postMessage({ type: 'ready' });
