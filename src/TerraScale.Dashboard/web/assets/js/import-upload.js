// Import uploads: TerraBase needs an export's exact size and SHA-256 before it
// accepts the bytes. The file is hashed in chunks (WebCrypto cannot hash
// incrementally, and a 1 GiB export must not sit in memory), the import is
// created, then the file goes through the dashboard to TerraBase in 4 MiB parts.
// A wrong digest fails closed: TerraBase discards the bytes (source_checksum_mismatch).
const K = Uint32Array.from([
  0x428a2f98, 0x71374491, 0xb5c0fbcf, 0xe9b5dba5, 0x3956c25b, 0x59f111f1, 0x923f82a4, 0xab1c5ed5,
  0xd807aa98, 0x12835b01, 0x243185be, 0x550c7dc3, 0x72be5d74, 0x80deb1fe, 0x9bdc06a7, 0xc19bf174,
  0xe49b69c1, 0xefbe4786, 0x0fc19dc6, 0x240ca1cc, 0x2de92c6f, 0x4a7484aa, 0x5cb0a9dc, 0x76f988da,
  0x983e5152, 0xa831c66d, 0xb00327c8, 0xbf597fc7, 0xc6e00bf3, 0xd5a79147, 0x06ca6351, 0x14292967,
  0x27b70a85, 0x2e1b2138, 0x4d2c6dfc, 0x53380d13, 0x650a7354, 0x766a0abb, 0x81c2c92e, 0x92722c85,
  0xa2bfe8a1, 0xa81a664b, 0xc24b8b70, 0xc76c51a3, 0xd192e819, 0xd6990624, 0xf40e3585, 0x106aa070,
  0x19a4c116, 0x1e376c08, 0x2748774c, 0x34b0bcb5, 0x391c0cb3, 0x4ed8aa4a, 0x5b9cca4f, 0x682e6ff3,
  0x748f82ee, 0x78a5636f, 0x84c87814, 0x8cc70208, 0x90befffa, 0xa4506ceb, 0xbef9a3f7, 0xc67178f2,
]);

function sha256() {
  const h = Uint32Array.from([0x6a09e667, 0xbb67ae85, 0x3c6ef372, 0xa54ff53a, 0x510e527f, 0x9b05688c, 0x1f83d9ab, 0x5be0cd19]);
  const w = new Uint32Array(64);
  const block = new Uint8Array(64);
  let used = 0;
  let length = 0;
  const rotr = (x, n) => (x >>> n) | (x << (32 - n));
  const compress = (bytes, at) => {
    for (let i = 0; i < 16; i++) w[i] = (bytes[at + 4 * i] << 24) | (bytes[at + 4 * i + 1] << 16) | (bytes[at + 4 * i + 2] << 8) | bytes[at + 4 * i + 3];
    for (let i = 16; i < 64; i++) {
      const s0 = rotr(w[i - 15], 7) ^ rotr(w[i - 15], 18) ^ (w[i - 15] >>> 3);
      const s1 = rotr(w[i - 2], 17) ^ rotr(w[i - 2], 19) ^ (w[i - 2] >>> 10);
      w[i] = w[i - 16] + s0 + w[i - 7] + s1;
    }
    let [a, b, c, d, e, f, g, k] = h;
    for (let i = 0; i < 64; i++) {
      const t1 = (k + (rotr(e, 6) ^ rotr(e, 11) ^ rotr(e, 25)) + ((e & f) ^ (~e & g)) + K[i] + w[i]) | 0;
      const t2 = ((rotr(a, 2) ^ rotr(a, 13) ^ rotr(a, 22)) + ((a & b) ^ (a & c) ^ (b & c))) | 0;
      k = g; g = f; f = e; e = (d + t1) | 0; d = c; c = b; b = a; a = (t1 + t2) | 0;
    }
    h[0] += a; h[1] += b; h[2] += c; h[3] += d; h[4] += e; h[5] += f; h[6] += g; h[7] += k;
  };
  return {
    update(bytes) {
      length += bytes.length;
      let i = 0;
      if (used > 0) {
        i = Math.min(64 - used, bytes.length);
        block.set(bytes.subarray(0, i), used);
        used += i;
        if (used < 64) return;
        compress(block, 0);
        used = 0;
      }
      for (; i + 64 <= bytes.length; i += 64) compress(bytes, i);
      block.set(bytes.subarray(i));
      used = bytes.length - i;
    },
    hex() {
      const tail = new Uint8Array(used < 56 ? 64 : 128);
      tail.set(block.subarray(0, used));
      tail[used] = 0x80;
      const view = new DataView(tail.buffer);
      view.setUint32(tail.length - 8, Math.floor(length / 0x20000000));
      view.setUint32(tail.length - 4, (length * 8) >>> 0);
      for (let i = 0; i < tail.length; i += 64) compress(tail, i);
      return Array.from(h, (word) => word.toString(16).padStart(8, '0')).join('');
    },
  };
}

