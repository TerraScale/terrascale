const streams = new Map();
const pageRevision = document.documentElement.dataset.appRevision;
const revisionHeader = 'X-TerraScale-Revision';
const reloadKey = 'ts-live-revision-reload';
const reloadCooldown = 60_000;
const chunkTimeout = 45_000;
let pageHidden = false;
let authPaused = false;
let navigationTimer;
let reloadTimer;
let pendingRevision = false;
let pendingWrites = 0;

function formIsDirty(form) {
  return Array.from(form.elements).some(control => {
    if (control.disabled || control.matches?.(':disabled')) return false;
    if (control.localName === 'textarea') return control.value !== control.defaultValue;
    if (control.localName === 'select') {
      const options = Array.from(control.options);
      const selected = options.filter(option => option.defaultSelected);
      const initial = selected.at(-1) ?? (control.size < 2 ? options.find(option => !option.disabled && !option.closest('optgroup:disabled')) : null);
      return options.some(option => option.selected !== (control.multiple ? option.defaultSelected : option === initial));
    }
    if (control.localName !== 'input') return false;
    switch (control.type) {
      case 'hidden':
      case 'button':
      case 'submit':
      case 'image':
      case 'reset':
        return false;
      case 'file':
        return control.files.length > 0;
      case 'checkbox':
      case 'radio':
        return control.checked !== control.defaultChecked;
      default:
        return control.value !== control.defaultValue;
    }
  });
}

function scheduleReload(delay = 1000) {
  if (!pendingRevision || reloadTimer !== undefined || document.hidden || pageHidden || navigationTimer !== undefined) return;
  reloadTimer = setTimeout(() => {
    reloadTimer = undefined;
    try {
      const lastReload = sessionStorage.getItem(reloadKey);
      const remaining = lastReload === null ? 0 : reloadCooldown - (Date.now() - Number(lastReload));
      if (remaining > 0) {
        scheduleReload(Math.min(remaining, reloadCooldown));
        return;
      }
      if (document.hidden || pageHidden || navigationTimer !== undefined) return;
      if (pendingWrites ||
          document.querySelector('dialog[open], [data-dirty], [data-editing], [data-brand-dirty]:not([hidden])') ||
          document.querySelector('[data-upload-queue] [data-state]:not([data-state="done"], [data-state="failed"])') ||
          Array.from(document.querySelectorAll('[data-upload-metadata]')).some(control => control.value !== control.defaultValue) ||
          document.activeElement?.closest('input, textarea, [contenteditable]:not([contenteditable="false"]), .monaco-editor') ||
          Array.from(document.querySelectorAll('form[method="post" i]')).some(formIsDirty)) {
        scheduleReload();
        return;
      }
      sessionStorage.setItem(reloadKey, String(Date.now()));
    } catch {
      pendingRevision = false;
      return;
    }
    pendingRevision = false;
    navigate();
  }, delay);
}

const fetch = window.fetch.bind(window);
window.fetch = async (input, options) => {
  const request = input instanceof Request;
  const sameOrigin = new URL(request ? input.url : input, location.href).origin === location.origin;
  const method = (options?.method ?? (request ? input.method : 'GET')).toUpperCase();
  const writing = sameOrigin && method !== 'GET' && method !== 'HEAD';
  if (writing) pendingWrites++;
  try {
    const response = await fetch(input, options);
    if (pageRevision && pageRevision !== 'unknown' && sameOrigin && (!response.url || new URL(response.url).origin === location.origin)) {
      const revision = response.headers.get(revisionHeader);
      if (revision && revision !== 'unknown' && revision !== pageRevision) {
        pendingRevision = true;
        scheduleReload();
      }
    }
    return response;
  } finally {
    if (writing) pendingWrites--;
  }
};

function isAttached(stream) {
  return stream.el.isConnected && (stream.el.getAttribute('data-init') ?? '').includes('__tsLive?.connect(');
}

function canRun(stream) {
  return streams.get(stream.el) === stream && isAttached(stream) && !stream.complete && !document.hidden && !pageHidden && !authPaused && navigator.onLine !== false;
}

function clearRetry(stream) {
  clearTimeout(stream.timer);
  stream.timer = undefined;
}

function pause(stream) {
  clearRetry(stream);
  stream.controller?.abort();
}

function pauseForAuth() {
  authPaused = true;
  for (const stream of streams.values()) pause(stream);
}

function navigate(path) {
  if (pageHidden || navigationTimer !== undefined) return;
  if (path) location.assign(path);
  else location.reload();
  if (pageHidden) return;
  navigationTimer = setTimeout(() => {
    navigationTimer = undefined;
  }, 2000);
}

function login() {
  navigate('/login?returnTo=' + encodeURIComponent(location.pathname + location.search + location.hash));
}

function retryAfter(response) {
  const header = response.headers.get('Retry-After')?.trim();
  if (!header) return 0;
  const now = Date.now();
  const deadline = /^\d+$/.test(header) ? now + Number(header) * 1000 : Date.parse(header);
  return Number.isFinite(deadline) ? Math.max(now, deadline) : 0;
}

