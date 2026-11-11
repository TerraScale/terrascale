const checked = new WeakSet();
const messages = new WeakMap();
let sequence = 0;

function fieldElement(element) {
  return element instanceof HTMLInputElement || element instanceof HTMLTextAreaElement || element instanceof HTMLSelectElement;
}

function messageFor(field) {
  const validity = field.validity;
  const label = field.labels?.[0]?.textContent.trim().replace(/[:*]$/, '').toLowerCase() || field.name || 'a value';
  if (validity.valueMissing) return field instanceof HTMLSelectElement ? `Choose ${label}` : `Enter ${label}`;
  if (validity.badInput) return field.type === 'number' ? 'Enter a number' : 'Enter a valid date and time';
  if (validity.typeMismatch) return field.type === 'email' ? 'Enter a valid email address' : 'Enter a valid URL';
  if (field.value && field.minLength > 0 && field.value.length < field.minLength) return `Use at least ${field.minLength} characters`;
  if (field.dataset.lengthReserveField && field.form?.elements.namedItem(field.dataset.lengthReserveField)?.value && field.value.length > field.maxLength - Number(field.dataset.lengthReserve)) return `Use at most ${field.maxLength - Number(field.dataset.lengthReserve)} characters`;
  if (field.maxLength >= 0 && field.value.length > field.maxLength) return `Use at most ${field.maxLength} characters`;
  if (field.dataset.maxBytes && new TextEncoder().encode(field.value).length > Number(field.dataset.maxBytes)) return `Use at most ${field.dataset.maxBytes} bytes`;
  if (validity.patternMismatch) return field.title || 'Use the requested format';
  if (validity.rangeUnderflow) return `Use ${field.min} or more`;
  if (validity.rangeOverflow) return `Use ${field.max} or less`;
  if (field.dataset.minExclusive && field.value && Number(field.value) <= Number(field.dataset.minExclusive)) return `Use more than ${field.dataset.minExclusive}`;
  if (validity.stepMismatch) return field.step === '1' || !field.step ? 'Enter a whole number' : `Use increments of ${field.step}`;
  if (validity.customError) return field.validationMessage;
  return '';
}

function validate(field) {
  if (!field.willValidate) return true;
  checked.add(field);
  const text = messageFor(field);
  let message = messages.get(field);
  if (message && !message.isConnected) {
    (field.closest('ts-code-editor') || field).after(message);
  }
  if (!message && text) {
    message = document.createElement('p');
    message.id = `ts-validation-${++sequence}`;
    message.className = 'ts-validation-message';
    message.setAttribute('aria-live', 'polite');
    message.setAttribute('aria-atomic', 'true');
    const editor = field.closest('ts-code-editor');
    (editor || field).after(message);
    messages.set(field, message);
  }
  if (message) {
    const described = new Set((field.getAttribute('aria-describedby') || '').split(/\s+/).filter(Boolean));
    if (text) described.add(message.id);
    else described.delete(message.id);
    if (described.size) field.setAttribute('aria-describedby', [...described].join(' '));
    else field.removeAttribute('aria-describedby');
    message.textContent = text;
    message.hidden = !text;
  }
  if (text) field.setAttribute('aria-invalid', 'true');
  else field.removeAttribute('aria-invalid');
  return !text;
}

function enhance(root) {
  if (root instanceof HTMLFormElement) root.noValidate = true;
  root.querySelectorAll?.('form').forEach(form => { form.noValidate = true; });
}

enhance(document.documentElement);
new MutationObserver(records => {
  for (const record of records) for (const node of record.addedNodes) if (node instanceof Element) enhance(node);
}).observe(document.documentElement, { childList: true, subtree: true });

document.addEventListener('focusout', event => {
  if (fieldElement(event.target)) validate(event.target);
});
for (const type of ['input', 'change']) document.addEventListener(type, event => {
  if (!fieldElement(event.target)) return;
  if (checked.has(event.target)) validate(event.target);
  for (const field of event.target.form?.elements || []) if (fieldElement(field) && checked.has(field) && field.dataset.lengthReserveField === event.target.name) validate(field);
});
window.addEventListener('submit', event => {
  const form = event.target;
  if (!(form instanceof HTMLFormElement) || form.method === 'dialog' || event.submitter?.formNoValidate) return;
  let first;
  for (const field of form.elements) if (fieldElement(field) && !validate(field)) first ||= field;
  if (!first) return;
  event.preventDefault();
  event.stopImmediatePropagation();
  if (first.closest('ts-code-editor') && first.getAttribute('aria-hidden') === 'true') first.dispatchEvent(new Event('invalid', { cancelable: true }));
  else first.focus();
}, true);
