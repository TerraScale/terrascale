const bareWords = new Map([
  ['null', 'NULL'],
  ['true', 'TRUE'],
  ['false', 'FALSE'],
  ['current_timestamp', 'CURRENT_TIMESTAMP'],
  ['current_time', 'CURRENT_TIME'],
  ['current_date', 'CURRENT_DATE'],
]);
const bareNumber = /^-?(?:0|[1-9]\d*)(?:\.\d+)?$/;

function quoteIdent(name) {
  return `"${name.replaceAll('"', '""')}"`;
}

function quoteLiteral(value) {
  const text = value.trim();
  if (text === '') return null;
  if (bareNumber.test(text)) return text;
  const word = bareWords.get(text.toLowerCase());
  if (word) return word;
  return `'${text.replaceAll("'", "''")}'`;
}

function rows(form) {
  return [...form.querySelectorAll('[data-column-row]')];
}

function columnSql(row) {
  const parts = [
    quoteIdent(row.querySelector('[name="columnName"]').value.trim()),
    row.querySelector('[name="columnType"]').value,
  ];
  if (row.querySelector('[name="columnPk"]').checked) parts.push('PRIMARY KEY');
  if (row.querySelector('[name="columnNotNull"]').checked) parts.push('NOT NULL');
  if (row.querySelector('[name="columnUnique"]').checked) parts.push('UNIQUE');
  const literal = quoteLiteral(row.querySelector('[name="columnDefault"]').value);
  if (literal !== null) parts.push(`DEFAULT ${literal}`);
  return parts.join(' ');
}

function statement(form) {
  const name = form.querySelector('[name="tableName"]').value.trim();
  const exists = form.querySelector('[name="ifNotExists"]').checked;
  const body = rows(form).map(row => `  ${columnSql(row)}`).join(',\n');
  return `CREATE TABLE ${exists ? 'IF NOT EXISTS ' : ''}${quoteIdent(name)} (\n${body}\n);`;
}

let painting = false;

function preview(form) {
  if (painting) return;
  const field = form.querySelector('#create-table-sql');
  const sql = statement(form);
  if (field.value === sql) return;
  painting = true;
  field.value = sql;
  field.dispatchEvent(new Event('input', { bubbles: true }));
  painting = false;
}

function nextIndex(form) {
  const used = new Set(rows(form).map(row => row.querySelector('[name="columnIndex"]').value));
  let index = 0;
  while (used.has(String(index))) index += 1;
  return String(index);
}

function syncRow(row, index) {
  row.querySelector('[name="columnIndex"]').value = index;
  for (const box of row.querySelectorAll('[data-column-flag]')) box.value = index;
}

function paint(form) {
  const list = rows(form);
  for (const row of list) row.querySelector('[data-remove-column]').disabled = list.length < 2;
  form.querySelector('[data-add-column]').disabled = list.length >= 32;
  preview(form);
}

function boot() {
  const form = document.querySelector('#create-table-form');
  if (!form) return;
  form.addEventListener('input', () => preview(form));
  form.addEventListener('click', event => {
    const add = event.target.closest('[data-add-column]');
    if (add) {
      if (rows(form).length >= 32) return;
      const row = document.querySelector('#create-table-column').content.firstElementChild.cloneNode(true);
      syncRow(row, nextIndex(form));
      form.querySelector('[data-column-rows]').append(row);
      paint(form);
      return;
    }
    const remove = event.target.closest('[data-remove-column]');
    if (!remove || rows(form).length < 2) return;
    remove.closest('[data-column-row]').remove();
    paint(form);
  });
  paint(form);
}

boot();
