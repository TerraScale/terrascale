const form = document.getElementById('auth-export-form');
if (form) {
  const status = document.getElementById('auth-export-status');
  const cutoff = document.getElementById('auth-export-cutoff');
  const cursor = form.elements.namedItem('after');
  const submit = form.querySelector('[type=submit]');
  const reset = form.querySelector('[type=reset]');
  const controls = [...form.querySelectorAll('button, select, textarea')];
  let complete = false;
  form.addEventListener('submit', async event => {
    event.preventDefault();
    const body = new FormData(form);
    controls.forEach(control => { control.disabled = true; });
    status.textContent = 'Downloading one page…';
    try {
      const response = await fetch(form.action, { method: 'POST', body, credentials: 'same-origin', cache: 'no-store' });
      if (!response.ok) {
        const messages = {
          401: 'Sign in again before resuming.',
          403: 'Access was refused. Reload Users to refresh your session and check owner or admin access.',
          409: 'The selected project changed. Reload Users before exporting.',
          422: 'The format or resume cursor was refused. Reload Users or start over.',
        };
        status.textContent = `${messages[response.status] ?? 'Download failed; retry later.'} Your cursor is unchanged.`;
        return;
      }
      const next = response.headers.get('X-Export-Next');
      const createdBefore = response.headers.get('X-Export-Created-Before');
      if (!createdBefore) throw new Error('Export metadata is missing. Your cursor is unchanged; retry.');
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
      cutoff.textContent = `Creation cutoff: ${createdBefore}`;
      complete = !next;
      submit.textContent = next ? 'Download next page' : 'Export complete';
      status.textContent = next ? 'Page downloaded. Save the resume cursor or download the next page.' : 'Final page downloaded. Start over to create a new export.';
    } catch {
      status.textContent = 'Download failed. Your cursor is unchanged; retry later.';
    } finally {
      controls.forEach(control => { control.disabled = false; });
      submit.disabled = complete;
      (complete ? reset : submit).focus();
    }
  });
  cursor.addEventListener('input', () => { complete = false; submit.disabled = false; submit.textContent = 'Download page'; cutoff.textContent = ''; });
  form.addEventListener('reset', () => { complete = false; submit.disabled = false; submit.textContent = 'Download page'; cutoff.textContent = ''; status.textContent = 'Ready to download one page.'; });
}
