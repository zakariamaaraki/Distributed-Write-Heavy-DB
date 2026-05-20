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
  return String(value).replace(/[&<>"']/g, char => ({
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
