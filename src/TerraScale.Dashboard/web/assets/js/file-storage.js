class UploadError extends Error {
  constructor(message, retryable = true) {
    super(message);
    this.retryable = retryable;
  }
}

const maxSize = 100 * 1024 * 1024;
const parallel = 3;
const fileId = /^[0-7][0-9A-HJKMNP-TV-Z]{25}$/;
const signedHeaders = ['content-type', 'content-length', 'x-amz-checksum-sha256'];
const refused = 'The upload could not be authorized. Reload the bucket and try again.';
const messages = {
  401: 'Sign in again before uploading.',
  403: 'Your role cannot upload to this bucket.',
  404: 'This bucket is no longer available in the selected project.',
  409: 'The upload is incomplete. Retry the upload.',
  422: 'Check the file name and the metadata.',
  429: 'The shared database and file storage allowance is full.',
};

function size(value) {
  if (value < 1000) return `${value} B`;
  let amount = value / 1000;
  let unit = 0;
  while (amount >= 999.95 && unit < 4) {
    amount /= 1000;
    unit++;
  }
  return `${amount.toFixed(1)} ${['kB', 'MB', 'GB', 'TB', 'PB'][unit]}`;
}

async function post(url, body) {
  const response = await fetch(url, { method: 'POST', body, credentials: 'same-origin', redirect: 'error', headers: { Accept: 'application/json' } });
  const text = await response.text();
  if (!response.ok) throw new UploadError(messages[response.status] ?? 'The request failed. Retry the upload.');
  return text ? JSON.parse(text) : null;
}

function authorized(capability) {
  const target = new URL(capability.url);
  if (target.protocol !== 'https:' || target.username || target.password || target.hash || !target.hostname.endsWith('.r2.cloudflarestorage.com') || !fileId.test(capability.id))
    throw new UploadError(refused);
  const headers = [];
  for (const [name, value] of Object.entries(capability.headers)) {
    if (!signedHeaders.includes(name.toLowerCase()) || typeof value !== 'string') throw new UploadError(refused);
    if (name.toLowerCase() !== 'content-length') headers.push([name, value]);
  }
  return { target, headers };
}

function transfer(target, headers, file, progress) {
  return new Promise((resolve, reject) => {
    const request = new XMLHttpRequest();
    request.open('PUT', target);
    for (const [name, value] of headers) request.setRequestHeader(name, value);
    request.upload.addEventListener('progress', event => {
      if (event.lengthComputable) progress(event.loaded / event.total);
    });
    request.addEventListener('load', () => (request.status >= 200 && request.status < 300 ? resolve() : reject(new UploadError('The transfer was refused. Retry the upload.'))));
    request.addEventListener('error', () => reject(new UploadError('The connection dropped. Retry the upload.')));
    request.addEventListener('abort', () => reject(new UploadError('The upload stopped. Retry the upload.')));
    request.send(file);
  });
}

function metadataOf(field) {
  const text = field.value.trim() || '{}';
  let parsed;
  try {
    parsed = JSON.parse(text);
  } catch {
    throw new UploadError('Metadata must be a JSON object.');
  }
  if (!parsed || typeof parsed !== 'object' || Array.isArray(parsed)) throw new UploadError('Metadata must be a JSON object.');
  if (new TextEncoder().encode(text).length > 16384) throw new UploadError('Metadata must be at most 16 KiB.');
  return text;
}

