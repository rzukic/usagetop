'use strict';

// What the plans have been worth: every finished cycle of every long window,
// added up by subscription and by month. The server sends the cycles and the
// price of each account; the adding up happens here, so a month can be read
// either way without a second round trip.

const TOKEN_KEY = 'aiusage-token';
const DAY = 86_400_000;

const $ = id => document.getElementById(id);

function el(tag, className, text) {
  const node = document.createElement(tag);
  if (className) node.className = className;
  if (text != null) node.textContent = text;
  return node;
}

function token() {
  try { return localStorage.getItem(TOKEN_KEY); } catch { return null; }
}

let memoryToken = token();

function setToken(value) {
  try {
    if (value) localStorage.setItem(TOKEN_KEY, value);
    else localStorage.removeItem(TOKEN_KEY);
  } catch { /* A private window: the token lasts as long as the page. */ }
  memoryToken = value;
}

class Unauthorized extends Error {}

async function api(method, path, body) {
  const response = await fetch('/api' + path, {
    method,
    headers: {
      Authorization: 'Bearer ' + (memoryToken || ''),
      ...(body ? { 'Content-Type': 'application/json' } : {}),
    },
    body: body ? JSON.stringify(body) : undefined,
    cache: 'no-store',
  });

  if (response.status === 401) throw new Unauthorized('The server refused that token.');
  if (response.status === 204) return null;

  const answer = await response.json().catch(() => null);
  if (!response.ok) throw new Error((answer && answer.error) || 'The server answered ' + response.status);
  return answer;
}

// ---- Numbers --------------------------------------------------------------

const money = value => '€' + value.toFixed(2);
const percentText = value => Math.round(value) + '%';

const MONTH_FORMAT = new Intl.DateTimeFormat(undefined, { month: 'long', year: 'numeric' });

/** The month a cycle belongs to: the one its middle fell in. */
function monthOf(period) {
  return new Date((Date.parse(period.start) + Date.parse(period.end)) / 2);
}

const monthKey = date => date.getFullYear() * 12 + date.getMonth();

const daysOf = period => (Date.parse(period.end) - Date.parse(period.start)) / DAY;

/** How much of a set of cycles went, weighted by how long each one ran. */
function usedOf(periods) {
  const days = periods.reduce((sum, period) => sum + daysOf(period), 0);
  if (!(days > 0)) return 0;
  return periods.reduce((sum, period) => sum + period.used * daysOf(period), 0) / days;
}

/** One account's cycles, newest month first, each with what it came to. */
function monthsOf(account) {
  const groups = new Map();
  for (const period of account.periods) {
    const date = monthOf(period);
    const key = monthKey(date);
    if (!groups.has(key)) groups.set(key, { date, periods: [] });
    groups.get(key).periods.push(period);
  }

  return [...groups.entries()]
    .sort((a, b) => b[0] - a[0])
    .map(([, group]) => {
      const used = usedOf(group.periods);
      const paid = account.monthly;
      return {
        date: group.date,
        periods: group.periods,
        used,
        paid,
        unused: paid == null ? null : paid * (1 - used / 100),
        partial: group.periods.some(period => !period.complete),
      };
    });
}

/** An account's whole history: what it cost, how much went, what was left. */
function summaryOf(account) {
  const months = monthsOf(account);
  const paid = account.monthly == null ? null : account.monthly * months.length;
  const unused = account.monthly == null
    ? null
    : months.reduce((sum, month) => sum + month.unused, 0);

  return {
    months,
    paid,
    unused,
    used: paid ? 100 * (1 - unused / paid) : usedOf(account.periods),
  };
}

// ---- Drawing --------------------------------------------------------------

function tone(used) {
  return used >= 85 ? 'normal' : used >= 60 ? 'warning' : 'critical';
}

function bar(used) {
  const track = el('div', 'plan-bar');
  const fill = el('div', 'fill-' + tone(used));
  fill.style.width = Math.min(Math.max(used, 0), 100) + '%';
  track.append(fill);
  return track;
}

function figure(value, label) {
  const box = el('div');
  box.append(el('span', 'figure', value), el('span', 'label', label));
  return box;
}

function drawTotals(accounts) {
  const host = $('totals');
  host.replaceChildren();

  const priced = accounts.filter(account => account.monthly != null);
  const paid = priced.reduce((sum, account) => sum + summaryOf(account).paid, 0);
  const unused = priced.reduce((sum, account) => sum + summaryOf(account).unused, 0);
  const used = paid > 0 ? 100 * (1 - unused / paid) : 0;
  const months = new Set();
  for (const account of accounts) {
    for (const month of summaryOf(account).months) months.add(monthKey(month.date));
  }

  const grid = el('div', 'totals');
  grid.append(
    figure(money(paid), 'paid for the plans'),
    figure(percentText(used), 'of them used'),
    figure(money(unused), 'left on the table'),
  );
  host.append(grid);

  const note = accounts.length === 0
    ? 'No finished cycle yet. A plan window has to reset once before it can be counted.'
    : `${accounts.length} subscription${accounts.length === 1 ? '' : 's'} over ${months.size} month${months.size === 1 ? '' : 's'}.`;
  host.append(el('p', 'hint', note));
}

