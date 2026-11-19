'use strict';

// The server's own page: the same cards and charts the apps draw, plus the
// sign-ins. Everything that came from a provider is written with textContent,
// never as markup.

const PROVIDERS = [
  { key: 'claude', name: 'Claude', kind: 'signin' },
  { key: 'chatgpt', name: 'ChatGPT', kind: 'signin' },
  { key: 'mistral', name: 'Mistral', kind: 'key' },
  { key: 'kimi', name: 'Kimi', kind: 'key' },
  { key: 'muse', name: 'Muse', kind: 'signin' },
  { key: 'antigravity', name: 'Antigravity', kind: 'signin' },
];

const TOKEN_KEY = 'aiusage-token';
const REFRESH_MS = 60_000;
const DAY = 86_400_000;
const SVG = 'http://www.w3.org/2000/svg';

const $ = id => document.getElementById(id);

let state = null;
let timer = null;
let chartCount = 0;

/** How far back the combined chart looks; the per-window charts follow their cycle. */
const RANGES = [{ label: '24h', days: 1 }, { label: '7d', days: 7 }, { label: '35d', days: 35 }];
let combinedDays = 35;

// ---- Small helpers --------------------------------------------------------

function el(tag, className, text) {
  const node = document.createElement(tag);
  if (className) node.className = className;
  if (text != null) node.textContent = text;
  return node;
}

function svg(tag, attributes) {
  const node = document.createElementNS(SVG, tag);
  for (const [name, value] of Object.entries(attributes || {})) node.setAttribute(name, value);
  return node;
}

function token() {
  try { return localStorage.getItem(TOKEN_KEY); } catch { return null; }
}

function setToken(value) {
  try {
    if (value) localStorage.setItem(TOKEN_KEY, value);
    else localStorage.removeItem(TOKEN_KEY);
  } catch { /* A private window: the token lasts as long as the page. */ }
  memoryToken = value;
}

let memoryToken = token();

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

function severity(percent) {
  return percent <= 15 ? 'critical' : percent <= 40 ? 'warning' : 'normal';
}

function span(ms) {
  const minutes = Math.floor(ms / 60_000);
  const hours = Math.floor(minutes / 60);
  if (hours >= 24) {
    const rest = hours % 24;
    return rest ? `${Math.floor(hours / 24)}d ${rest}h` : `${Math.floor(hours / 24)}d`;
  }
  return hours >= 1 ? `${hours}h ${minutes % 60}m` : `${minutes}m`;
}

function ago(iso) {
  const minutes = Math.floor((Date.now() - Date.parse(iso)) / 60_000);
  if (!(minutes >= 1)) return 'just now';
  return minutes < 60 ? `${minutes}m ago` : `${Math.floor(minutes / 60)}h ago`;
}

const percentText = value => Math.round(value) + '%';

const dayFormat = new Intl.DateTimeFormat(undefined, { day: 'numeric', month: 'short' });
const momentFormat = new Intl.DateTimeFormat(undefined, {
  day: 'numeric', month: 'short', hour: '2-digit', minute: '2-digit',
});
const clockFormat = new Intl.DateTimeFormat(undefined, { hour: '2-digit', minute: '2-digit' });

/**
 * Round times along the bottom: as many as fit without crowding, on the hour
 * or at midnight, labelled with the time or the day as the span suggests.
 */
function ticks(from, to, plotWidth) {
  const hour = 3_600_000;
  const steps = [hour / 4, hour / 2, hour, 3 * hour, 6 * hour, 12 * hour, DAY, 2 * DAY, 7 * DAY];
  const most = Math.max(2, Math.floor(plotWidth / 90));
  const step = steps.find(candidate => (to - from) / candidate <= most) || 14 * DAY;

  // Steps are aligned to local midnight, so "6h" lands on 00, 06, 12, 18.
  const midnight = new Date(from);
  midnight.setHours(0, 0, 0, 0);
  const list = [];
  for (let at = midnight.getTime(); at <= to; at += step) {
    if (at < from) continue;
    const local = new Date(at);
    const atMidnight = local.getHours() === 0 && local.getMinutes() === 0;
    list.push({ at, text: step < DAY && !atMidnight ? clockFormat.format(at) : dayFormat.format(at) });
  }
  return list;
}

/** A window that refills within a week charts a week; anything slower, five weeks. */
/** "Claude", "Claude · Work", or "Claude #2" for an added account with no name. */
function accountName(usage) {
  const provider = PROVIDERS.find(p => p.key === usage.id);
  const base = provider ? provider.name : usage.id;
  if (usage.accountName) return `${base} · ${usage.accountName}`;
  const account = usage.account || usage.id;
  return account === usage.id ? base : `${base} #${account.slice(usage.id.length + 1)}`;
}

/** Series are filed by account, so two accounts of one provider never share a chart. */
const seriesKey = (usage, label) => `${usage.account || usage.id}|${label}`;

/**
 * How long one cycle of the window lasts: from its label ("5h", "Fable 7d",
 * "Weekly"), or for "Monthly" the calendar month before its reset. Mistral's
 * two budgets, "Vibe" and "API", name no duration but run on its monthly
 * billing cycle. Null when nothing says.
 */
function periodOf(window) {
  const text = window.label.trim().toLowerCase();
  if (text === 'weekly') return 7 * DAY;
  if (text === 'monthly' || text === 'vibe' || text === 'api') {
    if (!window.resetAt) return 30 * DAY;
    const end = new Date(window.resetAt);
    const start = new Date(end);
    start.setMonth(start.getMonth() - 1);
    return end - start;
  }
  const match = /(?:^|\s)(\d+)\s*([mhd])$/.exec(text);
  if (!match) return null;
  return Number(match[1]) * { m: 60_000, h: 3_600_000, d: DAY }[match[2]];
}

/**
 * What would be left now had the window been spent evenly since it refilled:
 * the share of the cycle still to run. Null without a length and a reset, or
 * when the reset is out of step with the length.
 */
function evenPercent(window, now = Date.now()) {
  const length = periodOf(window);
  if (!length || !window.resetAt) return null;
  const left = Date.parse(window.resetAt) - now;
  if (!(left > 0) || left > length) return null;
  return left / length * 100;
}

/** Ahead of pace runs out before the reset; under pace has room to spare. Five points either way is on pace. */
function paceOf(window, now = Date.now()) {
  const even = evenPercent(window, now);
  if (even == null || window.remainingPercent == null) return null;
  const gap = window.remainingPercent - even;
  return gap < -5 ? 'ahead' : gap > 5 ? 'under' : 'on';
}

