// File bytes go directly to the signed capability. Dashboard requests carry only bounded metadata.
class UploadError extends Error {}
const messages = {
  401: 'Sign in again before uploading.',
  403: 'Your role cannot upload to this bucket.',
  404: 'This bucket is no longer available in the selected project.',
  409: 'The upload is incomplete. Reload files before trying again.',
  429: 'The shared database and file storage allowance is full.',
};

async function post(url, body) {
  const response = await fetch(url, { method: 'POST', body, credentials: 'same-origin', redirect: 'error', headers: { Accept: 'application/json' } });
  if (!response.ok) throw new UploadError(messages[response.status] ?? 'The file request failed. Check the file and metadata, then try again.');
  return response;
}

for (const form of document.querySelectorAll('form[data-file-upload]')) {
  form.addEventListener('submit', async event => {
    event.preventDefault();
    if (form.dataset.uploading) return;
    const status = form.querySelector('[data-file-status]');
    const submit = form.querySelector('button[type=submit]');
    form.dataset.uploading = 'true';
    submit.disabled = true;
    try {
      const file = form.querySelector('input[type=file]').files[0];
      if (!file || file.size < 1 || file.size > 100 * 1024 * 1024) throw new UploadError('Choose a file containing 1 byte–100 MiB.');
      const metadata = form.querySelector('[name=metadata]').value;
      let parsed;
      try { parsed = JSON.parse(metadata); } catch { throw new UploadError('Metadata must be a JSON object.'); }
      if (!parsed || typeof parsed !== 'object' || Array.isArray(parsed)) throw new UploadError('Metadata must be a JSON object.');
      if (new TextEncoder().encode(metadata).length > 16384) throw new UploadError('Metadata must be at most 16 KiB.');
      status.textContent = 'Computing file checksum…';
      const bytes = await file.arrayBuffer();
      const sha256 = btoa(String.fromCharCode(...new Uint8Array(await crypto.subtle.digest('SHA-256', bytes))));
      const body = new FormData();
      for (const name of ['_csrf', 'bucket']) body.set(name, form.querySelector(`[name=${name}]`).value);
      body.set('metadata', metadata);
      body.set('name', file.name);
      body.set('contentType', file.type || 'application/octet-stream');
      body.set('size', String(file.size));
      body.set('sha256', sha256);
      const capability = await (await post(form.action, body)).json();
      const target = new URL(capability.url);
      if (target.protocol !== 'https:' || target.username || target.password || target.hash || !target.hostname.endsWith('.r2.cloudflarestorage.com') ||
          !/^[0-7][0-9A-HJKMNP-TV-Z]{25}$/.test(capability.id)) throw new UploadError('The upload could not be authorized. Reload files and try again.');
      const headers = new Headers();
      for (const [name, value] of Object.entries(capability.headers)) {
        if (!['content-type', 'content-length', 'x-amz-checksum-sha256'].includes(name.toLowerCase()) || typeof value !== 'string')
          throw new UploadError('The upload could not be authorized. Reload files and try again.');
        if (name.toLowerCase() !== 'content-length') headers.set(name, value);
      }
      status.textContent = 'Uploading file…';
      const uploaded = await fetch(target, { method: 'PUT', body: bytes, headers, credentials: 'omit', redirect: 'error' });
      if (!uploaded.ok) throw new UploadError('The object transfer failed. Its storage reservation will be released after cleanup.');
      status.textContent = 'Verifying file…';
      const complete = new URL(form.action);
      complete.pathname = complete.pathname.replace(/\/upload$/, '/complete');
      const confirmation = new FormData();
      for (const name of ['_csrf', 'bucket']) confirmation.set(name, body.get(name));
      confirmation.set('fileId', capability.id);
      await post(complete, confirmation);
      status.textContent = 'File uploaded.';
      const page = new URL(location.href);
      page.searchParams.delete('after');
      location.assign(page);
    } catch (error) {
      // Fetch errors can contain capability URLs; show only fixed messages from this module.
      status.textContent = error instanceof UploadError ? error.message : 'Upload failed. Reload files before trying again; unfinished reservations are released after cleanup.';
    } finally {
      delete form.dataset.uploading;
      submit.disabled = false;
    }
  });
}
