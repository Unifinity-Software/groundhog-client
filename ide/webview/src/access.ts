import { hostAdapter } from './adapter';
const host = hostAdapter();
interface Access { default: { allow: string[] } | null; rules: { path: string; allow: string[] }[]; ipRanges: string[]; apiKeys: { id: string; name: string; created: string }[]; managedBy: string }
const root = document.getElementById('app')!;
root.innerHTML = '<header><div><h1>Visitor access</h1><p id="managed" role="status">Loading…</p></div><button id="handBack" type="button">Hand control back to agents</button></header><p>Allow methods: anyone, members, apikeys, ips. Anyone cannot be combined with another method.</p><form id="policy"><fieldset><legend>Default rule</legend><label>Allowed visitors<input id="default" placeholder="members"></label></fieldset><fieldset><legend>Path rules</legend><div id="rules"></div><button id="add" type="button">Add path rule</button></fieldset><fieldset><legend>IP ranges</legend><label>One IPv4 or IPv6 CIDR range per line<textarea id="ips" rows="3" placeholder="203.0.113.0/24"></textarea></label></fieldset><button type="submit">Save access rules</button></form><section><h2>Visitor API keys</h2><ul id="keys"></ul><label>New key label<input id="key-label" maxlength="100"></label><button id="create" type="button">Create API key</button><p>New keys are shown once through a secure host action. They are never sent to this page.</p></section><p id="operation" role="status"></p>';
const rules = document.getElementById('rules')!;
function addRule(path = '', allow = 'members'): void {
  const row = document.createElement('div'); row.className = 'rule-row';
  const pathLabel = document.createElement('label'); pathLabel.textContent = 'Path prefix'; const pathInput = document.createElement('input'); pathInput.value = path; pathInput.placeholder = '/webhooks'; pathInput.dataset.field = 'path'; pathLabel.append(pathInput);
  const allowLabel = document.createElement('label'); allowLabel.textContent = 'Allowed visitors'; const allowInput = document.createElement('input'); allowInput.value = allow; allowInput.dataset.field = 'allow'; allowLabel.append(allowInput);
  const remove = document.createElement('button'); remove.type = 'button'; remove.textContent = 'Remove rule'; remove.addEventListener('click', () => row.remove()); row.append(pathLabel, allowLabel, remove); rules.append(row);
}
const split = (value: string) => value.split(',').map(part => part.trim()).filter(Boolean);
document.getElementById('add')!.addEventListener('click', () => addRule());
document.getElementById('policy')!.addEventListener('submit', event => {
  event.preventDefault();
  host.postMessage({ type: 'save', default: { allow: split((document.getElementById('default') as HTMLInputElement).value) }, rules: [...rules.querySelectorAll('.rule-row')].map(row => ({ path: (row.querySelector('[data-field="path"]') as HTMLInputElement).value, allow: split((row.querySelector('[data-field="allow"]') as HTMLInputElement).value) })), ipRanges: (document.getElementById('ips') as HTMLTextAreaElement).value.split('\n').map(line => line.trim()).filter(Boolean) });
});
document.getElementById('handBack')!.addEventListener('click', () => host.postMessage({ type: 'handBack' }));
document.getElementById('create')!.addEventListener('click', () => host.postMessage({ type: 'createKey', label: (document.getElementById('key-label') as HTMLInputElement).value }));
host.onMessage(message => {
  const data = message as { type?: string; access?: Access; message?: string };
  if (data.type === 'operation') document.getElementById('operation')!.textContent = data.message ?? '';
  if (data.type !== 'access' || !data.access) return;
  const access = data.access;
  document.getElementById('managed')!.textContent = `Managed by ${access.managedBy === 'agent' ? 'agents' : 'the dashboard'}`;
  (document.getElementById('handBack') as HTMLButtonElement).disabled = access.managedBy === 'agent';
  (document.getElementById('default') as HTMLInputElement).value = (access.default?.allow ?? ['anyone']).join(', ');
  rules.replaceChildren(); access.rules.forEach(rule => addRule(rule.path, rule.allow.join(', ')));
  (document.getElementById('ips') as HTMLTextAreaElement).value = access.ipRanges.join('\n');
  document.getElementById('keys')!.replaceChildren(...access.apiKeys.map(key => {
    const li = document.createElement('li'); const label = document.createElement('span'); label.textContent = key.name + ' · created ' + new Date(key.created).toLocaleDateString();
    const revoke = document.createElement('button'); revoke.type = 'button'; revoke.textContent = 'Revoke'; revoke.setAttribute('aria-label', `Revoke ${key.name}`); revoke.addEventListener('click', () => host.postMessage({ type: 'deleteKey', id: key.id }));
    li.append(label, revoke); return li;
  }));
});
host.postMessage({ type: 'ready' });