const PACE = {
  ahead: { text: 'ahead of pace', title: 'Used faster than an even spread: at this rate it runs out before it resets.' },
  on: { text: 'on pace', title: 'About what an even spread over the cycle would leave.' },
  under: { text: 'under pace', title: 'Used slower than an even spread: room to spare before it resets.' },
};

/** The bar with a tick where an even spread would be, as in the cards and the list. */
function meterWithPace(percent, tone, window) {
  const meter = el('div', 'meter');
  const fill = el('div', 'fill-' + tone);
  fill.style.width = Math.min(Math.max(percent, 0), 100) + '%';
  meter.append(fill);
  const even = evenPercent(window);
  if (even != null) {
    const tick = el('span', 'pace-tick');
    tick.style.left = even + '%';
    tick.title = `Even pace: ${percentText(even)} left`;
    meter.append(tick);
  }
  return meter;
}

// ---- Burn rate and forecast -------------------------------------------------

const HOUR = 3_600_000;
const BURN_KEY = 'aiusage-burn-hours';
const BURNS = [1, 3, 6];
let burnHours = (() => {
  try { return BURNS.includes(Number(localStorage.getItem(BURN_KEY))) ? Number(localStorage.getItem(BURN_KEY)) : 3; }
  catch { return 3; }
})();

/** A week of readings in full, for burn rates; the long series are thinned out. */
let recent = {};

// Limits left out of the cards and the closest-to-empty list, as the server
// keeps them for every app ("account|label"); and the cards whose hidden
// limits have been opened up here.
const hiddenLimits = () => new Set((state && state.hiddenLimits) || []);
const showingHidden = new Set();

/** An eye, struck through when the limit is hidden; clicking it flips that. */
function eyeButton(key, label, hidden) {
  const button = el('button', 'eye');
  button.type = 'button';
  button.title = hidden ? 'Hidden from the overview in every app. Click to show it again.'
    : 'Shown in every app. Click to hide it from their overviews.';
  button.setAttribute('aria-label', (hidden ? 'Show ' : 'Hide ') + label);
  const icon = svg('svg', { viewBox: '0 0 24 24', width: '14', height: '14', 'aria-hidden': 'true' });
  icon.append(svg('path', { d: 'M2 12s3.6-6.5 10-6.5S22 12 22 12s-3.6 6.5-10 6.5S2 12 2 12z' }));
  icon.append(svg('circle', { cx: '12', cy: '12', r: '2.8' }));
  if (hidden) icon.append(svg('path', { d: 'M4 4l16 16' }));
  button.append(icon);
  button.addEventListener('click', async () => {
    button.disabled = true;
    try {
      state.hiddenLimits = await api(hidden ? 'DELETE' : 'PUT', '/hidden?limit=' + encodeURIComponent(key));
      renderUsage();
    } catch (problem) {
      button.disabled = false;
      $('status').textContent = problem.message;
    }
  });
  return button;
}

/** How far back the usual rate looks: long enough to take in the nights. */
const USUAL_HOURS = 7 * 24;
/** Below this, in points an hour, a window counts as not draining. */
const IDLE = 0.05;
/** A jump this large is a reset, not a reading drifting. */
const REFILL_JUMP = 1.5;
/** How far ahead a window is looked at for when it runs dry. */
const HORIZON = 35 * DAY;

/**
 * Points of remaining-percent drained per hour over the last `hours`, refills
 * left out. A level holds until the next reading, and the last one until now.
 * Null with under half an hour (or half the span, if shorter) to go on.
 * The apps take it the same way (Projection.RateOver).
 */
function rateOver(points, hours, now = Date.now()) {
  if (!points || !points.length) return null;
  const cutoff = now - hours * HOUR;
  const start = Math.max(points[0][0], cutoff);
  const elapsed = (now - start) / HOUR;
  if (elapsed < Math.min(0.5, hours / 2)) return null;
  let drained = 0;
  for (let i = 1; i < points.length; i++) {
    if (points[i][0] <= cutoff || points[i][0] > now) continue;
    const step = points[i - 1][1] - points[i][1];
    if (-step >= REFILL_JUMP) continue;
    drained += step;
  }
  return Math.max(drained / elapsed, 0);
}

/**
 * One window and how fast it is being spent: the recent rate (over the hours
 * picked below the cards) and the usual one (over the week). The forecast
 * leaves now at the recent rate and eases into the usual one,
 *
 *   rate(t) = usual + (recent - usual) * e^(-t / ease)
 *
 * with the ease as long as the recent rate was measured over: an hour of heavy
 * work says something about the next few hours and little about the week, and
 * a quiet lunch does not mean the window lasts for ever. The apps draw the same
 * curve (Projection.cs, Projection.kt).
 */
function trackOf(window, points, now = Date.now()) {
  if (window.remainingPercent == null) return null;
  const reset = window.resetAt ? Date.parse(window.resetAt) : NaN;
  return {
    recent: rateOver(points, burnHours, now),
    usual: rateOver(points, USUAL_HOURS, now),
    level: window.remainingPercent,
    at: now,
    reset: reset > now ? reset : null,
    period: periodOf(window),
    ease: burnHours,
  };
}

const measured = track => track.recent != null || track.usual != null;
const draining = track => (track.recent || 0) > IDLE || (track.usual || 0) > IDLE;

/** Points drained from the track's now until `hours` later. */
function drained(track, hours) {
  if (hours <= 0) return 0;
  const recent = track.recent ?? track.usual ?? 0;
  const usual = track.usual ?? track.recent ?? 0;
  return usual * hours + (recent - usual) * track.ease * (1 - Math.exp(-hours / track.ease));
}

/** The last refill at or before t, or null if none has come yet. */
function cycleStart(track, t) {
  if (!track.reset || t < track.reset) return null;
  if (!track.period) return track.reset;
  return track.reset + Math.floor((t - track.reset) / track.period) * track.period;
}

/** Where the window stands at t, refilling at every reset on the way. */
function levelAt(track, t) {
  if (t <= track.at) return track.level;
  const spent = drained(track, (t - track.at) / HOUR);
  const start = cycleStart(track, t);
  if (start == null) return Math.max(track.level - spent, 0);
  return Math.max(100 - (spent - drained(track, (start - track.at) / HOUR)), 0);
}

