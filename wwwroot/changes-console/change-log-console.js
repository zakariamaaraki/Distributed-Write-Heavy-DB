const fromSequence = document.getElementById('fromSequence');
const connectButton = document.getElementById('connect');
const disconnectButton = document.getElementById('disconnect');
const loadReplayButton = document.getElementById('loadReplay');
const statusEl = document.getElementById('status');
const eventCountEl = document.getElementById('eventCount');
const lastSequenceEl = document.getElementById('lastSequence');
const tableHost = document.getElementById('tableHost');
let source = null;
let events = [];

function connect() {
  disconnect();
  const start = normalizeSequence(fromSequence.value);
  source = new EventSource(`/changes/stream?fromSequence=${encodeURIComponent(start)}`);
  setStatus('connecting', 'live');
  connectButton.disabled = true;
  disconnectButton.disabled = false;

  source.addEventListener('open', () => setStatus('connected', 'ok'));
  source.addEventListener('change', event => {
    appendEvent(JSON.parse(event.data));
  });
  source.addEventListener('error', () => {
    setStatus('reconnecting', 'live');
  });
}

function disconnect() {
  if (source) {
    source.close();
    source = null;
  }

  setStatus('idle', '');
  connectButton.disabled = false;
  disconnectButton.disabled = true;
}

async function loadReplay() {
  const start = normalizeSequence(fromSequence.value);
  const response = await fetch(`/changes?fromSequence=${encodeURIComponent(start)}&limit=1000`);
  if (!response.ok) {
    setStatus('replay failed', '');
    return;
  }

  events = await response.json();
  render();
  setStatus('replay loaded', 'ok');
}

function appendEvent(entry) {
  events = events.filter(existing => existing.sequence !== entry.sequence);
  events.unshift(entry);
  events = events.slice(0, 500);
  render();
}

function render() {
  eventCountEl.innerHTML = `events <strong>${events.length}</strong>`;
  const maxSequence = events.reduce((max, entry) => Math.max(max, entry.sequence || 0), 0);
  lastSequenceEl.innerHTML = `last sequence <strong>${maxSequence}</strong>`;

  if (events.length === 0) {
    tableHost.className = 'empty';
    tableHost.innerHTML = 'No change events loaded.';
    return;
  }

  tableHost.className = '';
  const rows = events
    .slice()
    .sort((left, right) => (right.sequence || 0) - (left.sequence || 0))
    .map(entry => `
      <tr>
        <td>${escapeHtml(String(entry.sequence ?? ''))}</td>
        <td>${escapeHtml(entry.table ?? 'kv')}</td>
        <td>${escapeHtml(entry.operation ?? '')}</td>
        <td>${escapeHtml(entry.key ?? '')}</td>
        <td>${escapeHtml(entry.value ?? '')}</td>
        <td>${escapeHtml(String(entry.isDeleted ?? false))}</td>
        <td>${escapeHtml(entry.committedAt ?? '')}</td>
      </tr>
    `)
    .join('');

  tableHost.innerHTML = `
    <table>
      <thead>
        <tr>
          <th>Sequence</th>
          <th>Table</th>
          <th>Operation</th>
          <th>Key</th>
          <th>Value</th>
          <th>Deleted</th>
          <th>Committed At</th>
        </tr>
      </thead>
      <tbody>${rows}</tbody>
    </table>
  `;
}

function setStatus(value, mode) {
  statusEl.className = mode ? `pill ${mode}` : 'pill';
  statusEl.innerHTML = `stream <strong>${escapeHtml(value)}</strong>`;
}

function normalizeSequence(value) {
  const number = Number.parseInt(value, 10);
  return Number.isFinite(number) && number > 0 ? number : 0;
}

function escapeHtml(value) {
  return String(value).replace(/[&<>"']/g, char => ({
    '&': '&amp;',
    '<': '&lt;',
    '>': '&gt;',
    '"': '&quot;',
    "'": '&#39;'
  }[char]));
}

connectButton.addEventListener('click', connect);
disconnectButton.addEventListener('click', disconnect);
loadReplayButton.addEventListener('click', loadReplay);
loadReplay();