async function fingerprint(file, report) {
  const hash = sha256();
  const reader = file.stream().getReader();
  let read = 0;
  for (;;) {
    const { done, value } = await reader.read();
    if (done) return hash.hex();
    hash.update(value);
    read += value.length;
    report(read / file.size, `Fingerprinting ${Math.floor((100 * read) / file.size)}%`);
  }
}

async function problem(response) {
  try { return (await response.json()).detail || 'The request failed.'; } catch { return 'The request failed.'; }
}

const PART = 4 * 1024 * 1024;

async function call(url, init) {
  const response = await fetch(url, { ...init, headers: { Accept: 'application/json', ...init?.headers } });
  if (!response.ok) throw new Error(await problem(response));
  return response.json();
}

// Parts go in order from what TerraBase already holds, so an interrupted upload continues where it stopped.
async function upload(database, file, csrf, job, report) {
  const path = `/api/v1/dashboard/imports/${encodeURIComponent(database)}`;
  let received = job.source.receivedBytes;
  while (received < file.size) {
    const part = file.slice(received, Math.min(received + PART, file.size));
    job = await call(`${path}/source?offset=${received}`, { method: 'PUT', body: part, headers: { 'Content-Type': 'application/octet-stream', 'X-CSRF-Token': csrf } });
    // TerraBase discards a completed source whose SHA-256 differs and starts over at zero.
    if (job.source.receivedBytes < received) throw new Error('The file was damaged in transit and TerraBase discarded it. Upload the same file again.');
    received = job.source.receivedBytes;
    report(received / file.size, `Uploading ${Math.floor((100 * received) / file.size)}%`);
  }
}

document.addEventListener('submit', async (event) => {
  const form = event.target.closest?.('form[data-import-upload]');
  if (!form) return;
  event.preventDefault();
  const button = form.querySelector('button[type=submit]');
  const status = form.querySelector('[data-import-status]');
  const progress = form.querySelector('[data-import-progress]');
  const report = (fraction, text) => { progress.hidden = false; progress.value = Math.round(fraction * 100); status.textContent = text; };
  const file = form.querySelector('input[type=file]').files[0];
  const csrf = form.querySelector('input[name=_csrf]').value;
  if (!file) return;
  button.disabled = true;
  try {
    if (file.size < 1 || file.size > 2 ** 30) throw new Error('Exports must be between 1 byte and 1 GiB.');
    const digest = await fingerprint(file, report);
    let database = form.dataset.database;
    if (form.dataset.importUpload === 'create') {
      form.elements.sourceBytes.value = String(file.size);
      form.elements.sourceSha256.value = digest;
      const body = new URLSearchParams([...new FormData(form)].filter(([, value]) => typeof value === 'string'));
      const job = await call(form.action, { method: 'POST', body });
      database = job.database;
      // A resubmitted form gets the same import back; only one still waiting for its file takes parts.
      if (job.state === 'awaiting_source') await upload(database, file, csrf, job, report);
    } else {
      if (String(file.size) !== form.dataset.bytes || digest !== form.dataset.sha256) throw new Error('This is not the file this import was created for. Choose the same export.');
      await upload(database, file, csrf, await call(`/api/v1/dashboard/imports/${encodeURIComponent(database)}`), report);
    }
    status.textContent = 'Uploaded. Opening the preview…';
    location.assign(`/imports/${encodeURIComponent(database)}`);
  } catch (error) {
    progress.hidden = true;
    status.textContent = error.message;
    status.setAttribute('role', 'alert');
    button.disabled = false;
  }
});