/** When the window runs dry before its next refill, looking no further than `until`. */
function emptyAt(track, until) {
  if (track.level <= 0) return track.at;
  if (!draining(track)) return null;
  const end = track.reset && track.reset < until ? track.reset : until;
  if (end <= track.at || track.level - drained(track, (end - track.at) / HOUR) > 0) return null;
  // The level only falls between refills, so halving the gap finds it.
  let low = 0, high = (end - track.at) / HOUR;
  for (let i = 0; i < 48; i++) {
    const middle = (low + high) / 2;
    if (track.level - drained(track, middle) > 0) low = middle; else high = middle;
  }
  return track.at + high * HOUR;
}

/** Every refill of the window after `from`, up to `to`. */
function resetsOf(track, from, to) {
  const list = [];
  for (let at = track.reset, n = 0; at && at <= to && n < 1000; at = track.period ? at + track.period : null, n++) {
    if (at > from) list.push(at);
  }
  return list;
}

/**
 * The forecast line from `from` to `to`: evenly spaced samples, plus both sides
 * of every refill so it steps straight up.
 */
function sampled(tracks, from, to, value) {
  if (to <= from) return [];
  const times = new Set();
  const STEPS = 160;
  for (let i = 0; i <= STEPS; i++) times.add(from + (to - from) * i / STEPS);
  for (const track of tracks) {
    for (const at of resetsOf(track, from, to)) {
      if (at - 1 > from) times.add(at - 1);
      times.add(at);
    }
  }
  return [...times].sort((a, b) => a - b).map(t => [t, value(t)]);
}

/** The forecast for one window's card, or null with no readings to go on. */
function forecastOf(window, points, now = Date.now()) {
  const track = trackOf(window, points, now);
  if (!track || !measured(track)) return null;
  return {
    recent: track.recent,
    usual: track.usual,
    level: track.level,
    emptyAt: emptyAt(track, now + HORIZON),
    reset: track.reset,
    steps: (from, to) => sampled([track], from, to, t => levelAt(track, t)),
  };
}

/**
 * The readings to take a burn rate from: the last week in full, after the
 * thinned history up to it. Readings are kept only when the level moves, so
 * the level going into the week is in the history, not the recent part.
 */
function burnPoints(key) {
  const long = (state && state.series[key]) || [];
  const short = recent[key] || [];
  if (!short.length) return long;
  return long.filter(point => point[0] < short[0][0]).concat(short);
}

// ---- The combined forecast ------------------------------------------------

/** "5h" and other windows shorter than a day: left out of the combined figure. */
function isShortWindow(window) {
  const match = /^(\d+)\s*([mhd])$/i.exec(window.label.trim());
  if (!match) return false;
  return Number(match[1]) * { m: 1 / 60, h: 1, d: 24 }[match[2].toLowerCase()] < 24;
}

/**
 * The windows that make up a subscription's share of the combined figure,
 * chosen as the server's CombinedUsage.LongWindows chooses them: the long
 * ones that count, of the tier that resets furthest away.
 */
function longWindows(usage) {
  const counted = usage.windows.filter(w => w.remainingPercent != null && w.countsTowardCombined !== false);
  const long = counted.filter(w => !isShortWindow(w));
  const rated = long.length ? long : counted;
  const resets = rated.filter(w => w.resetAt).map(w => Date.parse(w.resetAt));
  if (!resets.length) return rated;
  const furthest = Math.max(...resets);
  return rated.filter(w => !w.resetAt || furthest - Date.parse(w.resetAt) <= DAY);
}

const average = values => values.reduce((sum, v) => sum + v, 0) / values.length;

/**
 * The combined figure carried forward: every window in it drains on its own
 * forecast and refills at its own reset (and every cycle after, where its
 * length is known), then they are averaged as the server averages them: each
 * subscription's windows first, then the subscriptions.
 */
function combinedForecast(snapshot, now = Date.now()) {
  const parts = snapshot.usages
    .filter(usage => usage.state === 'OK')
    .map(usage => longWindows(usage).map(window => ({
      name: `${accountName(usage)} ${window.label}`,
      ...trackOf(window, burnPoints(seriesKey(usage, window.label)), now),
    })))
    .filter(windows => windows.length);
  if (!parts.length) return null;
  const all = parts.flat();
  if (!all.some(measured)) return null;

  const valueAt = t => average(parts.map(windows => average(windows.map(w => levelAt(w, t)))));
  const steps = (from, to) => sampled(all, from, to, valueAt);

  // Dry only if every window is at once, before any refill: looked for over five
  // weeks, then pinned down between the two samples either side of it.
  let dry = null;
  const ahead = steps(now, now + HORIZON);
  const index = ahead.findIndex(point => point[1] <= IDLE);
  if (index === 0) dry = now;
  else if (index > 0) {
    let low = ahead[index - 1][0], high = ahead[index][0];
    for (let i = 0; i < 40; i++) {
      const middle = (low + high) / 2;
      if (valueAt(middle) <= IDLE) high = middle; else low = middle;
    }
    dry = high;
  }

  const rate = key => {
    const known = parts.map(windows => windows.filter(w => w[key] != null));
    if (!known.some(windows => windows.length)) return null;
    return average(parts.map(windows => average(windows.map(w => (w.level > 0 ? w[key] ?? 0 : 0)))));
  };
  const next = all.filter(w => w.reset).sort((a, b) => a.reset - b.reset)[0] || null;

  return {
    recent: rate('recent'),
    usual: rate('usual'),
    level: valueAt(now),
    emptyAt: dry,
    reset: null,
    next,
    steps,
  };
}

const rateText = burn => (burn < 10 ? burn.toFixed(1) : Math.round(burn)) + '%/h';

const whenFormat = new Intl.DateTimeFormat(undefined, { weekday: 'short', hour: '2-digit', minute: '2-digit' });
const whenText = time => (time - Date.now() < 6 * DAY ? whenFormat : momentFormat).format(time);

/**
 * "Burning 2.1%/h · usually 0.6%/h · empty in 3h 20m, Thu 21:54": the two rates
 * the forecast is drawn from, then where it ends up. `outcome` says how a
 * forecast that does not run dry ends. The apps word it the same way.
 */
function burnLine(forecast, outcome, together = false) {
  const line = el('p', 'hint burn');
  if (!draining(forecast)) {
    line.textContent = 'Not draining lately';
    return line;
  }
  const parts = [];
  if (forecast.recent != null) {
    parts.push(forecast.recent > IDLE
      ? `Burning ${rateText(forecast.recent)}${together ? ' together' : ''}`
      : `Idle for the last ${burnHours}h`);
  }
  if (forecast.usual != null) parts.push(`${parts.length ? 'usually' : 'Usually'} ${rateText(forecast.usual)}`);
  line.append(parts.join(' · ') + ' · ');
  if (forecast.emptyAt != null) {
    line.append(el('span', 'burn-empty', `empty in ${span(forecast.emptyAt - Date.now())}, ${whenText(forecast.emptyAt)}`));
  } else {
    line.append(outcome);
  }
  return line;
}

