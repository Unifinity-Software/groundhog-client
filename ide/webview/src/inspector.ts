import { hostAdapter } from './adapter';
import { RequestModel, RequestRow, matches } from './model';

const host = hostAdapter();
const model = new RequestModel();
let paused = false;
let frozenRows: RequestRow[] = [];
let currentSource = '';
const root = document.getElementById('app')!;
root.innerHTML = `<header><div><h1>Requests</h1><span id="source" role="status">Connecting…</span></div><button id="pause" type="button">Pause</button></header><form aria-label="Filter requests"><label>Method<select id="method"><option value="">All methods</option>${['GET', 'POST', 'PUT', 'PATCH', 'DELETE', 'HEAD', 'OPTIONS'].map(m => `<option>${m}</option>`).join('')}</select></label><label>Status<select id="status"><option value="">All statuses</option>${[2, 3, 4, 5].map(s => `<option value="${s}">${s}xx</option>`).join('')}</select></label><label class="search">Path<input id="path" type="search" placeholder="Filter paths"></label></form><p id="count" role="status">Waiting for requests</p><div class="table-wrap"><table aria-label="Captured requests"><thead><tr><th scope="col">Time</th><th scope="col">Method</th><th scope="col">Path</th><th scope="col">Status</th><th scope="col">Duration</th></tr></thead><tbody id="rows"></tbody></table></div><p id="empty">Requests appear here when traffic reaches this tunnel.</p><section id="detail" aria-label="Request detail" hidden></section>`;
const method = document.getElementById('method') as HTMLSelectElement;
const status = document.getElementById('status') as HTMLSelectElement;
const path = document.getElementById('path') as HTMLInputElement;
const tbody = document.getElementById('rows')!;
const operation = document.createElement('p'); operation.setAttribute('role', 'status'); operation.id = 'operation'; root.append(operation);
const qrButton = document.createElement('button'); qrButton.type = 'button'; qrButton.textContent = 'Show QR code'; document.querySelector('header')!.append(qrButton);
const qr = document.createElement('img'); qr.alt = 'QR code of the public tunnel URL'; qr.width = 160; qr.height = 160; qr.hidden = true; document.querySelector('header')!.after(qr);
qrButton.addEventListener('click', () => { qr.hidden = !qr.hidden; qrButton.textContent = qr.hidden ? 'Show QR code' : 'Hide QR code'; });
interface Detail { requestId: string; method: string; path: string; headers: Record<string, string>; body: string; truncated: boolean; captureBodies: boolean; canCaptureBodies: boolean }
function renderDetail(detail: Detail, target: string): void {
  const section = document.getElementById('detail')!; section.hidden = false;
  section.innerHTML = '<h2></h2><label>Body capture<input id="capture" type="checkbox"></label><p id="permission"></p><label>Local target<input id="target" type="url"></label><label>Headers (JSON)<textarea id="headers" maxlength="65536" spellcheck="false"></textarea></label><label>Body<textarea id="body" maxlength="262144" spellcheck="false"></textarea></label><p id="warning"></p><div class="actions"><button id="replay" type="button">Send again</button><button id="curl" type="button">Copy as curl</button><button id="export" type="button">Export as .http</button></div>';
  section.querySelector('h2')!.textContent = detail.method + ' ' + detail.path;
  const capture = section.querySelector('#capture') as HTMLInputElement; capture.checked = detail.captureBodies; capture.disabled = !detail.canCaptureBodies;
  section.querySelector('#permission')!.textContent = detail.canCaptureBodies ? 'Body capture changes apply to future requests. Captured data may contain sensitive information.' : 'Only the owner or tunnel creator can change body capture.';
  capture.addEventListener('change', () => host.postMessage({ type: 'capture', enabled: capture.checked }));
  const targetInput = section.querySelector('#target') as HTMLInputElement; targetInput.value = target;
  const headers = section.querySelector('#headers') as HTMLTextAreaElement; headers.value = JSON.stringify(detail.headers, null, 2);
  const body = section.querySelector('#body') as HTMLTextAreaElement; body.value = detail.body;
  section.querySelector('#warning')!.textContent = detail.truncated ? 'This capture was truncated. Send again is unavailable.' : Object.keys(detail.headers).some(name => name.toLowerCase().endsWith('-signature')) ? 'Signed webhooks may reject old timestamps when sent again.' : '';
  (section.querySelector('#replay') as HTMLButtonElement).disabled = detail.truncated;
  for (const type of ['replay', 'curl', 'export']) section.querySelector('#' + type)!.addEventListener('click', () => host.postMessage({ type, target: targetInput.value, headers: headers.value, body: body.value }));
}
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
  const data = message as { type?: string; requests?: RequestRow[]; request?: RequestRow; state?: string; source?: string; message?: string; demo?: boolean; detail?: Detail; target?: string; dataUrl?: string };
  if (data.type === 'requests' && Array.isArray(data.requests)) { model.add(data.requests); if (!paused) render(); }
  if (data.type === 'request' && data.request) { model.add([data.request]); if (!paused) render(); }
  if (data.type === 'detail' && data.detail) renderDetail(data.detail, data.target ?? 'http://localhost:5173');
  if (data.type === 'operation') operation.textContent = data.message ?? '';
  if (data.type === 'qr' && data.dataUrl?.startsWith('data:image/png;base64,')) qr.src = data.dataUrl;
  if (data.type === 'connection') {
    if (data.source) currentSource = data.source === 'remote' ? 'Service' : 'Local';
    document.getElementById('source')!.textContent = data.state === 'live' ? `${data.demo ? 'Demo data · ' : ''}${currentSource || 'Source pending'} · live` : data.state === 'reconnecting' ? 'Reconnecting…' : data.message ?? 'Stopped';
  }
});
host.postMessage({ type: 'ready' });
