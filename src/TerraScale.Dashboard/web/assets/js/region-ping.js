const cacheKey = 'ts-region-ping';
const cacheTtlMs = 60_000;
const probeTimeoutMs = 3000;
const warmed = new Set();
const inflight = new Map();

const readCache = () => {
  try {
    const raw = sessionStorage.getItem(cacheKey);
    return raw === null ? {} : JSON.parse(raw);
  } catch {
    return {};
  }
};

const writeCache = entries => {
  try {
    sessionStorage.setItem(cacheKey, JSON.stringify(entries));
  } catch {
  }
};

const sampleUrl = (url, index) => `${url}${url.includes('?') ? '&' : '?'}p=${index}-${Date.now()}`;

const timing = url => {
  const entry = performance.getEntriesByName(url).at(-1);
  if (entry && entry.responseStart > 0 && entry.requestStart > 0) return entry.responseStart - entry.requestStart;
  return null;
};

const sample = async (url, index) => {
  const probed = sampleUrl(url, index);
  const controller = new AbortController();
  const timer = setTimeout(() => controller.abort(), probeTimeoutMs);
  const start = performance.now();
  try {
    await (await fetch(probed, { mode: 'cors', cache: 'no-store', credentials: 'omit', signal: controller.signal })).arrayBuffer();
    return timing(probed) ?? Math.round(performance.now() - start);
  } finally {
    clearTimeout(timer);
  }
};

const measure = async url => {
  if (!warmed.has(url)) {
    warmed.add(url);
    await sample(url, 0).catch(() => null);
  }
  const readings = [];
  for (let index = 1; index <= 3; index += 1) {
    try {
      readings.push(await sample(url, index));
    } catch {
      return null;
    }
  }
  readings.sort((a, b) => a - b);
  return Math.round(readings[1]);
};

const resolve = (url, force) => {
  const cached = readCache()[url];
  if (!force && cached && Date.now() - cached.at < cacheTtlMs) return Promise.resolve(cached.ms);
  const running = inflight.get(url);
  if (running) return running;
  const task = measure(url)
    .then(ms => {
      const entries = readCache();
      entries[url] = { ms, at: Date.now() };
      writeCache(entries);
      return ms;
    })
    .finally(() => inflight.delete(url));
  inflight.set(url, task);
  return task;
};

const state = ms => ms === null ? 'down' : ms < 100 ? 'good' : ms < 250 ? 'fair' : 'slow';

const text = ms => ms === null ? 'Unreachable' : `${ms} ms`;

const render = (badge, ms) => {
  badge.dataset.state = state(ms);
  badge.textContent = text(ms);
};

const urlOf = badge => badge.closest('.ts-region-callout')?.dataset.pingUrl ?? '';

const parseMs = badge => Number.parseInt(badge.textContent, 10);

const markLowest = badges => {
  clearLowest(badges);
  const measured = badges.map(badge => ({ badge, ms: parseMs(badge) })).filter(entry => entry.badge.dataset.state !== 'down' && Number.isFinite(entry.ms));
  if (measured.length < 2) return;
  const best = measured.reduce((lowestSeen, entry) => entry.ms < lowestSeen.ms ? entry : lowestSeen);
  best.badge.closest('.ts-region-callout')?.setAttribute('data-fastest', '');
};

const clearLowest = badges => {
  for (const badge of badges) badge.closest('.ts-region-callout')?.removeAttribute('data-fastest');
};

export const pingRegions = (root, signal) => {
  const badges = [...root.querySelectorAll('[data-region-ping]')];
  if (badges.length === 0) return;
  const selectable = root.dataset.mode === 'select';
  for (const badge of badges) {
    const cached = readCache()[urlOf(badge)];
    if (cached && Date.now() - cached.at < cacheTtlMs && cached.ms !== undefined) render(badge, cached.ms);
  }
  let cancelled = false;
  signal.addEventListener('abort', () => { cancelled = true; });
  const run = async force => {
    if (selectable) clearLowest(badges);
    const settle = badges.map(async badge => {
      const url = urlOf(badge);
      if (url === '') return;
      const cached = readCache()[url];
      if (force || !cached || Date.now() - cached.at >= cacheTtlMs) {
        badge.dataset.state = 'pending';
        badge.textContent = 'Measuring…';
      }
      const ms = await resolve(url, force).catch(() => null);
      if (cancelled || !badge.isConnected) return;
      render(badge, ms);
      if (selectable) markLowest(badges);
    });
    await Promise.allSettled(settle);
  };
  run(false);
  root.querySelector('[data-ping-refresh]')?.addEventListener('click', () => {
    const entries = readCache();
    for (const badge of badges) delete entries[urlOf(badge)];
    writeCache(entries);
    run(true);
  }, { signal });
};