/** For the combined figure: when it all runs dry, or when the next window refills. */
function combinedBurnLine(forecast) {
  const nextLift = forecast.next ? `next lift ${whenText(forecast.next.reset)}, ${forecast.next.name} resets` : 'no reset reported';
  if (forecast.emptyAt != null && forecast.next && forecast.emptyAt >= forecast.next.reset) {
    return burnLine({ ...forecast, emptyAt: null }, nextLift);
  }
  return burnLine(forecast, nextLift);
}

function burnSwitch() {
  const group = el('div', 'range');
  group.setAttribute('role', 'group');
  group.setAttribute('aria-label', 'Burn rate over');
  group.append(el('span', 'range-label', 'Burn rate over'));
  for (const hours of BURNS) {
    const button = el('button', null, hours + 'h');
    button.type = 'button';
    button.setAttribute('aria-pressed', String(hours === burnHours));
    button.addEventListener('click', () => {
      burnHours = hours;
      try { localStorage.setItem(BURN_KEY, String(hours)); } catch { /* For this page only, then. */ }
      renderUsage();
    });
    group.append(button);
  }
  return group;
}

function chartDays(label) {
  const match = /(?:^|\s)(\d+)\s*([mhd])$/.exec(label);
  if (!match) return 35;
  const unit = { m: 60_000, h: 3_600_000, d: DAY }[match[2]];
  return Number(match[1]) * unit <= 7 * DAY ? 7 : 35;
}

// ---- Chart ----------------------------------------------------------------

/**
 * One series of remaining-percent over time. A single line on a fixed 0-100
 * axis, so no legend: the heading above it says what it is.
 */
function drawChart(host, points, days, title, window = null, forecast = null) {
  host.replaceChildren();
  const now = Date.now();
  const visible = (points || []).filter(point => point[0] >= now - days * DAY);

  if (visible.length < 2) {
    host.classList.add('empty');
    host.textContent = 'Collecting readings…';
    return;
  }
  host.classList.remove('empty');

  // The size comes from the stylesheet, which makes charts larger on a desk.
  const width = Math.max(host.clientWidth, 240);
  const height = Math.max(host.clientHeight, 100);
  const left = 30, right = 6, top = 8, bottom = 18;
  const plotWidth = width - left - right;
  const plotHeight = height - top - bottom;

  const from = visible[0][0];
  const until = Math.max(now, visible[visible.length - 1][0]);

  // With a forecast the chart runs on past now: half as far again as it looks
  // back, or on to where it runs dry, but never past the reset or the chart's span.
  let to = until;
  if (forecast) {
    to = until + Math.max((until - from) * 0.5, HOUR);
    if (forecast.emptyAt != null) to = Math.max(to, forecast.emptyAt + (forecast.emptyAt - until) * 0.15);
    to = Math.min(to, until + days * DAY);
    if (forecast.reset && forecast.reset < to) to = forecast.reset;
  }
  const x = time => left + (time - from) / (to - from || 1) * plotWidth;
  const y = percent => top + (1 - Math.min(Math.max(percent, 0), 100) / 100) * plotHeight;

  const last = visible[visible.length - 1];
  const tone = severity(last[1]);
  const colour = getComputedStyle(document.documentElement).getPropertyValue('--' + tone).trim();

  const root = svg('svg', {
    viewBox: `0 0 ${width} ${height}`,
    role: 'img',
    'aria-label': `${title}: ${percentText(last[1])} left now, over the last ${days === 1 ? 'day' : days + ' days'}`
      + (forecast && forecast.emptyAt != null ? `; at this rate empty ${whenText(forecast.emptyAt)}` : ''),
  });

  for (const level of [0, 50, 100]) {
    root.append(svg('line', { class: 'grid', x1: left, x2: width - right, y1: y(level), y2: y(level) }));
    const label = svg('text', { class: 'axis', x: left - 6, y: y(level) + 3, 'text-anchor': 'end' });
    label.textContent = level;
    root.append(label);
  }

  // "now" at the right edge, or where the forecast takes over.
  const nowX = x(until);
  const future = to > until;
  const end = svg('text', { class: 'axis', x: nowX, y: height - 4, 'text-anchor': future ? 'middle' : 'end' });
  end.textContent = 'now';
  root.append(end);
  if (future) root.append(svg('line', { class: 'now-line', x1: nowX, x2: nowX, y1: top, y2: top + plotHeight }));

  for (const tick of ticks(from, to, plotWidth)) {
    const at = x(tick.at);
    // Clear of the "now" and of the axis numbers at the left.
    if (Math.abs(at - nowX) < 40 || at > width - right - 20 || at < left + 14) continue;
    root.append(svg('line', { class: 'grid', x1: at, x2: at, y1: top + plotHeight, y2: top + plotHeight + 4 }));
    const label = svg('text', { class: 'axis', x: at, y: height - 4, 'text-anchor': 'middle' });
    label.textContent = tick.text;
    root.append(label);
  }

  // The level holds until the next reading, and after the last one until now.
  const path = visible.map((point, index) => `${index ? 'L' : 'M'}${x(point[0]).toFixed(1)},${y(point[1]).toFixed(1)}`).join('')
    + `L${x(until).toFixed(1)},${y(last[1]).toFixed(1)}`;

  const gradientId = 'fade-' + (++chartCount);
  const gradient = svg('linearGradient', { id: gradientId, x1: 0, x2: 0, y1: 0, y2: 1 });
  gradient.append(
    svg('stop', { offset: '0', 'stop-color': colour, 'stop-opacity': '0.32' }),
    svg('stop', { offset: '1', 'stop-color': colour, 'stop-opacity': '0' }));
  const defs = svg('defs');
  defs.append(gradient);

  root.append(
    defs,
    svg('path', { d: `${path}L${x(until).toFixed(1)},${y(0)}L${x(from).toFixed(1)},${y(0)}Z`, fill: `url(#${gradientId})` }),
    svg('path', { class: 'line', d: path, stroke: colour }));

  // The forecast: the recent burn easing into the usual one, flat once dry,
  // and back up to full at every reset that falls inside the chart.
  if (future) {
    const steps = forecast.steps(until, to);
    root.append(svg('path', {
      class: 'forecast',
      d: steps.map((point, index) => `${index ? 'L' : 'M'}${x(point[0]).toFixed(1)},${y(point[1]).toFixed(1)}`).join(''),
      stroke: colour,
    }));

    if (forecast.emptyAt != null && forecast.emptyAt < to) {
      const ex = x(forecast.emptyAt);
      root.append(svg('circle', { class: 'empty-dot', cx: ex, cy: y(0), r: 3.5, stroke: colour }));
      const nearRight = ex > width - right - 90;
      const label = svg('text', {
        class: 'empty-label', x: nearRight ? ex - 6 : ex + 6, y: y(0) - 6,
        'text-anchor': nearRight ? 'end' : 'start',
      });
      label.textContent = 'empty ' + whenText(forecast.emptyAt);
      root.append(label);
    }
  }

  // The even spread through this cycle: from full at the last refill to empty
  // at the next, drawn as far as now. Above it the window is being used fast.
  const length = window && periodOf(window);
  const reset = window && window.resetAt ? Date.parse(window.resetAt) : NaN;
  if (length && reset > now && reset - now <= length) {
    const start = Math.max(reset - length, from);
    const level = time => (reset - time) / length * 100;
    root.append(svg('line', {
      class: 'even',
      x1: x(start).toFixed(1), y1: y(level(start)).toFixed(1),
      x2: x(to).toFixed(1), y2: y(level(to)).toFixed(1),
    }));
  }

  // Hover: a crosshair on the nearest reading and what it was.
  const cross = svg('line', { class: 'cross', y1: top, y2: top + plotHeight, visibility: 'hidden' });
  const dot = svg('circle', { class: 'dot', r: 4, fill: colour, visibility: 'hidden' });
  root.append(cross, dot);

  const tip = el('div', 'tip');
  tip.hidden = true;

  function show(event) {
    const box = root.getBoundingClientRect();
    const time = from + (event.clientX - box.left - left * box.width / width) / (plotWidth * box.width / width) * (to - from);

    let low = 0, high = visible.length - 1;
    while (high - low > 1) {
      const middle = (low + high) >> 1;
      if (visible[middle][0] <= time) low = middle; else high = middle;
    }
    const point = Math.abs(visible[low][0] - time) <= Math.abs(visible[high][0] - time) ? visible[low] : visible[high];

    const px = x(point[0]);
    cross.setAttribute('x1', px);
    cross.setAttribute('x2', px);
    dot.setAttribute('cx', px);
    dot.setAttribute('cy', y(point[1]));
    cross.setAttribute('visibility', 'visible');
    dot.setAttribute('visibility', 'visible');

    tip.replaceChildren(el('b', null, percentText(point[1])), momentFormat.format(point[0]));
    tip.hidden = false;
    const scale = box.width / width;
    const wanted = px * scale - tip.offsetWidth / 2;
    tip.style.left = Math.min(Math.max(wanted, 0), box.width - tip.offsetWidth) + 'px';
  }

  function hide() {
    cross.setAttribute('visibility', 'hidden');
    dot.setAttribute('visibility', 'hidden');
    tip.hidden = true;
  }

  root.addEventListener('pointermove', show);
  root.addEventListener('pointerdown', show);
  root.addEventListener('pointerleave', hide);

  host.append(root, tip);
}

