// Raw cell JSON preserves integer precision and the ordered, possibly duplicate column names.
const pageSize = 200;
const isText = raw => raw.startsWith('"');
const text = raw => isText(raw) ? JSON.parse(raw) : raw;
const csvText = value => `"${(/^[\s\uFEFF]*[=+\-@]/.test(value) ? "'" + value : value).replaceAll('"', '""')}"`;

for (const host of document.querySelectorAll('[data-sql-result]')) {
  const feedback = host.querySelector('[data-sql-feedback]');
  if (!feedback) continue;
  try {
    const { columns, rows } = JSON.parse(host.querySelector('[data-sql-result-data]').textContent);
    const pages = host.querySelector('[data-sql-pages]');
    const buttons = [...pages.querySelectorAll('button')];
    const last = Math.max(0, Math.ceil(rows.length / pageSize) - 1);
    let page = 0;
    const show = target => {
      page = Math.max(0, Math.min(last, target));
      const start = page * pageSize;
      const fragment = document.createDocumentFragment();
      for (const row of rows.slice(start, start + pageSize)) {
        const tr = document.createElement('tr');
        for (const raw of row) {
          const td = document.createElement('td');
          td.textContent = text(raw);
          td.dataset.kind = raw === 'null' ? 'null' : isText(raw) ? 'string' : 'number';
          if (td.dataset.kind === 'number') td.className = 'ts-num';
          tr.append(td);
        }
        fragment.append(tr);
      }
      host.querySelector('tbody')?.replaceChildren(fragment);
      host.querySelector('[data-sql-range]').textContent = `Rows ${rows.length ? start + 1 : 0}–${Math.min(start + pageSize, rows.length)} of ${rows.length}`;
      buttons[0].disabled = page === 0;
      buttons[1].disabled = page === last;
    };
    pages.addEventListener('click', event => {
      const button = event.target.closest('button[data-page]');
      if (button) show(page + Number(button.dataset.page));
    });
    pages.addEventListener('keydown', event => {
      if (event.altKey || event.ctrlKey || event.metaKey || event.shiftKey) return;
      const target = { ArrowLeft: page - 1, ArrowRight: page + 1, Home: 0, End: last }[event.key];
      if (target === undefined) return;
      event.preventDefault();
      show(target);
    });
    const json = () => `{"columns":${JSON.stringify(columns)},"rows":[${rows.map(row => `[${row.join(',')}]`).join(',')}]}`;
    const csv = () => [columns.map(column => csvText(column.name)).join(','), ...rows.map(row => row.map(raw => raw === 'null' ? '\\N' : isText(raw) ? csvText(text(raw)) : raw).join(','))].join('\r\n') + '\r\n';
    host.querySelector('[data-sql-exports]').addEventListener('click', async event => {
      const button = event.target.closest('[data-export]');
      if (!button) return;
      const format = button.dataset.export;
      let url;
      const link = document.createElement('a');
      try {
        if (format === 'copy') {
          await navigator.clipboard.writeText(json());
          feedback.textContent = 'JSON copied.';
        } else {
          url = URL.createObjectURL(new Blob([format === 'csv' ? csv() : json()], { type: format === 'csv' ? 'text/csv;charset=utf-8' : 'application/json' }));
          link.href = url;
          link.download = `query-result.${format}`;
          host.append(link);
          link.click();
          feedback.textContent = 'Download requested. Check your browser downloads; allow downloads if blocked.';
        }
      } catch {
        feedback.textContent = format === 'copy' ? 'Could not copy JSON. Try Download JSON instead.' : 'Could not start the download. Try copying JSON instead.';
      } finally {
        link.remove();
        if (url) setTimeout(() => URL.revokeObjectURL(url), 1000);
      }
    });
    show(0);
    pages.hidden = last === 0;
    host.querySelector('[data-sql-exports]').hidden = false;
    host.querySelector('[data-sql-fallback]').hidden = true;
  } catch {
    feedback.textContent = 'Full results could not be opened. The first 200 rows remain available.';
  }
}