function install(section) {
  const input = section.querySelector('[data-upload-input]');
  const zone = section.querySelector('[data-upload-zone]');
  const queue = section.querySelector('[data-upload-queue]');
  const actions = section.querySelector('[data-upload-actions]');
  const status = section.querySelector('[data-upload-status]');
  const template = section.querySelector('template[data-upload-row]');
  const metadata = section.querySelector('[data-upload-metadata]');
  const action = new URL(section.dataset.action, location.href);
  const complete = new URL(action);
  complete.pathname = complete.pathname.replace(/\/upload$/, '/complete');
  const items = new Set();
  const waiting = [];
  let active = 0;
  let refreshing = null;
  let stale = false;

  function form(fields) {
    const body = new FormData();
    body.set('_csrf', section.dataset.csrf);
    body.set('bucket', section.dataset.bucket);
    for (const [name, value] of Object.entries(fields)) body.set(name, value);
    return body;
  }

  function show(item, state, text, value) {
    item.element.dataset.state = state;
    item.status.textContent = text;
    if (value === undefined) item.progress.removeAttribute('value');
    else item.progress.value = value;
  }

  function announce() {
    const all = [...items];
    const done = all.filter(item => item.element.dataset.state === 'done').length;
    const failed = all.filter(item => item.element.dataset.state === 'failed').length;
    const busy = all.length - done - failed;
    status.textContent = all.length === 0 ? '' : `${done} of ${all.length} uploaded${failed ? `, ${failed} failed` : ''}.`;
    actions.hidden = busy > 0 || done === 0;
    queue.hidden = all.length === 0;
  }

  async function refresh() {
    if (refreshing) {
      stale = true;
      return refreshing;
    }
    refreshing = (async () => {
      do {
        stale = false;
        try {
          const response = await fetch(location.href, { credentials: 'same-origin', headers: { Accept: 'text/html' } });
          const fresh = new DOMParser().parseFromString(await response.text(), 'text/html').getElementById('bucket-files');
          const current = document.getElementById('bucket-files');
          if (!response.ok || !fresh || !current) throw new Error('refresh');
          current.replaceWith(document.adoptNode(fresh));
        } catch {
          status.textContent = 'Uploaded. Reload the page to see the new files.';
        }
      } while (stale);
      refreshing = null;
    })();
    return refreshing;
  }

  async function upload(item) {
    const { file } = item;
    if (file.size < 1 || file.size > maxSize) throw new UploadError('Files can be 1 byte to 100 MiB.', false);
    const fields = { metadata: metadataOf(metadata), name: file.name, contentType: file.type || 'application/octet-stream', size: String(file.size) };
    show(item, 'hashing', 'Preparing…');
    const digest = new Uint8Array(await crypto.subtle.digest('SHA-256', await file.arrayBuffer()));
    fields.sha256 = btoa(String.fromCharCode(...digest));
    const capability = await post(action, form(fields));
    const { target, headers } = authorized(capability);
    show(item, 'uploading', 'Uploading 0%', 0);
    await transfer(target, headers, file, fraction => show(item, 'uploading', `Uploading ${Math.floor(fraction * 100)}%`, Math.round(fraction * 100)));
    show(item, 'verifying', 'Verifying…');
    await post(complete, form({ fileId: capability.id }));
    show(item, 'done', 'Uploaded', 100);
  }

  async function run(item) {
    active++;
    try {
      await upload(item);
      refresh();
    } catch (error) {
      item.element.toggleAttribute('data-retryable', !(error instanceof UploadError) || error.retryable);
      show(item, 'failed', error instanceof UploadError ? error.message : 'Upload failed. Retry the upload.', 0);
    } finally {
      active--;
      pump();
      announce();
    }
  }

  function pump() {
    while (active < parallel && waiting.length > 0) run(waiting.shift());
  }

  function enqueue(item) {
    item.element.removeAttribute('data-retryable');
    show(item, 'queued', 'Waiting…', 0);
    waiting.push(item);
    announce();
    pump();
  }

  function add(files) {
    for (const file of files) {
      const element = template.content.firstElementChild.cloneNode(true);
      const item = { file, element, status: element.querySelector('[data-status]'), progress: element.querySelector('[data-progress]') };
      element.querySelector('[data-name]').textContent = file.name;
      element.querySelector('[data-name]').title = file.name;
      element.querySelector('[data-size]').textContent = size(file.size);
      item.progress.setAttribute('aria-label', `Upload of ${file.name}`);
      const retry = element.querySelector('[data-retry]');
      retry.setAttribute('aria-label', `Retry ${file.name}`);
      retry.addEventListener('click', () => enqueue(item));
      const remove = element.querySelector('[data-remove]');
      remove.setAttribute('aria-label', `Remove ${file.name}`);
      remove.addEventListener('click', () => {
        if (!['queued', 'done', 'failed'].includes(element.dataset.state)) return;
        const at = waiting.indexOf(item);
        if (at >= 0) waiting.splice(at, 1);
        items.delete(item);
        element.remove();
        announce();
      });
      items.add(item);
      queue.append(element);
      enqueue(item);
    }
  }

  input.addEventListener('change', () => {
    add([...input.files]);
    input.value = '';
  });
  zone.addEventListener('click', event => {
    if (!event.target.closest('[data-upload-browse]')) input.click();
  });
  document.addEventListener('click', event => {
    if (event.target.closest('[data-upload-browse]')) input.click();
  });
  actions.querySelector('[data-upload-clear]').addEventListener('click', () => {
    for (const item of [...items]) {
      if (item.element.dataset.state !== 'done') continue;
      items.delete(item);
      item.element.remove();
    }
    announce();
    zone.querySelector('[data-upload-browse]').focus();
  });

  const carriesFiles = event => event.dataTransfer?.types.includes('Files');
  document.addEventListener('dragenter', event => {
    if (carriesFiles(event)) zone.dataset.dragging = '';
  });
  document.addEventListener('dragover', event => {
    if (!carriesFiles(event)) return;
    event.preventDefault();
    event.dataTransfer.dropEffect = 'copy';
    zone.dataset.dragging = '';
  });
  document.addEventListener('dragleave', event => {
    if (event.relatedTarget === null) delete zone.dataset.dragging;
  });
  document.addEventListener('drop', event => {
    if (!carriesFiles(event)) return;
    event.preventDefault();
    delete zone.dataset.dragging;
    add([...event.dataTransfer.files]);
  });
  window.addEventListener('beforeunload', event => {
    if (active > 0 || waiting.length > 0) event.preventDefault();
  });
  if (input.files.length > 0) {
    add([...input.files]);
    input.value = '';
  }
}

for (const section of document.querySelectorAll('[data-upload]')) install(section);