// ---- Usage ----------------------------------------------------------------

function renderUsage() {
  const snapshot = state && state.snapshot;
  const combined = $('combined');
  const providers = $('providers');
  combined.replaceChildren();
  providers.replaceChildren();

  if (!snapshot) {
    $('lowest-list').replaceChildren(el('li', 'hint', 'Nothing reported yet.'));
    combined.append(el('p', 'hint', 'No reading yet. Add a sign-in or a key below; the first poll follows within seconds.'));
    return;
  }

  // Combined
  const head = el('div', 'combined-head');
  const words = el('div');
  words.append(el('h2', null, 'All usage'));
  const count = snapshot.combinedProviders || 0;
  words.append(el('p', 'hint', count ? `across ${count} subscription${count === 1 ? '' : 's'}` : 'No subscriptions yet'));
  head.append(words);

  const level = snapshot.combinedPercent;
  if (level != null) head.append(rangeSwitch());
  head.append(el('span', 'figure sev-' + (level == null ? 'normal' : severity(level)), level == null ? '—' : percentText(level)));
  combined.append(head);

  // Charts are drawn after every card is in the page: each sizes itself to the
  // box it ends up with, and a row's height is only settled once its last card
  // has been added.
  const drawings = [];

  if (level != null) {
    const chart = el('div', 'chart hero-chart');
    combined.append(chart);
    const forecast = combinedForecast(snapshot);
    if (forecast) words.append(combinedBurnLine(forecast));
    drawings.push(() => drawChart(chart, state.combined, combinedDays, 'All usage', null, forecast));
  }

  renderLowest(snapshot);

  $('burn-range').replaceChildren(burnSwitch());

  // Subscriptions, one card per account, in the server's order; a provider
  // with several accounts gets a card for them together before the first.
  const pooled = new Set();
  for (const usage of snapshot.usages) {
    if (usage.state === 'NOT_CONFIGURED') continue;
    if (!pooled.has(usage.id)) {
      pooled.add(usage.id);
      const pool = poolCard(snapshot, usage.id);
      if (pool) providers.append(pool);
    }
    const name = accountName(usage);

    // In the page before its charts are drawn: a chart sizes itself to its host.
    const card = el('article', 'card');
    providers.append(card);
    const title = el('div', 'provider-head');
    const mark = providerMark(usage.id);
    if (mark) title.append(mark);
    title.append(el('h2', null, name));
    if (usage.plan) title.append(el('span', 'plan', usage.plan));
    if (usage.stale) title.append(el('span', 'hint', `· as of ${ago(usage.fetchedAt)}`));
    card.append(title);

    // A passing failure (a rate limit, Meta going quiet while idle) is a note;
    // one that needs the user is a warning.
    if ((usage.state === 'ERROR' || usage.stale) && usage.message) {
      card.append(el('p', usage.transient ? 'hint' : 'problem', usage.message));
    }

    const hidden = hiddenLimits();
    const account = usage.account || usage.id;
    const hiddenCount = usage.windows.filter(window => hidden.has(seriesKey(usage, window.label))).length;
    for (const window of usage.windows) {
      const key = seriesKey(usage, window.label);
      const isHidden = hidden.has(key);
      if (isHidden && !showingHidden.has(account)) continue;
      const block = el('div', isHidden ? 'window hidden-limit' : 'window');
      card.append(block);
      const row = el('div', 'window-head');
      row.append(el('span', 'label', window.label));
      if (isHidden) row.append(el('span', 'hint', 'Hidden'));

      const reset = window.resetAt ? Date.parse(window.resetAt) - Date.now() : null;
      row.append(el('span', 'hint reset', reset == null ? '' : reset <= 0 ? 'resets now' : `resets in ${span(reset)}`));

      const percent = window.remainingPercent;
      if (percent == null) {
        row.append(el('span', 'hint', window.detail || '—'));
        row.append(eyeButton(key, `${name} ${window.label}`, isHidden));
        block.append(row);
        continue;
      }

      {
        const tone = severity(percent);
        const pace = paceOf(window);
        if (pace) {
          const badge = el('span', 'pace pace-' + pace, PACE[pace].text);
          badge.title = PACE[pace].title;
          row.append(badge);
        }
        row.append(eyeButton(key, `${name} ${window.label}`, isHidden));
        row.append(el('span', 'value sev-' + tone, percentText(percent)));
        block.append(row);

        const meter = meterWithPace(percent, tone, window);
        meter.setAttribute('role', 'meter');
        meter.setAttribute('aria-valuemin', '0');
        meter.setAttribute('aria-valuemax', '100');
        meter.setAttribute('aria-valuenow', String(Math.round(percent)));
        meter.setAttribute('aria-label', `${name} ${window.label} remaining` + (pace ? `, ${PACE[pace].text}` : ''));
        block.append(meter);

        if (window.detail) block.append(el('p', 'hint', window.detail));

        const forecast = forecastOf(window, burnPoints(key));
        if (forecast) block.append(burnLine(forecast, forecast.reset ? 'lasts past the reset' : 'no reset reported'));

        // Marked so the stylesheet can let this window's chart take up the
        // height a shorter card is stretched to.
        const chart = el('div', 'chart');
        block.classList.add('charted');
        block.append(chart);
        drawings.push(() => drawChart(chart, state.series[key], chartDays(window.label), `${name} ${window.label}`, window, forecast));
      }
    }

    if (hiddenCount) {
      const open = showingHidden.has(account);
      const toggle = el('button', 'quiet hidden-toggle',
        open ? 'Fold the hidden limits away'
          : hiddenCount === usage.windows.length ? 'All limits hidden · show them' : `${hiddenCount} hidden · show`);
      toggle.type = 'button';
      toggle.addEventListener('click', () => {
        if (open) showingHidden.delete(account); else showingHidden.add(account);
        renderUsage();
      });
      card.append(toggle);
    }
  }

  for (const draw of drawings) draw();

  $('status').textContent = `Updated ${ago(snapshot.fetchedAt)}`;
}

