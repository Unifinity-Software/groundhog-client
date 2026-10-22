import { JSDOM } from 'jsdom';
import { afterEach, expect, it, vi } from 'vitest';
import { matches, RequestModel, RequestRow } from '../../webview/src/model';
import { SubscriptionBuffer } from '../src/subscription';

const row: RequestRow = { requestId: '1', completed: '2026-10-22T12:00:00Z', method: 'POST', path: '/webhooks', statusCode: 201, durationMs: 12, source: 'local' };
afterEach(() => vi.unstubAllGlobals());
it('preserves early subscription notifications and bounds unrelated pending traffic', () => {
  const buffer = new SubscriptionBuffer();
  for (let i = 0; i < 1100; i++) buffer.receive({ type: 'request', value: { subscriptionId: 'other', request: row } });
  buffer.receive({ type: 'connection', value: { subscriptionId: 'mine', state: 'reconnecting' } });
  expect(buffer.bind('mine')).toEqual([{ type: 'connection', value: { subscriptionId: 'mine', state: 'reconnecting' } }]);
  expect(buffer.receive({ type: 'request', value: { subscriptionId: 'other', request: row } })).toEqual([]);
});
it('caps at 1000, deduplicates reconnect backfill and filters method/status/path', () => {
  const model = new RequestModel(); model.add(Array.from({ length: 1100 }, (_, i) => ({ ...row, requestId: String(i), completed: new Date(Date.parse(row.completed) + i * 1000).toISOString() })));
  expect(model.rows).toHaveLength(1000); const newest = model.rows[0]; model.add([newest]); expect(model.rows).toHaveLength(1000);
  expect(matches(row, { method: 'POST', status: '2', path: 'HOOK' })).toBe(true);
  expect(matches(row, { method: 'GET', status: '', path: '' })).toBe(false);
  expect(matches(row, { method: '', status: '5', path: '' })).toBe(false);
});
it('webview smoke renders safely, filters, freezes display and resumes buffered traffic', async () => {
  const dom = new JSDOM('<main id="app"></main>');
  vi.stubGlobal('window', dom.window); vi.stubGlobal('document', dom.window.document);
  const postMessage = vi.fn(); vi.stubGlobal('acquireVsCodeApi', () => ({ postMessage }));
  await import('../../webview/src/inspector.js');
  expect(postMessage).toHaveBeenCalledWith({ type: 'ready' });
  const send = (data: unknown) => dom.window.dispatchEvent(new dom.window.MessageEvent('message', { data }));
  send({ type: 'requests', requests: [{ ...row, path: '<script>bad()</script>' }] });
  expect(dom.window.document.querySelector('tbody')!.textContent).toContain('<script>bad()</script>'); expect(dom.window.document.querySelector('tbody script')).toBeNull();
  dom.window.document.getElementById('pause')!.click(); send({ type: 'request', request: { ...row, requestId: '2' } }); expect(dom.window.document.querySelectorAll('tbody tr')).toHaveLength(1);
  dom.window.document.getElementById('pause')!.click(); expect(dom.window.document.querySelectorAll('tbody tr')).toHaveLength(2);
  const method = dom.window.document.getElementById('method') as HTMLSelectElement; method.value = 'GET'; method.dispatchEvent(new dom.window.Event('input')); expect(dom.window.document.querySelectorAll('tbody tr')).toHaveLength(0);
  send({ type: 'connection', state: 'reconnecting' }); expect(dom.window.document.getElementById('source')!.textContent).toBe('Reconnecting…');
  dom.window.close();
});
