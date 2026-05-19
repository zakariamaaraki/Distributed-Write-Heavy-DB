namespace LsmWriteDb.ChangeLogs;

internal static class ChangeLogConsolePage
{
    public const string Html = """
<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <title>LsmWriteDb Change Log</title>
  <style>
    :root {
      --bg: #f6f7f9;
      --panel: #ffffff;
      --text: #172033;
      --muted: #667085;
      --border: #d9dee8;
      --blue: #2563eb;
      --green: #16803c;
      --red: #c0262d;
      --shadow: 0 14px 34px rgba(18, 24, 38, 0.10);
      --mono: "Cascadia Mono", "SFMono-Regular", Consolas, "Liberation Mono", monospace;
      --sans: Inter, ui-sans-serif, system-ui, -apple-system, BlinkMacSystemFont, "Segoe UI", sans-serif;
    }

    * {
      box-sizing: border-box;
    }

    body {
      margin: 0;
      min-height: 100vh;
      background: var(--bg);
      color: var(--text);
      font-family: var(--sans);
    }

    button,
    input {
      font: inherit;
    }

    button {
      border: 1px solid var(--border);
      border-radius: 8px;
      background: var(--panel);
      color: var(--text);
      cursor: pointer;
      min-height: 36px;
      padding: 0 12px;
    }

    button:hover {
      border-color: #aab4c3;
      background: #f8fafc;
    }

    button:disabled {
      opacity: 0.55;
      cursor: not-allowed;
    }

    .app {
      min-height: 100vh;
      display: grid;
      grid-template-rows: auto auto minmax(0, 1fr);
    }

    .topbar {
      min-height: 72px;
      border-bottom: 1px solid var(--border);
      background: var(--panel);
      display: flex;
      align-items: center;
      justify-content: space-between;
      gap: 16px;
      padding: 14px 22px;
    }

    .brand {
      display: flex;
      align-items: center;
      gap: 12px;
      min-width: 0;
    }

    .mark {
      width: 38px;
      height: 38px;
      border-radius: 8px;
      background: #172033;
      color: #ffffff;
      display: grid;
      place-items: center;
      font-family: var(--mono);
      font-weight: 700;
    }

    h1 {
      margin: 0;
      font-size: 17px;
      line-height: 1.1;
    }

    .brand span {
      display: block;
      color: var(--muted);
      font-size: 12px;
      margin-top: 3px;
    }

    .nav {
      display: flex;
      align-items: center;
      gap: 8px;
      flex-wrap: wrap;
    }

    .nav a {
      color: var(--blue);
      text-decoration: none;
      border: 1px solid var(--border);
      border-radius: 8px;
      padding: 8px 10px;
      background: #ffffff;
      font-size: 13px;
    }

    .controls {
      padding: 16px 22px;
      border-bottom: 1px solid var(--border);
      display: grid;
      grid-template-columns: minmax(220px, 320px) auto auto auto minmax(160px, 1fr);
      gap: 10px;
      align-items: end;
      background: #fbfcfe;
    }

    .field {
      min-width: 0;
    }

    label {
      display: block;
      color: var(--muted);
      font-size: 12px;
      font-weight: 700;
      margin-bottom: 6px;
    }

    input {
      width: 100%;
      min-height: 36px;
      border: 1px solid var(--border);
      border-radius: 8px;
      padding: 7px 9px;
      color: var(--text);
      background: var(--panel);
      font-family: var(--mono);
      font-size: 13px;
    }

    .primary {
      color: #ffffff;
      background: var(--blue);
      border-color: var(--blue);
      font-weight: 700;
    }

    .primary:hover {
      background: #1d4ed8;
      border-color: #1d4ed8;
    }

    .red {
      color: var(--red);
      border-color: rgba(192, 38, 45, 0.35);
      background: rgba(192, 38, 45, 0.08);
    }

    .pill {
      min-height: 30px;
      border: 1px solid var(--border);
      border-radius: 999px;
      display: inline-flex;
      align-items: center;
      gap: 6px;
      padding: 5px 10px;
      color: var(--muted);
      font-size: 12px;
      width: fit-content;
      max-width: 100%;
      overflow-wrap: anywhere;
      background: var(--panel);
    }

    .pill strong {
      color: var(--text);
    }

    .pill.ok {
      color: var(--green);
      border-color: rgba(22, 128, 60, 0.28);
      background: rgba(22, 128, 60, 0.08);
    }

    .pill.live {
      color: var(--blue);
      border-color: rgba(37, 99, 235, 0.28);
      background: rgba(37, 99, 235, 0.08);
    }

    .content {
      min-height: 0;
      padding: 20px 22px;
      overflow: auto;
    }

    .summary {
      display: flex;
      gap: 8px;
      align-items: center;
      flex-wrap: wrap;
      margin-bottom: 14px;
    }

    table {
      width: 100%;
      border-collapse: collapse;
      background: var(--panel);
      border: 1px solid var(--border);
      box-shadow: var(--shadow);
      min-width: 900px;
      font-size: 13px;
    }

    th,
    td {
      padding: 10px 12px;
      border-bottom: 1px solid var(--border);
      text-align: left;
      vertical-align: top;
    }

    th {
      position: sticky;
      top: 0;
      background: #f1f5f9;
      z-index: 1;
      color: #344054;
      font-size: 12px;
    }

    td {
      font-family: var(--mono);
      overflow-wrap: anywhere;
    }

    .empty {
      border: 1px dashed var(--border);
      border-radius: 8px;
      padding: 22px;
      color: var(--muted);
      background: #ffffff;
    }

    @media (max-width: 900px) {
      .topbar,
      .controls {
        align-items: stretch;
        grid-template-columns: 1fr;
      }

      .topbar {
        flex-direction: column;
      }

      .nav,
      .controls button {
        width: 100%;
      }
    }
  </style>
</head>
<body>
  <div class="app">
    <header class="topbar">
      <div class="brand">
        <div class="mark">CDC</div>
        <div>
          <h1>Change Log Stream</h1>
          <span>replay and watch committed kv changes</span>
        </div>
      </div>
      <nav class="nav">
        <a href="/sql-console">SQL console</a>
        <a href="/stats">Stats</a>
      </nav>
    </header>

    <section class="controls">
      <div class="field">
        <label for="fromSequence">Start after sequence</label>
        <input id="fromSequence" type="number" min="0" step="1" value="0">
      </div>
      <button id="connect" class="primary">Connect</button>
      <button id="disconnect" class="red" disabled>Disconnect</button>
      <button id="loadReplay">Load replay</button>
      <span id="status" class="pill">stream <strong>idle</strong></span>
    </section>

    <main class="content">
      <div class="summary">
        <span id="eventCount" class="pill">events <strong>0</strong></span>
        <span id="lastSequence" class="pill">last sequence <strong>0</strong></span>
        <span class="pill">endpoint <strong>/changes/stream</strong></span>
      </div>
      <div id="tableHost" class="empty">No change events loaded.</div>
    </main>
  </div>

  <script>
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
      return value.replace(/[&<>"']/g, char => ({
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
  </script>
</body>
</html>
""";
}