/**
 * Every account of one provider added up, window by window: "143% of 200%",
 * one bar with a segment per account, soonest reset first. Null for a
 * provider with a single account reporting.
 */
function poolCard(snapshot, id) {
  const accounts = snapshot.usages.filter(usage => usage.id === id
    && usage.state !== 'NOT_CONFIGURED' && (usage.state === 'OK' || usage.stale));
  if (accounts.length < 2) return null;

  const labels = [];
  for (const usage of accounts) {
    for (const window of usage.windows) if (!labels.includes(window.label)) labels.push(window.label);
  }

  const card = el('article', 'card pool');
  const title = el('div', 'provider-head');
  const provider = PROVIDERS.find(p => p.key === id);
  const mark = providerMark(id);
  if (mark) title.append(mark);
  title.append(el('h2', null, provider ? provider.name : id), el('span', 'plan', `${accounts.length} accounts together`));
  card.append(title);

  let shown = 0;
  for (const label of labels) {
    const members = accounts
      .map(usage => ({ usage, window: usage.windows.find(w => w.label === label) }))
      .filter(member => member.window && member.window.remainingPercent != null)
      .map(member => ({ ...member, reset: member.window.resetAt ? Date.parse(member.window.resetAt) : Infinity }))
      .sort((a, b) => a.reset - b.reset);
    if (members.length < 2) continue;
    shown++;

    const total = members.reduce((sum, member) => sum + member.window.remainingPercent, 0);
    const most = members.length * 100;
    const tone = severity(total / members.length);

    const block = el('div', 'window');
    const row = el('div', 'window-head');
    row.append(el('span', 'label', label));
    const soonest = members[0].reset - Date.now();
    row.append(el('span', 'hint reset', soonest > 0 && soonest < Infinity ? `next reset in ${span(soonest)}` : ''));
    const value = el('span', 'value sev-' + tone, percentText(total));
    value.append(el('span', 'of', ` of ${most}%`));
    row.append(value);
    block.append(row);

    const meter = el('div', 'meter segmented');
    meter.setAttribute('role', 'img');
    meter.setAttribute('aria-label', members.map(member =>
      `${accountName(member.usage)} ${percentText(member.window.remainingPercent)}`).join(', '));
    for (const member of members) {
      const part = el('div', 'fill-' + tone);
      part.style.width = member.window.remainingPercent / members.length + '%';
      const left = member.reset - Date.now();
      part.title = `${accountName(member.usage)}: ${percentText(member.window.remainingPercent)}`
        + (left > 0 && left < Infinity ? `, resets in ${span(left)}` : '');
      meter.append(part);
    }
    block.append(meter);

    // Burn rates add up; when it runs dry is left to the accounts, whose resets differ.
    const tracks = members.map(member => trackOf(member.window, burnPoints(seriesKey(member.usage, label))));
    const sum = key => (tracks.some(t => t[key] != null) ? tracks.reduce((total, t) => total + (t[key] || 0), 0) : null);
    const together = { recent: sum('recent'), usual: sum('usual'), emptyAt: null };
    if (measured(together)) {
      const line = burnLine(together, '', true);
      line.textContent = line.textContent.replace(/ · $/, '');
      block.append(line);
    }
    card.append(block);
  }
  return shown ? card : null;
}

function rangeSwitch() {
  const group = el('div', 'range');
  group.setAttribute('role', 'group');
  group.setAttribute('aria-label', 'Chart range');
  for (const range of RANGES) {
    const button = el('button', null, range.label);
    button.type = 'button';
    button.setAttribute('aria-pressed', String(range.days === combinedDays));
    button.addEventListener('click', () => {
      combinedDays = range.days;
      renderUsage();
    });
    group.append(button);
  }
  return group;
}

