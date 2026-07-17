'use strict';

/* ============================== configuration ============================== */

// 'side' (default) or 'bottom' — the fallback if the side column crowds 1440p.
const TASK_PANEL_POSITION = 'side';

const IS_HOSTED = !!(window.chrome && window.chrome.webview); // false when previewed in a plain browser

/* ============================== state ============================== */

let payload = null;          // last data payload from the host
let todayKey = dateKey(new Date());
const slideshow = createSlideshow(document.getElementById('photos'));

/* ============================== helpers ============================== */

function dateKey(d) {
  return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`;
}
function parseDateOnly(s) {
  const [y, m, d] = s.split('-').map(Number);
  return new Date(y, m - 1, d);
}
function fmtTime(d) {
  return `${d.getHours()}:${String(d.getMinutes()).padStart(2, '0')}`;
}
function el(tag, className, text) {
  const node = document.createElement(tag);
  if (className) node.className = className;
  if (text !== undefined) node.textContent = text;
  return node;
}
function shuffle(arr) {
  for (let i = arr.length - 1; i > 0; i--) {
    const j = Math.floor(Math.random() * (i + 1));
    [arr[i], arr[j]] = [arr[j], arr[i]];
  }
  return arr;
}

/* ============================== calendar rendering ============================== */

function monthModel(year, month /* 1-12 */) {
  const first = new Date(year, month - 1, 1);
  const offset = (first.getDay() + 6) % 7;           // Monday-start
  const daysInMonth = new Date(year, month, 0).getDate();
  const weeks = Math.ceil((offset + daysInMonth) / 7);
  const gridStart = new Date(year, month - 1, 1 - offset);
  const days = [];
  for (let i = 0; i < weeks * 7; i++) {
    const d = new Date(gridStart);
    d.setDate(gridStart.getDate() + i);
    days.push(d);
  }
  return { days, weeks };
}

function indexEvents(data) {
  const byDay = new Map();
  const bucket = (k) => {
    if (!byDay.has(k)) byDay.set(k, { allday: [], timed: [] });
    return byDay.get(k);
  };
  for (const ev of data.events) {
    const color = (data.feeds[ev.feed] && data.feeds[ev.feed].color) || '#7aa2f7';
    if (ev.allDay) {
      const start = parseDateOnly(ev.start);
      const end = parseDateOnly(ev.end);
      for (let d = new Date(start); d <= end; d.setDate(d.getDate() + 1)) {
        bucket(dateKey(d)).allday.push({ title: ev.title, color, sort: start.getTime() });
      }
    } else {
      const start = new Date(ev.start);
      const end = new Date(ev.end || ev.start);
      // Timed events lasting a day or more (e.g. week-long events entered with times)
      // render as spanning bars; short events that merely cross midnight stay chips.
      if (end - start >= 24 * 3600 * 1000) {
        const startDay = new Date(start.getFullYear(), start.getMonth(), start.getDate());
        const lastDay = new Date(end.getFullYear(), end.getMonth(), end.getDate());
        if (end.getHours() === 0 && end.getMinutes() === 0) lastDay.setDate(lastDay.getDate() - 1);
        for (let d = new Date(startDay); d <= lastDay; d.setDate(d.getDate() + 1)) {
          const first = +d === +startDay;
          bucket(dateKey(d)).allday.push({
            title: first ? `${fmtTime(start)} ${ev.title}` : ev.title,
            color, sort: start.getTime(),
          });
        }
      } else {
        bucket(dateKey(start)).timed.push({
          title: ev.title, color, time: fmtTime(start), sort: start.getTime(),
        });
      }
    }
  }
  for (const day of byDay.values()) {
    day.allday.sort((a, b) => a.sort - b.sort || a.title.localeCompare(b.title));
    day.timed.sort((a, b) => a.sort - b.sort || a.title.localeCompare(b.title));
  }
  return byDay;
}

function render() {
  if (!payload) return;
  const { year, month } = payload;
  const now = new Date();
  todayKey = dateKey(now);

  // header
  const monthName = new Date(year, month - 1, 1).toLocaleString(undefined, { month: 'long' });
  const title = document.getElementById('month-title');
  title.textContent = '';
  title.append(monthName);
  title.append(Object.assign(el('span', 'year'), { textContent: String(year) }));

  // legend: one dot per visible feed
  const legend = document.getElementById('legend');
  legend.textContent = '';
  for (const feed of payload.feeds) {
    if (feed.enabled === false) continue;
    const item = el('div', 'legend-item');
    const dot = el('div', 'dot');
    dot.style.setProperty('--c', feed.color || '#7aa2f7');
    item.append(dot, el('span', null, feed.name));
    legend.append(item);
  }

  const dowRow = document.getElementById('dow-row');
  dowRow.textContent = '';
  const base = new Date(2024, 0, 1); // a Monday
  for (let i = 0; i < 7; i++) {
    const d = new Date(base);
    d.setDate(base.getDate() + i);
    dowRow.append(el('div', null, d.toLocaleString(undefined, { weekday: 'short' })));
  }

  // grid
  const { days, weeks } = monthModel(year, month);
  const byDay = indexEvents(payload);
  const grid = document.getElementById('grid');
  grid.textContent = '';
  grid.style.gridTemplateRows = `repeat(${weeks}, minmax(0, 1fr))`;

  const frag = document.createDocumentFragment();
  days.forEach((d, i) => {
    const key = dateKey(d);
    const cell = el('div', 'cell');
    const col = i % 7;
    if (col >= 5) cell.classList.add('weekend');
    if (d.getMonth() !== month - 1) cell.classList.add('out');
    if (key === todayKey) cell.classList.add('today');

    const head = el('div', 'day-head');
    head.append(el('div', 'day-num', String(d.getDate())));
    cell.append(head);

    const events = el('div', 'events');
    const day = byDay.get(key);
    if (day) {
      for (const bar of day.allday) {
        const node = el('div', 'bar', bar.title);
        node.style.setProperty('--c', bar.color);
        events.append(node);
      }
      for (const chip of day.timed) {
        const node = el('div', 'chip');
        node.style.setProperty('--c', chip.color);
        node.append(el('span', 't', chip.time));
        node.append(el('span', 's', chip.title));
        events.append(node);
      }
    }
    cell.append(events);
    frag.append(cell);
  });
  grid.append(frag);

  renderTasks();
  renderStatus();
  updateClock();
  fitEvents();

  slideshow.update(payload.photos || [], payload.photoIntervalSeconds || 20);
  document.getElementById('root').classList.toggle('no-photos', !(payload.photos || []).length);
}

/* ---------- auto-scale so the busiest day still shows every event ---------- */

function overflows() {
  for (const node of document.querySelectorAll('.cell .events')) {
    if (node.scrollHeight > node.clientHeight + 1) return true;
  }
  return false;
}

function fitEvents() {
  const root = document.documentElement;
  const setScale = (v) => root.style.setProperty('--scale', String(v));

  setScale(1);
  if (!overflows()) return;

  let lo = 0.35, hi = 1;                 // find the largest scale that fits
  for (let i = 0; i < 9; i++) {
    const mid = (lo + hi) / 2;
    setScale(mid);
    if (overflows()) hi = mid; else lo = mid;
  }
  setScale(lo);
}

/* ============================== tasks ============================== */

function renderTasks() {
  const list = document.getElementById('task-list');
  list.textContent = '';
  const tasks = (payload.tasks || []).slice();
  document.getElementById('root').classList.toggle('no-tasks', tasks.length === 0);
  if (!tasks.length) return;

  const today = todayKey;
  const rank = (t) => (t.due ? (t.due < today ? 0 : 1) : 2); // overdue, dated, undated
  tasks.sort((a, b) => rank(a) - rank(b) || (a.due || '9999').localeCompare(b.due || '9999') || a.title.localeCompare(b.title));

  for (const task of tasks) {
    const color = (payload.feeds[task.feed] && payload.feeds[task.feed].color) || '#7aa2f7';
    const node = el('div', 'task');
    const dot = el('div', 'dot');
    dot.style.setProperty('--c', color);
    const body = el('div', 'body');
    body.append(el('div', 'title', task.title));
    if (task.due) {
      const due = parseDateOnly(task.due);
      const overdue = task.due < today;
      if (overdue) node.classList.add('overdue');
      const label = overdue
        ? `Overdue · ${due.toLocaleString(undefined, { day: 'numeric', month: 'short' })}`
        : task.due === today
          ? 'Today'
          : due.toLocaleString(undefined, { weekday: 'short', day: 'numeric', month: 'short' });
      body.append(el('div', 'due', label));
    }
    node.append(dot, body);
    list.append(node);
  }
}

/* ============================== status line & clock ============================== */

function renderStatus() {
  const left = document.getElementById('status-left');
  left.textContent = '';
  const parts = [];

  if (!payload.feeds.length) {
    parts.push(el('span', null, 'No feeds configured — right-click the screensaver file → Install, then open Settings'));
  } else {
    parts.push(el('span', null, payload.lastRefresh ? `Updated ${payload.lastRefresh}` : 'Loading feeds…'));
    const stale = payload.feeds.filter((f) => f.stale).map((f) => f.name);
    const dead = payload.feeds.filter((f) => f.error && !f.stale).map((f) => f.name);
    if (stale.length) parts.push(el('span', 'stale', `⚠ ${stale.join(', ')}: showing cached`));
    if (dead.length) parts.push(el('span', 'err', `✕ ${dead.join(', ')}: unavailable`));
  }
  parts.forEach((p, i) => {
    if (i) left.append(el('span', null, '   ·   '));
    left.append(p);
  });
}

function updateClock() {
  const now = new Date();
  document.getElementById('clock').textContent =
    `${String(now.getHours()).padStart(2, '0')}:${String(now.getMinutes()).padStart(2, '0')}`;
  document.getElementById('clock-date').textContent =
    now.toLocaleString(undefined, { weekday: 'long', day: 'numeric', month: 'long' });
}

setInterval(() => {
  updateClock();
  const nowKey = dateKey(new Date());
  if (nowKey !== todayKey && payload) {
    todayKey = nowKey;
    if (!IS_HOSTED) payload = mockPayload();  // host re-pushes real data at midnight
    render();
  }
}, 10_000);

/* ============================== photo slideshow ============================== */

function createSlideshow(container) {
  let pool = [];
  let queue = [];
  let tiles = [];
  let timer = null;
  let nextTile = 0;
  let signature = '';
  let intervalSec = 20;

  function nextPhoto() {
    if (!queue.length) queue = shuffle(pool.slice());
    const visible = new Set(tiles.map((t) => t.current));
    for (let i = 0; i < queue.length; i++) {           // prefer one not already on screen
      if (!visible.has(queue[i])) return queue.splice(i, 1)[0];
    }
    return queue.pop();
  }

  function swap(tile) {
    const url = nextPhoto();
    if (!url) return;
    const incoming = tile.imgs[tile.active ^ 1];
    const probe = new Image();
    probe.onload = () => {
      incoming.src = url;   // already decoded via the probe, so the crossfade starts clean
      incoming.classList.add('show');
      tile.imgs[tile.active].classList.remove('show');
      tile.active ^= 1;
      tile.current = url;
    };
    probe.onerror = () => { /* unreadable file — just skip this slot */ };
    probe.src = url;
  }

  function build() {
    container.textContent = '';
    tiles = [];
    nextTile = 0;
    const count = pool.length >= 12 ? 4 : pool.length >= 4 ? 3 : Math.min(pool.length, 2);
    const grows = shuffle([1.25, 0.9, 1.1, 0.8]).slice(0, count);
    for (let i = 0; i < count; i++) {
      const tileEl = el('div', 'tile');
      tileEl.style.setProperty('--grow', grows[i]);
      const a = el('img'); const b = el('img');
      tileEl.append(a, b);
      container.append(tileEl);
      tiles.push({ el: tileEl, imgs: [a, b], active: 0, current: null });
    }
    tiles.forEach((tile, i) => setTimeout(() => swap(tile), 350 * i)); // staggered first fill
  }

  function restartTimer() {
    if (timer) clearInterval(timer);
    timer = null;
    if (!tiles.length) return;
    timer = setInterval(() => {          // one tile at a time — never blank the whole panel
      swap(tiles[nextTile]);
      nextTile = (nextTile + 1) % tiles.length;
    }, Math.max(3, intervalSec) * 1000);
  }

  return {
    update(photos, seconds) {
      const sig = JSON.stringify(photos);
      const intervalChanged = seconds !== intervalSec;
      intervalSec = seconds;
      if (sig !== signature) {
        signature = sig;
        pool = photos.slice();
        queue = [];
        build();
        restartTimer();
      } else if (intervalChanged) {
        restartTimer();
      }
    },
  };
}

/* ============================== host wiring & input-to-exit ============================== */

function applyPayload(data) {
  payload = data;
  document.documentElement.classList.toggle('light', payload.theme === 'light');
  render();
}

if (IS_HOSTED) {
  let exited = false;
  const exit = (reason) => {
    if (exited) return;
    exited = true;
    window.chrome.webview.postMessage({ type: 'exit', reason: String(reason) });
  };

  // Mouse jitter must not kill the saver: only exit after >10px of cumulative travel.
  let lastPos = null;
  let travelled = 0;
  window.addEventListener('mousemove', (e) => {
    if (lastPos) {
      travelled += Math.hypot(e.screenX - lastPos.x, e.screenY - lastPos.y);
      if (travelled > 10) exit(`mousemove travelled=${Math.round(travelled)} at=${e.screenX},${e.screenY}`);
    }
    lastPos = { x: e.screenX, y: e.screenY };
  });
  window.addEventListener('keydown', (e) => exit('keydown:' + e.key));
  window.addEventListener('mousedown', () => exit('mousedown'));
  window.addEventListener('wheel', () => exit('wheel'));
  window.addEventListener('contextmenu', (e) => e.preventDefault());

  window.chrome.webview.addEventListener('message', (e) => {
    if (e.data && e.data.type === 'data') {
      applyPayload(e.data);
      const grid = document.getElementById('grid').getBoundingClientRect();
      window.chrome.webview.postMessage({
        type: 'metrics',
        vw: innerWidth, vh: innerHeight, dpr: devicePixelRatio,
        gridBottom: Math.round(grid.bottom),
        events: (e.data.events || []).length, photos: (e.data.photos || []).length,
      });
      setTimeout(() => {
        const loaded = [...document.querySelectorAll('#photos img')].filter((i) => i.naturalWidth > 0).length;
        window.chrome.webview.postMessage({ type: 'metrics', tilesLoaded: loaded });
      }, 8000);
    }
  });
  window.chrome.webview.postMessage({ type: 'ready' });
} else {
  // Browser preview: render a rich mock so the design can be inspected without the host.
  setTimeout(() => applyPayload(mockPayload()), 30);
}

window.addEventListener('resize', () => render());
if (document.fonts && document.fonts.ready) {
  document.fonts.ready.then(() => fitEvents()); // Inter changes metrics once it loads
}

if (TASK_PANEL_POSITION === 'bottom') {
  document.getElementById('root').classList.add('tasks-bottom');
}

/* ============================== mock data (browser preview only) ============================== */

function mockPayload() {
  const now = new Date();
  const y = now.getFullYear();
  const m = now.getMonth() + 1;
  const day = (d) => `${y}-${String(m).padStart(2, '0')}-${String(d).padStart(2, '0')}`;
  const feeds = [
    { name: 'Family', color: '#7aa2f7', stale: false, error: null, enabled: true },
    { name: 'Work', color: '#f7768e', stale: true, error: null, enabled: true },
    { name: 'School', color: '#9ece6a', stale: false, error: null, enabled: true },
    { name: 'Birthdays', color: '#e0af68', stale: false, error: null, enabled: true },
  ];
  const events = [];
  const add = (feed, title, d, hh, mm = 0) =>
    events.push({ feed, title, allDay: false, start: `${day(d)}T${String(hh).padStart(2, '0')}:${String(mm).padStart(2, '0')}:00`, end: `${day(d)}T${String(hh + 1).padStart(2, '0')}:00:00` });
  const addAllDay = (feed, title, d1, d2) =>
    events.push({ feed, title, allDay: true, start: day(d1), end: day(d2 || d1) });

  // weekly recurrences, as the host would expand them
  for (let d = 1; d <= 28; d += 7) { add(2, 'Soccer practice', d + 1, 17, 30); add(0, 'Swim class', d + 3, 16, 0); }
  add(1, 'Sprint planning', 2, 10); add(1, '1:1 with Sam', 4, 14, 30);
  add(0, 'Dentist — kids', 9, 9, 15); add(1, 'Design review', 10, 11);
  add(3, "Grandma's birthday", 12, 12); addAllDay(3, 'Nana & Pop visiting', 13, 16);
  addAllDay(0, 'School holiday', 20);
  add(0, 'Pizza night', 17, 18, 30); add(1, 'Quarterly review', 18, 9);
  add(2, 'Book fair', 19, 8, 45); add(0, 'Oil change', 24, 15);
  add(1, 'Team offsite', 25, 9); add(0, 'Movie night', 26, 19, 30);

  // one deliberately packed day to exercise the auto-fit
  const busy = Math.min(now.getDate() + 2, 27);
  [['Breakfast run', 7, 30, 0], ['Standup', 9, 0, 1], ['Parent-teacher mtg', 10, 0, 2],
   ['Lunch w/ Alex', 12, 15, 0], ['Vet appointment', 13, 30, 0], ['Code review', 14, 30, 1],
   ['School pickup', 15, 15, 2], ['Groceries', 16, 30, 0], ['Soccer game', 18, 0, 2],
  ].forEach(([t, h, mm, f]) => add(f, t, busy, h, mm));
  addAllDay(1, 'Conference (remote)', busy, busy + 1);

  const tasks = [
    { feed: 0, title: 'Renew car registration', due: day(Math.max(1, now.getDate() - 3)) },
    { feed: 1, title: 'Submit expense report', due: day(Math.max(1, now.getDate() - 1)) },
    { feed: 0, title: 'Book summer camp', due: day(Math.min(28, now.getDate() + 2)) },
    { feed: 2, title: 'Sign permission slip', due: day(Math.min(28, now.getDate() + 4)) },
    { feed: 0, title: 'Fix the fence gate', due: null },
    { feed: 1, title: 'Update team wiki', due: day(Math.min(28, now.getDate() + 9)) },
  ];

  const photos = [];
  for (let i = 0; i < 10; i++) {
    const h1 = (i * 47) % 360, h2 = (h1 + 40) % 360;
    const svg = `<svg xmlns="http://www.w3.org/2000/svg" width="800" height="1000">` +
      `<defs><linearGradient id="g" x1="0" y1="0" x2="1" y2="1">` +
      `<stop offset="0" stop-color="hsl(${h1},45%,32%)"/><stop offset="1" stop-color="hsl(${h2},55%,16%)"/>` +
      `</linearGradient></defs><rect width="800" height="1000" fill="url(#g)"/>` +
      `<circle cx="${180 + i * 40}" cy="${240 + i * 55}" r="130" fill="hsl(${h2},50%,45%)" opacity="0.35"/></svg>`;
    photos.push('data:image/svg+xml,' + encodeURIComponent(svg));
  }

  return {
    type: 'data', year: y, month: m, events, tasks, feeds, photos,
    photoIntervalSeconds: 6, lastRefresh: fmtTime(now),
    theme: new URLSearchParams(location.search).get('theme') || 'dark', // preview: ?theme=light
  };
}