function drawPrice(account) {
  const form = el('form', 'price');
  const input = el('input');
  input.type = 'number';
  input.min = '0';
  input.step = '0.01';
  input.value = account.monthly == null ? '' : String(account.monthly);
  input.placeholder = '—';
  input.setAttribute('aria-label', `Monthly price for ${account.displayName}`);

  const unit = el('span', null, '€/mo');
  const save = el('button', 'link', 'Save');
  save.type = 'submit';

  form.append(input, unit, save);
  if (account.assumed) form.append(el('span', 'assumed', 'assumed'));

  form.addEventListener('submit', async event => {
    event.preventDefault();
    const typed = input.value.trim();
    const monthly = typed === '' ? null : Number(typed);
    if (monthly != null && !(monthly >= 0)) {
      input.setCustomValidity('A price is a number, or empty for none.');
      input.reportValidity();
      return;
    }

    save.disabled = true;
    try {
      await api('PUT', `/plans/price/${encodeURIComponent(account.key)}`, { monthly });
      await load();
    } catch (problem) {
      $('status').textContent = problem.message;
    } finally {
      save.disabled = false;
    }
  });

  return form;
}

function drawSubscription(account) {
  const card = el('div', 'card');
  const summary = summaryOf(account);

  const head = el('div', 'plan-head');
  const mark = providerMark(account.provider);
  if (mark) head.append(mark);
  const name = el('span', 'name', account.displayName);
  head.append(name);
  if (account.plan) head.append(el('span', 'tier', account.plan));
  head.append(drawPrice(account));
  card.append(head);

  const figures = el('div', 'plan-figures');
  if (summary.paid == null) {
    figures.append(el('span', 'muted', 'Set a price to see what it costs.'));
  } else {
    figures.append(
      el('span', null, money(summary.paid) + ' paid'),
      el('span', null, percentText(summary.used) + ' used'),
      el('span', 'unused', money(summary.unused) + ' unused'),
    );
  }
  figures.append(el('span', 'muted', `${account.periods.length} cycle${account.periods.length === 1 ? '' : 's'}`));
  card.append(figures);

  if (account.periods.length > 0) card.append(bar(summary.used));
  return card;
}

function drawMonth(month, accounts) {
  const block = el('div', 'month');
  block.append(el('h3', null, MONTH_FORMAT.format(month.date)));

  for (const { account, month: entry } of accounts) {
    const row = el('div', 'month-row');

    const head = el('div', 'head');
    head.append(el('span', 'name', account.displayName));

    const figures = el('div', 'figures');
    if (entry.paid == null) {
      figures.append(el('span', null, percentText(entry.used) + ' used'));
    } else {
      figures.append(
        el('span', null, money(entry.paid) + ' paid'),
        el('span', null, percentText(entry.used) + ' used'),
        el('span', 'unused', money(entry.unused) + ' unused'),
      );
    }
    if (entry.partial) figures.append(el('span', 'partial', 'partly watched'));
    head.append(figures);
    row.append(head);

    row.append(bar(entry.used));
    block.append(row);
  }

  return block;
}

function drawMonths(accounts) {
  const host = $('months');
  host.replaceChildren();

  // Every month any account has a finished cycle in, newest first.
  const months = new Map();
  for (const account of accounts) {
    for (const month of summaryOf(account).months) {
      const key = monthKey(month.date);
      if (!months.has(key)) months.set(key, { date: month.date, rows: [] });
      months.get(key).rows.push({ account, month });
    }
  }

  if (months.size === 0) {
    host.append(el('p', 'hint', 'Nothing to show yet.'));
    return;
  }

  for (const [, month] of [...months.entries()].sort((a, b) => b[0] - a[0])) {
    month.rows.sort((a, b) => (b.month.paid || 0) - (a.month.paid || 0));
    host.append(drawMonth(month, month.rows));
  }
}

function draw(plans) {
  const accounts = plans.accounts.filter(account => account.periods.length > 0 || account.monthly != null);
  drawTotals(accounts);
  drawMonths(accounts);

  const subs = $('subs');
  subs.replaceChildren();
  for (const account of accounts) subs.append(drawSubscription(account));
}

// ---- Wiring ---------------------------------------------------------------

async function load() {
  const plans = await api('GET', '/plans');
  $('app').hidden = false;
  $('gate').hidden = true;
  $('status').textContent = '';
  draw(plans);
}

function showGate(message) {
  $('app').hidden = true;
  $('gate').hidden = false;
  $('gate-error').textContent = message || '';
  $('gate-token').focus();
}

$('gate').addEventListener('submit', async event => {
  event.preventDefault();
  const typed = $('gate-token').value.trim();
  if (!typed) return;

  setToken(typed);
  try {
    await load();
  } catch (problem) {
    setToken(null);
    showGate(problem.message);
  }
});

$('forget').addEventListener('click', () => {
  setToken(null);
  showGate('');
});

(async () => {
  if (!memoryToken) {
    showGate('');
    return;
  }

  try {
    await load();
  } catch (problem) {
    if (problem instanceof Unauthorized) showGate(problem.message);
    else $('status').textContent = problem.message;
  }
})();