/** Every window with a level, lowest first: what is about to stop you working. */
function renderLowest(snapshot) {
  const list = $('lowest-list');
  list.replaceChildren();

  const hidden = hiddenLimits();
  const windows = snapshot.usages
    .filter(usage => usage.state === 'OK' || usage.stale)
    .flatMap(usage => usage.windows
      .filter(window => window.remainingPercent != null && !hidden.has(seriesKey(usage, window.label)))
      .map(window => ({ provider: accountName(usage), window })))
    .sort((a, b) => a.window.remainingPercent - b.window.remainingPercent)
    .slice(0, 7);

  if (!windows.length) {
    list.append(el('li', 'hint', 'Nothing reported yet.'));
    return;
  }

  for (const { provider, window } of windows) {
    const percent = window.remainingPercent;
    const tone = severity(percent);
    const item = el('li');
    const head = el('div', 'row-head');

    const name = el('span', 'name', provider + ' ');
    name.append(el('span', null, window.label));
    head.append(name);

    const reset = window.resetAt ? Date.parse(window.resetAt) - Date.now() : null;
    head.append(el('span', 'hint reset', reset == null ? '' : reset <= 0 ? 'now' : 'in ' + span(reset)));
    head.append(el('span', 'value sev-' + tone, percentText(percent)));

    const meter = meterWithPace(percent, tone, window);
    meter.setAttribute('aria-hidden', 'true');

    item.append(head, meter);
    list.append(item);
  }
}

async function load() {
  try {
    // The long view thinned for the charts, and the last week in full for burn rates.
    const [long, short] = await Promise.all([
      api('GET', '/state?days=35&points=800'),
      api('GET', `/state?since=${Date.now() - USUAL_HOURS * HOUR}&points=5000`),
    ]);
    state = long;
    recent = short.series || {};
    showApp();
    renderUsage();
  } catch (problem) {
    if (problem instanceof Unauthorized) return showGate(memoryToken ? problem.message : '');
    $('status').textContent = state ? 'Server unreachable' : problem.message;
  }
}

function schedule() {
  clearInterval(timer);
  timer = setInterval(() => { if (!document.hidden) load(); }, REFRESH_MS);
}

// ---- Sign-ins ------------------------------------------------------------

/** One tile per account, kept across reloads so a sign-in in progress survives them. */
const tiles = new Map();
let addTile = null;

function kindOf(providerKey) {
  return (PROVIDERS.find(provider => provider.key === providerKey) || {}).kind || 'key';
}

function accountTile(account) {
  const card = el('div', 'card');
  const head = el('div', 'signin-head');
  const title = el('h3');
  const status = el('span', 'hint');

  const act = el('button');
  act.type = 'button';
  const out = el('button', 'quiet', 'Sign out');
  out.type = 'button';
  head.append(title, status, act, out);

  const manage = el('div', 'manage');
  const rename = el('button', 'link', 'Rename');
  rename.type = 'button';
  const remove = el('button', 'link', 'Remove account');
  remove.type = 'button';
  manage.append(rename, remove);

  const step = el('div', 'step');
  step.hidden = true;
  const problem = el('p', 'problem');
  problem.setAttribute('role', 'alert');
  card.append(head, manage, step, problem);

  const nodes = { card, title, status, act, out, remove, step, problem, poll: null, account };

  act.addEventListener('click', () =>
    kindOf(nodes.account.provider) === 'key' ? askKey(nodes) : startSignin(nodes));

  out.addEventListener('click', async () => {
    if (!confirm(`Sign the server out of ${nodes.account.displayName}?`)) return;
    await attempt(nodes, () => api('DELETE', '/credentials/' + nodes.account.key));
    closeStep(nodes);
    await afterChange();
  });

  rename.addEventListener('click', () => askName(nodes));

  remove.addEventListener('click', async () => {
    if (!confirm(`Remove ${nodes.account.displayName} from the server? Its sign-in is forgotten; its history stays on disk.`)) return;
    const done = await attempt(nodes, () => api('DELETE', '/accounts/' + nodes.account.key).then(() => true));
    if (done) {
      closeStep(nodes);
      await afterChange();
    }
  });

  return nodes;
}

function updateTile(nodes, account) {
  nodes.account = account;
  const kind = kindOf(account.provider);
  nodes.title.textContent = account.displayName;
  nodes.status.textContent = !account.configured ? 'Not set up'
    : kind === 'key' ? 'Key stored'
    : account.renews ? 'Signed in · renews itself'
    : 'Token stored';
  nodes.out.hidden = !account.configured;
  nodes.act.textContent = kind === 'key'
    ? (account.configured ? 'Replace key' : 'Set key')
    : (account.configured ? 'Sign in again' : 'Sign in');
  nodes.remove.hidden = !account.removable;
}

/** The last tile: an account of any provider, the first or another, e.g. a second Claude subscription. */
function buildAddTile() {
  const card = el('div', 'card add-account');
  card.append(el('h3', null, 'Add an account'));
  card.append(el('p', 'hint', 'Pick the provider. A name tells two accounts with one provider apart, e.g. a work Claude.'));

  const form = el('form', 'add-form');
  const select = el('select');
  select.setAttribute('aria-label', 'Provider');
  for (const provider of PROVIDERS) {
    const option = el('option', null, provider.name);
    option.value = provider.key;
    select.append(option);
  }
  const name = el('input');
  name.type = 'text';
  name.placeholder = 'Name (optional), e.g. Work';
  name.setAttribute('aria-label', 'Account name');
  name.maxLength = 40;
  name.autocomplete = 'off';
  const add = el('button', null, 'Add');
  add.type = 'submit';
  form.append(select, name, add);

  const problem = el('p', 'problem');
  problem.setAttribute('role', 'alert');
  card.append(form, problem);

  form.addEventListener('submit', async event => {
    event.preventDefault();
    add.disabled = true;
    problem.textContent = '';
    try {
      const account = await api('POST', '/accounts', { provider: select.value, name: name.value.trim() || null });
      name.value = '';
      await loadCredentials();
      // Straight on to the step the new account needs.
      const nodes = tiles.get(account.key);
      if (nodes) {
        nodes.card.scrollIntoView({ block: 'nearest' });
        if (kindOf(account.provider) === 'key') askKey(nodes); else startSignin(nodes);
      }
    } catch (failure) {
      if (failure instanceof Unauthorized) showGate(failure.message);
      else problem.textContent = failure.message;
    }
    add.disabled = false;
  });

  return card;
}