function handleEvent(stream, type, data) {
  if (type === 'ts-live-complete') stream.complete = true;
  if (type === 'ts-live-auth' && (data === '401' || data === '403')) {
    pauseForAuth();
    if (data === '401') login();
    else navigate();
  }
  if (type === 'datastar-patch-elements' && isAttached(stream)) {
    const fields = new Map();
    for (const line of data.split('\n')) {
      const separator = line.indexOf(' ');
      if (separator < 0) continue;
      const key = line.slice(0, separator);
      const value = line.slice(separator + 1);
      fields.set(key, fields.has(key) ? fields.get(key) + '\n' + value : value);
    }
    document.dispatchEvent(new CustomEvent('datastar-fetch', {
      detail: {type, el: stream.el, argsRaw: Object.fromEntries(fields)}
    }));
  }
}

async function readEvents(stream, body, signal, onChunk) {
  const reader = body.getReader();
  const decoder = new TextDecoder();
  let trailing = '';
  let skipLF = false;
  let type = '';
  let data = [];
  try {
    while (!signal.aborted && !stream.complete && isAttached(stream)) {
      let result;
      try {
        result = await reader.read();
      } catch {
        return;
      }
      const {done, value} = result;
      if (done || signal.aborted || !isAttached(stream)) return;
      onChunk();
      let chunk = decoder.decode(value, {stream: true});
      if (!chunk) continue;
      if (skipLF && chunk.startsWith('\n')) chunk = chunk.slice(1);
      if (!chunk) {
        skipLF = false;
        continue;
      }
      const text = trailing + chunk;
      const lines = text.split(/\r\n|\r|\n/);
      trailing = lines.pop();
      skipLF = text.endsWith('\r');
      for (const line of lines) {
        if (signal.aborted || stream.complete) return;
        if (line === '') {
          if (type) handleEvent(stream, type, data.join('\n'));
          type = '';
          data = [];
          continue;
        }
        const colon = line.indexOf(':');
        const field = colon === -1 ? line : line.slice(0, colon);
        const value = colon === -1 ? '' : line.slice(colon + 1).replace(/^ /, '');
        if (field === 'event') type = value;
        else if (field === 'data') data.push(value);
      }
    }
  } finally {
    reader.releaseLock();
  }
}

function resume(stream) {
  if (!canRun(stream) || stream.controller) return;
  clearRetry(stream);
  const wait = Math.max(stream.retryAt, stream.retryAfter) - Date.now();
  if (wait > 0) {
    stream.timer = setTimeout(() => resume(stream), Math.min(wait, 2_147_483_647));
    return;
  }
  void open(stream);
}

async function open(stream) {
  const controller = new AbortController();
  stream.controller = controller;
  let watchdog;
  let timedOut = false;
  function expectChunk() {
    clearTimeout(watchdog);
    watchdog = setTimeout(() => {
      timedOut = true;
      controller.abort();
    }, chunkTimeout);
  }

  try {
    expectChunk();
    let response;
    try {
      response = await window.fetch(stream.url, {
        cache: 'no-store',
        headers: {Accept: 'text/event-stream', 'Datastar-Request': 'true'},
        signal: controller.signal
      });
    } catch {
      return;
    }
    if (controller.signal.aborted) return;
    const target = response.redirected && response.url ? new URL(response.url) : null;
    if (response.status === 401 || target?.origin === location.origin && target.pathname === '/login') {
      pauseForAuth();
      login();
      return;
    }
    stream.retryAfter = retryAfter(response);
    if (!response.ok || !response.body || response.headers.get('Content-Type')?.split(';')[0].trim().toLowerCase() !== 'text/event-stream') return;
    const openedAt = Date.now();
    expectChunk();
    await readEvents(stream, response.body, controller.signal, () => {
      expectChunk();
      if (Date.now() - openedAt >= 15_000) stream.backoff = 1000;
    });
  } finally {
    clearTimeout(watchdog);
    const paused = controller.signal.aborted && !timedOut;
    controller.abort();
    stream.controller = undefined;
    if (canRun(stream)) {
      if (!paused) {
        const wait = Math.min(stream.backoff, 24_000) * (1 + Math.random() * 0.25);
        stream.retryAt = Math.max(stream.retryAt, Date.now() + wait);
        stream.backoff = Math.min(stream.backoff * 2, 30_000);
      }
      resume(stream);
    }
  }
}

function disconnect(stream) {
  streams.delete(stream.el);
  pause(stream);
}

window.__tsLive = {
  connect(el, url) {
    const existing = streams.get(el);
    if (existing?.url === url) return;
    if (existing) disconnect(existing);
    const stream = {el, url, backoff: 1000, retryAt: 0, retryAfter: 0, complete: false};
    streams.set(el, stream);
    resume(stream);
  }
};

new MutationObserver(() => {
  for (const stream of streams.values()) {
    if (!isAttached(stream)) disconnect(stream);
  }
}).observe(document.documentElement, {subtree: true, childList: true, attributes: true, attributeFilter: ['data-init']});

function wake(reconnect = false) {
  for (const stream of streams.values()) {
    if (canRun(stream)) {
      stream.retryAt = 0;
      stream.backoff = 1000;
      if (reconnect) stream.controller?.abort();
      resume(stream);
    } else {
      pause(stream);
    }
  }
  scheduleReload();
}

document.addEventListener('visibilitychange', () => {
  if (!document.hidden) authPaused = false;
  wake();
});
window.addEventListener('online', () => wake(true));
window.addEventListener('offline', () => wake());
window.addEventListener('pagehide', () => {
  pageHidden = true;
  clearTimeout(navigationTimer);
  navigationTimer = undefined;
  wake();
});
window.addEventListener('pageshow', event => {
  pageHidden = false;
  if (event.persisted) authPaused = false;
  wake(event.persisted);
});
