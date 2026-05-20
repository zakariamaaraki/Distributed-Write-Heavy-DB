namespace LsmWriteDb.SqlConsole;

internal static class SqlConsolePage
{
    public const string Html = """
<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <title>LsmWriteDb SQL Console</title>
  <style>
    :root {
      color-scheme: light;
      --bg: #f6f7f9;
      --panel: #ffffff;
      --panel-2: #f1f5f9;
      --text: #172033;
      --muted: #667085;
      --border: #d9dee8;
      --blue: #2563eb;
      --green: #16803c;
      --orange: #c2410c;
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
    textarea,
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
      cursor: not-allowed;
      opacity: 0.55;
    }

    .app {
      min-height: 100vh;
      display: grid;
      grid-template-columns: minmax(240px, 300px) minmax(0, 1fr);
    }

    .sidebar {
      border-right: 1px solid var(--border);
      background: #ffffff;
      display: flex;
      flex-direction: column;
      min-width: 0;
    }

    .brand {
      min-height: 72px;
      padding: 16px 18px;
      border-bottom: 1px solid var(--border);
      display: flex;
      align-items: center;
      gap: 12px;
    }

    .mark {
      width: 36px;
      height: 36px;
      border-radius: 8px;
      background: #172033;
      color: #ffffff;
      display: grid;
      place-items: center;
      font-family: var(--mono);
      font-weight: 700;
    }

    .brand h1 {
      margin: 0;
      font-size: 16px;
      line-height: 1.1;
    }

    .brand span {
      display: block;
      color: var(--muted);
      font-size: 12px;
      margin-top: 3px;
    }

    .sidebar-section {
      padding: 14px;
      border-bottom: 1px solid var(--border);
    }

    .label {
      display: block;
      color: var(--muted);
      font-size: 12px;
      font-weight: 700;
      letter-spacing: 0;
      margin-bottom: 8px;
    }

    .transaction-input {
      width: 100%;
      min-height: 36px;
      border: 1px solid var(--border);
      border-radius: 8px;
      padding: 7px 9px;
      color: var(--text);
      background: var(--panel);
      font-family: var(--mono);
      font-size: 12px;
    }

    .transaction-row {
      display: grid;
      grid-template-columns: 1fr auto;
      gap: 8px;
      align-items: center;
    }

    .query-list {
      display: grid;
      gap: 8px;
    }

    .snippet {
      width: 100%;
      text-align: left;
      font-family: var(--mono);
      font-size: 12px;
      line-height: 1.35;
      min-height: 42px;
      padding: 9px;
      white-space: normal;
    }

    .history {
      flex: 1;
      overflow: auto;
      min-height: 140px;
    }

    .history-item {
      border: 1px solid var(--border);
      border-radius: 8px;
      background: var(--panel);
      padding: 9px;
      margin-bottom: 8px;
      text-align: left;
      font-family: var(--mono);
      font-size: 12px;
      line-height: 1.35;
      overflow-wrap: anywhere;
    }

    .history-item .time {
      display: block;
      color: var(--muted);
      font-family: var(--sans);
      font-size: 11px;
      margin-top: 5px;
    }

    .main {
      min-width: 0;
      display: grid;
      grid-template-rows: auto minmax(300px, 42vh) 1fr;
    }

    .topbar {
      min-height: 72px;
      padding: 14px 20px;
      border-bottom: 1px solid var(--border);
      background: rgba(255, 255, 255, 0.92);
      display: flex;
      align-items: center;
      justify-content: space-between;
      gap: 14px;
    }

    .status-group {
      display: flex;
      gap: 8px;
      align-items: center;
      flex-wrap: wrap;
      min-width: 0;
    }

    .pill {
      border: 1px solid var(--border);
      border-radius: 999px;
      background: var(--panel);
      min-height: 30px;
      padding: 5px 10px;
      color: var(--muted);
      font-size: 12px;
      display: inline-flex;
      align-items: center;
      gap: 6px;
      max-width: 100%;
    }

    .pill strong {
      color: var(--text);
      font-weight: 700;
      overflow-wrap: anywhere;
    }

    a.pill {
      text-decoration: none;
    }

    .pill.ok {
      color: var(--green);
      border-color: rgba(22, 128, 60, 0.28);
      background: rgba(22, 128, 60, 0.08);
    }

    .pill.warn {
      color: var(--orange);
      border-color: rgba(194, 65, 12, 0.28);
      background: rgba(194, 65, 12, 0.08);
    }

    .editor-shell {
      padding: 20px;
      min-height: 0;
      display: flex;
      flex-direction: column;
      gap: 12px;
    }

    .toolbar {
      display: flex;
      align-items: center;
      justify-content: space-between;
      gap: 12px;
      flex-wrap: wrap;
    }

    .toolbar-group {
      display: flex;
      align-items: center;
      gap: 8px;
      flex-wrap: wrap;
    }

    .primary {
      background: var(--blue);
      color: #ffffff;
      border-color: var(--blue);
      font-weight: 700;
    }

    .primary:hover {
      background: #1d4ed8;
      border-color: #1d4ed8;
    }

    .green {
      color: var(--green);
      border-color: rgba(22, 128, 60, 0.35);
      background: rgba(22, 128, 60, 0.08);
    }

    .orange {
      color: var(--orange);
      border-color: rgba(194, 65, 12, 0.35);
      background: rgba(194, 65, 12, 0.08);
    }

    .red {
      color: var(--red);
      border-color: rgba(192, 38, 45, 0.35);
      background: rgba(192, 38, 45, 0.08);
    }

    .editor {
      flex: 1;
      min-height: 220px;
      width: 100%;
      resize: none;
      border: 1px solid var(--border);
      border-radius: 8px;
      background: #111827;
      color: #e5e7eb;
      padding: 16px;
      font-family: var(--mono);
      font-size: 14px;
      line-height: 1.55;
      box-shadow: var(--shadow);
      outline: none;
    }

    .editor:focus {
      border-color: #7aa2f7;
    }

    .results {
      min-height: 0;
      border-top: 1px solid var(--border);
      background: var(--panel);
      display: grid;
      grid-template-rows: auto 1fr;
    }

    .results-head {
      min-height: 52px;
      padding: 10px 20px;
      border-bottom: 1px solid var(--border);
      display: flex;
      align-items: center;
      justify-content: space-between;
      gap: 12px;
      flex-wrap: wrap;
    }

    .results-head h2 {
      margin: 0;
      font-size: 15px;
    }

    .result-meta {
      display: flex;
      align-items: center;
      gap: 8px;
      flex-wrap: wrap;
    }

    .result-body {
      min-height: 0;
      overflow: auto;
      padding: 16px 20px 22px;
    }

    table {
      width: 100%;
      border-collapse: collapse;
      min-width: 520px;
      font-size: 13px;
    }

    th,
    td {
      border-bottom: 1px solid var(--border);
      text-align: left;
      vertical-align: top;
      padding: 10px 12px;
    }

    th {
      position: sticky;
      top: 0;
      background: var(--panel-2);
      font-size: 12px;
      color: #344054;
      z-index: 1;
    }

    td {
      font-family: var(--mono);
      overflow-wrap: anywhere;
    }

    pre {
      margin: 0;
      border: 1px solid var(--border);
      border-radius: 8px;
      background: #0f172a;
      color: #e5e7eb;
      padding: 14px;
      overflow: auto;
      font-family: var(--mono);
      font-size: 12px;
      line-height: 1.5;
    }

    .empty {
      border: 1px dashed var(--border);
      border-radius: 8px;
      padding: 22px;
      color: var(--muted);
      background: #fafafa;
    }

    .toast {
      position: fixed;
      right: 18px;
      bottom: 18px;
      max-width: min(420px, calc(100vw - 36px));
      border-radius: 8px;
      border: 1px solid var(--border);
      background: var(--panel);
      color: var(--text);
      box-shadow: var(--shadow);
      padding: 12px 14px;
      display: none;
      font-size: 13px;
      line-height: 1.4;
      overflow-wrap: anywhere;
      z-index: 10;
    }

    .toast.show {
      display: block;
    }

    .toast.error {
      border-color: rgba(192, 38, 45, 0.35);
      color: var(--red);
    }

    @media (max-width: 900px) {
      .app {
        grid-template-columns: 1fr;
      }

      .sidebar {
        border-right: 0;
        border-bottom: 1px solid var(--border);
        max-height: 42vh;
      }

      .main {
        grid-template-rows: auto minmax(340px, 48vh) 1fr;
      }
    }

    @media (max-width: 560px) {
      .topbar,
      .editor-shell,
      .results-head,
      .result-body {
        padding-left: 12px;
        padding-right: 12px;
      }

      .toolbar,
      .topbar {
        align-items: stretch;
      }

      .toolbar-group,
      .status-group {
        width: 100%;
      }

      .toolbar-group button {
        flex: 1 1 auto;
      }
    }
  </style>
</head>
<body>
  <div class="app">
    <aside class="sidebar">
      <div class="brand">
        <div class="mark">SQL</div>
        <div>
          <h1>LsmWriteDb Console</h1>
          <span>kv query workspace</span>
        </div>
      </div>

      <section class="sidebar-section">
        <label class="label" for="transactionId">Transaction</label>
        <div class="transaction-row">
          <input class="transaction-input" id="transactionId" placeholder="none" spellcheck="false">
          <button id="clearTransaction" title="Clear transaction">Clear</button>
        </div>
      </section>

      <section class="sidebar-section">
        <span class="label">Suggested queries</span>
        <div class="query-list">
          <button class="snippet" data-query="CREATE TABLE users">CREATE TABLE users</button>
          <button class="snippet" data-query="INSERT INTO users (key, value) VALUES ('user:1001', '{&quot;name&quot;:&quot;Ada&quot;}')">INSERT INTO users (key, value) VALUES ('user:1001', '{"name":"Ada"}')</button>
          <button class="snippet" data-query="SELECT key, value FROM users WHERE key BETWEEN 'user:1000' AND 'user:1999' LIMIT 50">SELECT key, value FROM users WHERE key BETWEEN 'user:1000' AND 'user:1999' LIMIT 50</button>
          <button class="snippet" data-query="SELECT * FROM kv WHERE key = 'alpha'">SELECT * FROM kv WHERE key = 'alpha'</button>
          <button class="snippet" data-query="SELECT key, value FROM kv WHERE key BETWEEN 'a' AND 'z' LIMIT 100">SELECT key, value FROM kv WHERE key BETWEEN 'a' AND 'z' LIMIT 100</button>
          <button class="snippet" data-query="SELECT key, value FROM kv WHERE key >= 'user:1000' AND key <= 'user:1999' LIMIT 50">SELECT key, value FROM kv WHERE key >= 'user:1000' AND key <= 'user:1999' LIMIT 50</button>
          <button class="snippet" data-query="INSERT INTO kv (key, value) VALUES ('alpha', 'one')">INSERT INTO kv (key, value) VALUES ('alpha', 'one')</button>
          <button class="snippet" data-query="UPDATE kv SET value = 'updated' WHERE key = 'alpha'">UPDATE kv SET value = 'updated' WHERE key = 'alpha'</button>
          <button class="snippet" data-query="DELETE FROM kv WHERE key = 'alpha'">DELETE FROM kv WHERE key = 'alpha'</button>
          <button class="snippet" data-query="BEGIN TRANSACTION">BEGIN TRANSACTION</button>
          <button class="snippet" data-query="COMMIT TRANSACTION">COMMIT TRANSACTION</button>
          <button class="snippet" data-query="ROLLBACK TRANSACTION">ROLLBACK TRANSACTION</button>
        </div>
      </section>

      <section class="sidebar-section history">
        <span class="label">History</span>
        <div id="history"></div>
      </section>
    </aside>

    <main class="main">
      <header class="topbar">
        <div class="status-group">
          <span id="serviceStatus" class="pill warn">service <strong>checking</strong></span>
          <span id="transactionStatus" class="pill">transaction <strong>none</strong></span>
        </div>
        <div class="status-group">
          <span class="pill">endpoint <strong>/sql</strong></span>
          <span class="pill">table <strong>kv</strong></span>
          <a class="pill" href="/changes-console">changes <strong>stream</strong></a>
        </div>
      </header>

      <section class="editor-shell">
        <div class="toolbar">
          <div class="toolbar-group">
            <button id="runQuery" class="primary">Run</button>
            <button id="beginTransaction" class="green">Begin</button>
            <button id="commitTransaction" class="orange">Commit</button>
            <button id="rollbackTransaction" class="red">Rollback</button>
          </div>
          <div class="toolbar-group">
            <button id="formatQuery">Format</button>
            <button id="clearEditor">Clear</button>
          </div>
        </div>
        <textarea id="queryEditor" class="editor" spellcheck="false">CREATE TABLE users</textarea>
      </section>

      <section class="results">
        <div class="results-head">
          <h2>Result</h2>
          <div class="result-meta">
            <span id="statementType" class="pill">statement <strong>none</strong></span>
            <span id="rowsAffected" class="pill">rows <strong>0</strong></span>
            <span id="elapsedTime" class="pill">time <strong>0 ms</strong></span>
          </div>
        </div>
        <div id="resultBody" class="result-body">
          <div class="empty">No result yet.</div>
        </div>
      </section>
    </main>
  </div>

  <div id="toast" class="toast"></div>

  <script>
    const editor = document.getElementById('queryEditor');
    const transactionInput = document.getElementById('transactionId');
    const transactionStatus = document.getElementById('transactionStatus');
    const serviceStatus = document.getElementById('serviceStatus');
    const resultBody = document.getElementById('resultBody');
    const statementType = document.getElementById('statementType');
    const rowsAffected = document.getElementById('rowsAffected');
    const elapsedTime = document.getElementById('elapsedTime');
    const historyEl = document.getElementById('history');
    const toast = document.getElementById('toast');
    const historyKey = 'lsmwrite-sql-console-history';

    function activeTransactionId() {
      const value = transactionInput.value.trim();
      return value.length === 0 ? null : value;
    }

    function setTransactionId(value) {
      transactionInput.value = value || '';
      renderTransactionStatus();
    }

    function renderTransactionStatus() {
      const id = activeTransactionId();
      transactionStatus.className = id ? 'pill ok' : 'pill';
      transactionStatus.innerHTML = id
        ? `transaction <strong>${escapeHtml(id)}</strong>`
        : 'transaction <strong>none</strong>';
    }

    async function checkService() {
      try {
        const response = await fetch('/stats');
        serviceStatus.className = response.ok ? 'pill ok' : 'pill warn';
        serviceStatus.innerHTML = response.ok ? 'service <strong>ready</strong>' : 'service <strong>error</strong>';
      } catch {
        serviceStatus.className = 'pill warn';
        serviceStatus.innerHTML = 'service <strong>offline</strong>';
      }
    }

    async function execute(query) {
      const sql = (query ?? editor.value).trim();
      if (!sql) {
        showToast('SQL query is required.', true);
        return;
      }

      const started = performance.now();
      setBusy(true);

      try {
        const response = await fetch('/sql', {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({
            query: sql,
            transactionId: activeTransactionId()
          })
        });

        const payload = await response.json().catch(() => ({}));
        const elapsed = Math.max(0, Math.round(performance.now() - started));

        if (!response.ok) {
          renderError(payload.error || response.statusText || 'SQL request failed.', elapsed);
          showToast(payload.error || 'SQL request failed.', true);
          return;
        }

        if (payload.transactionId) {
          if (payload.statementType === 'ROLLBACK' || payload.statementType === 'COMMIT') {
            setTransactionId('');
          } else {
            setTransactionId(payload.transactionId);
          }
        }

        renderResult(payload, elapsed);
        saveHistory(sql);
      } catch (error) {
        renderError(error.message || 'Network error.', Math.max(0, Math.round(performance.now() - started)));
        showToast(error.message || 'Network error.', true);
      } finally {
        setBusy(false);
      }
    }

    function renderResult(payload, elapsed) {
      statementType.innerHTML = `statement <strong>${escapeHtml(payload.statementType || 'unknown')}</strong>`;
      rowsAffected.innerHTML = `rows <strong>${Number(payload.rowsAffected || 0)}</strong>`;
      elapsedTime.innerHTML = `time <strong>${elapsed} ms</strong>`;

      const rows = Array.isArray(payload.rows) ? payload.rows : [];
      if (rows.length === 0) {
        const message = payload.message ? escapeHtml(payload.message) : 'Statement completed.';
        resultBody.innerHTML = `<div class="empty">${message}</div><pre>${escapeHtml(JSON.stringify(payload, null, 2))}</pre>`;
        return;
      }

      const columns = Array.from(rows.reduce((set, row) => {
        Object.keys(row).forEach(key => set.add(key));
        return set;
      }, new Set()));

      const head = columns.map(column => `<th>${escapeHtml(column)}</th>`).join('');
      const body = rows.map(row => {
        const cells = columns.map(column => `<td>${escapeHtml(String(row[column] ?? ''))}</td>`).join('');
        return `<tr>${cells}</tr>`;
      }).join('');

      resultBody.innerHTML = `<table><thead><tr>${head}</tr></thead><tbody>${body}</tbody></table>`;
    }

    function renderError(message, elapsed) {
      statementType.innerHTML = 'statement <strong>error</strong>';
      rowsAffected.innerHTML = 'rows <strong>0</strong>';
      elapsedTime.innerHTML = `time <strong>${elapsed} ms</strong>`;
      resultBody.innerHTML = `<pre>${escapeHtml(message)}</pre>`;
    }

    function setBusy(isBusy) {
      document.querySelectorAll('button').forEach(button => {
        button.disabled = isBusy;
      });
    }

    function saveHistory(sql) {
      const history = loadHistory().filter(item => item.sql !== sql);
      history.unshift({ sql, at: new Date().toLocaleTimeString() });
      localStorage.setItem(historyKey, JSON.stringify(history.slice(0, 12)));
      renderHistory();
    }

    function loadHistory() {
      try {
        const value = JSON.parse(localStorage.getItem(historyKey) || '[]');
        return Array.isArray(value) ? value : [];
      } catch {
        return [];
      }
    }

    function renderHistory() {
      const history = loadHistory();
      if (history.length === 0) {
        historyEl.innerHTML = '<div class="empty">No history.</div>';
        return;
      }

      historyEl.innerHTML = history.map(item => (
        `<button class="history-item" data-history="${escapeAttribute(item.sql)}">${escapeHtml(item.sql)}<span class="time">${escapeHtml(item.at)}</span></button>`
      )).join('');
    }

    function showToast(message, isError = false) {
      toast.textContent = message;
      toast.className = isError ? 'toast show error' : 'toast show';
      clearTimeout(showToast.timer);
      showToast.timer = setTimeout(() => {
        toast.className = 'toast';
      }, 3800);
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

    function escapeAttribute(value) {
      return escapeHtml(value).replace(/`/g, '&#96;');
    }

    function compactSql(value) {
      return value
        .replace(/\s+/g, ' ')
        .replace(/\s*,\s*/g, ', ')
        .replace(/\s*(>=|<=|=)\s*/g, ' $1 ')
        .trim();
    }

    document.getElementById('runQuery').addEventListener('click', () => execute());
    document.getElementById('beginTransaction').addEventListener('click', () => execute('BEGIN'));
    document.getElementById('commitTransaction').addEventListener('click', () => execute('COMMIT'));
    document.getElementById('rollbackTransaction').addEventListener('click', () => execute('ROLLBACK'));
    document.getElementById('clearTransaction').addEventListener('click', () => setTransactionId(''));
    document.getElementById('clearEditor').addEventListener('click', () => {
      editor.value = '';
      editor.focus();
    });
    document.getElementById('formatQuery').addEventListener('click', () => {
      editor.value = compactSql(editor.value);
      editor.focus();
    });

    document.querySelectorAll('[data-query]').forEach(button => {
      button.addEventListener('click', () => {
        editor.value = button.dataset.query;
        editor.focus();
      });
    });

    historyEl.addEventListener('click', event => {
      const button = event.target.closest('[data-history]');
      if (!button) {
        return;
      }

      editor.value = button.dataset.history;
      editor.focus();
    });

    editor.addEventListener('keydown', event => {
      if ((event.ctrlKey || event.metaKey) && event.key === 'Enter') {
        event.preventDefault();
        execute();
      }
    });

    transactionInput.addEventListener('input', renderTransactionStatus);

    renderTransactionStatus();
    renderHistory();
    checkService();
  </script>
</body>
</html>
""";
}