async function loadCredentials() {
  let accounts;
  try {
    accounts = await api('GET', '/credentials');
  } catch (problem) {
    if (problem instanceof Unauthorized) showGate(problem.message);
    return;
  }

  const host = $('signins');
  const seen = new Set();
  for (const account of accounts) {
    seen.add(account.key);
    let nodes = tiles.get(account.key);
    if (!nodes) {
      nodes = accountTile(account);
      tiles.set(account.key, nodes);
    }
    updateTile(nodes, account);
    // Appending an element already in place moves it, so the order follows the server's.
    host.append(nodes.card);
  }

  for (const [key, nodes] of tiles) {
    if (seen.has(key)) continue;
    closeStep(nodes);
    nodes.card.remove();
    tiles.delete(key);
  }

  addTile = addTile || buildAddTile();
  host.append(addTile);
}

async function attempt(nodes, action) {
  nodes.problem.textContent = '';
  try {
    return await action();
  } catch (problem) {
    if (problem instanceof Unauthorized) showGate(problem.message);
    else nodes.problem.textContent = problem.message;
    return undefined;
  }
}

function closeStep(nodes) {
  clearInterval(nodes.poll);
  nodes.poll = null;
  nodes.step.hidden = true;
  nodes.step.replaceChildren();
}

async function afterChange() {
  await loadCredentials();
  // The server polls an account the moment its credentials change.
  setTimeout(load, 4000);
}

function pasteRow(placeholder, buttonText, secret, onSubmit, value) {
  const form = el('form', 'row');
  const input = el('input');
  input.type = secret ? 'password' : 'text';
  input.placeholder = placeholder;
  input.setAttribute('aria-label', placeholder);
  input.autocomplete = 'off';
  input.spellcheck = false;
  input.required = !value && value !== '';
  if (value) input.value = value;
  const button = el('button', null, buttonText);
  button.type = 'submit';
  form.append(input, button);
  form.addEventListener('submit', async event => {
    event.preventDefault();
    button.disabled = true;
    await onSubmit(input.value.trim());
    button.disabled = false;
  });
  return form;
}

function askName(nodes) {
  closeStep(nodes);
  nodes.step.hidden = false;
  nodes.step.append(pasteRow('Name, e.g. Work (empty for none)', 'Save', false, async value => {
    const done = await attempt(nodes, () => api('PATCH', '/accounts/' + nodes.account.key, { name: value || null }).then(() => true));
    if (done) {
      closeStep(nodes);
      await afterChange();
    }
  }, nodes.account.name || ''));
  nodes.step.querySelector('input').focus();
}

function askKey(nodes) {
  closeStep(nodes);
  nodes.step.hidden = false;
  nodes.step.append(pasteRow(`${nodes.account.displayName} API key`, 'Save', true, async value => {
    const done = await attempt(nodes, () => api('PUT', '/credentials/' + nodes.account.key, { token: value }).then(() => true));
    if (done) {
      closeStep(nodes);
      await afterChange();
    }
  }));
  nodes.step.querySelector('input').focus();
}

async function startSignin(nodes) {
  closeStep(nodes);
  const key = nodes.account.key;
  nodes.act.disabled = true;
  const step = await attempt(nodes, () => api('POST', `/signin/${key}/start`));
  nodes.act.disabled = false;
  if (!step) return;

  nodes.step.hidden = false;
  nodes.step.append(el('p', null, step.instructions));
  const siblings = [...tiles.values()].filter(other => other.account.provider === nodes.account.provider);
  if (siblings.length > 1) {
    // With two accounts of one provider, the browser decides which one approves.
    nodes.step.append(el('p', 'hint', 'Make sure the browser is signed in to the account you mean before you approve.'));
  }

  const link = el('a', null, 'Open the sign-in page');
  // Only ever a web address: this goes into an href.
  if (/^https:\/\//i.test(step.url)) link.href = step.url;
  link.target = '_blank';
  link.rel = 'noopener noreferrer';
  const line = el('p');
  line.append(link);
  nodes.step.append(line);

  if (step.kind === 'device') {
    nodes.step.append(el('p', 'user-code', step.userCode || ''));
    nodes.step.append(el('p', 'hint', 'Waiting for you to approve…'));

    nodes.poll = setInterval(async () => {
      const answer = await attempt(nodes, () => api('GET', `/signin/${key}/status`));
      if (!answer || answer.status === 'pending') return;
      closeStep(nodes);
      if (answer.status !== 'done') nodes.problem.textContent = answer.status;
      await afterChange();
    }, 3000);
    return;
  }

  const placeholder = step.kind === 'redirect' ? 'The address the browser landed on' : 'The code the page shows';
  nodes.step.append(pasteRow(placeholder, 'Finish', false, async value => {
    const done = await attempt(nodes, () => api('POST', `/signin/${key}/complete`, { input: value }).then(() => true));
    if (done) {
      closeStep(nodes);
      await afterChange();
    }
  }));
}

// ---- Gate -----------------------------------------------------------------

function showGate(message) {
  clearInterval(timer);
  $('app').hidden = true;
  $('refresh').hidden = true;
  $('status').textContent = '';
  $('gate').hidden = false;
  $('gate-error').textContent = message || '';
  $('gate-token').focus();
}

function showApp() {
  if (!$('gate').hidden || $('app').hidden) {
    $('gate').hidden = true;
    $('app').hidden = false;
    $('refresh').hidden = false;
    loadCredentials();
    schedule();
  }
}

$('gate').addEventListener('submit', event => {
  event.preventDefault();
  setToken($('gate-token').value.trim());
  $('gate-token').value = '';
  load();
});

$('forget').addEventListener('click', () => {
  setToken(null);
  state = null;
  showGate('');
});

$('refresh').addEventListener('click', async () => {
  const button = $('refresh');
  button.disabled = true;
  $('status').textContent = 'Asking the providers…';
  try {
    await api('POST', '/refresh');
  } catch (problem) {
    if (problem instanceof Unauthorized) showGate(problem.message);
  }
  await load();
  button.disabled = false;
});

// Charts are drawn to the width they have, so a change of width redraws them.
let resizing = null;
let drawnWidth = 0;
function redrawIfResized() {
  const width = $('app').clientWidth;
  if (!state || $('app').hidden || width === drawnWidth) return;
  drawnWidth = width;
  clearTimeout(resizing);
  resizing = setTimeout(renderUsage, 150);
}
if (typeof ResizeObserver === 'function') new ResizeObserver(redrawIfResized).observe($('app'));
else window.addEventListener('resize', redrawIfResized);

document.addEventListener('visibilitychange', () => {
  if (!document.hidden && memoryToken && !$('app').hidden) load();
});

if (memoryToken) load(); else showGate('');
