const form = document.getElementById('auth-export-form');
if (form) {
  const status = document.getElementById('auth-export-status');
  const cursor = form.elements.namedItem('after');
  const submit = form.querySelector('[type=submit]');
  const reset = form.querySelector('[type=reset]');
  const controls = [...form.querySelectorAll('button, select')];
  let complete = false;
  form.addEventListener('submit', async event => {
    event.preventDefault();
    const body = new FormData(form);
    controls.forEach(control => { control.disabled = true; });
    status.textContent = 'Downloading…';
    try {
      const response = await fetch(form.action, { method: 'POST', body, credentials: 'same-origin', cache: 'no-store' });
      if (!response.ok) {
        const messages = {
          401: 'Sign in again before resuming.',
          403: 'Access was refused. Reload Users to refresh your session and check owner or admin access.',
          409: 'The selected project changed. Reload Users before exporting.',
          422: 'The format or page was refused. Reload Users or start over.',
        };
        status.textContent = `${messages[response.status] ?? 'Download failed; retry later.'} Retry to continue from this page.`;
        return;
      }
      const next = response.headers.get('X-Export-Next');
      if (!response.headers.get('X-Export-Created-Before')) throw new Error('Export metadata is missing.');
      // Hold only this page; never fetch the next page automatically or persist directory data.
      const url = URL.createObjectURL(await response.blob());
      const link = document.createElement('a');
      link.href = url;
      link.download = `users-${body.get('project_id')}.${body.get('format')}`;
      document.body.append(link);
      link.click();
      link.remove();
      setTimeout(() => URL.revokeObjectURL(url), 1000);
      cursor.value = next ?? '';
      complete = !next;
      reset.hidden = false;
      submit.textContent = next ? 'Next page' : 'Export complete';
      status.textContent = next ? 'Page downloaded.' : 'Final page downloaded.';
    } catch {
      status.textContent = 'Download failed. Retry to continue from this page.';
    } finally {
      controls.forEach(control => { control.disabled = false; });
      submit.disabled = complete;
      (complete ? reset : submit).focus();
    }
  });
  form.addEventListener('reset', () => {
    complete = false;
    cursor.value = '';
    submit.disabled = false;
    submit.textContent = 'Download';
    status.textContent = '';
    reset.hidden = true;
    submit.focus();
  });
}
