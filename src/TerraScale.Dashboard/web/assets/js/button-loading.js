import './workspace.js';

// Shared feedback for native form submissions and Datastar requests. Keep
// submit controls enabled so their name/value still reaches the server.
const pending = new Map();
const submitters = new WeakMap();
const requests = new Map();

function start(button, native = false) {
  if (!button?.matches('button.ts-btn, a.ts-btn')) return;
  let state = pending.get(button);
  if (!state) {
    const icon = button.querySelector(':scope > svg, :scope > span[aria-hidden="true"]');
    const spinner = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
    spinner.setAttribute('viewBox', '0 0 24 24');
    spinner.setAttribute('width', icon?.getAttribute('width') ?? '16');
    spinner.setAttribute('height', icon?.getAttribute('height') ?? '16');
    spinner.setAttribute('aria-hidden', 'true');
    spinner.classList.add('ts-btn-spinner');
    const circle = document.createElementNS(spinner.namespaceURI, 'circle');
    for (const [name, value] of Object.entries({cx: '12', cy: '12', r: '9', fill: 'none', stroke: 'currentColor', 'stroke-width': '2', 'stroke-linecap': 'round', 'stroke-dasharray': '42 15'})) {
      circle.setAttribute(name, value);
    }
    spinner.append(circle);
    state = {icon, spinner, busy: button.getAttribute('aria-busy'), disabled: button.getAttribute('aria-disabled'), count: 0, native: false};
    pending.set(button, state);
    icon?.classList.add('ts-btn-idle-icon');
    if (icon) icon.before(spinner);
    else button.prepend(spinner);
    button.classList.add('ts-btn-loading');
    button.setAttribute('aria-busy', 'true');
    button.setAttribute('aria-disabled', 'true');
  }
  state.count++;
  state.native ||= native;
}

function stop(button, reset = false) {
  const state = pending.get(button);
  if (!state || (!reset && --state.count > 0)) return;
  state.spinner.remove();
  state.icon?.classList.remove('ts-btn-idle-icon');
  button.classList.remove('ts-btn-loading');
  for (const [name, value] of [['aria-busy', state.busy], ['aria-disabled', state.disabled]]) {
    if (value === null) button.removeAttribute(name);
    else button.setAttribute(name, value);
  }
  pending.delete(button);
}

document.addEventListener('click', event => {
  if (event.target.closest?.('.ts-btn-loading')) {
    event.preventDefault();
    event.stopImmediatePropagation();
  }
}, true);

window.addEventListener('click', event => {
  const link = event.target.closest?.('a.ts-btn[href]');
  if (!event.defaultPrevented && event.button === 0 && !event.metaKey && !event.ctrlKey && !event.shiftKey && !event.altKey && link && !link.hasAttribute('download') && (!link.target || link.target === '_self') && link.origin === location.origin && (link.pathname !== location.pathname || link.search !== location.search)) start(link, true);
});

document.addEventListener('submit', event => {
  const form = event.target;
  if ([...pending.keys()].some(button => button.form === form)) {
    event.preventDefault();
    event.stopImmediatePropagation();
    return;
  }
  // requestSubmit() without a submitter uses the form action. Prefer its
  // primary action for feedback, rather than a secondary formaction button.
  submitters.set(form, event.submitter ?? form.querySelector('button.ts-btn-primary[type="submit"]:not([formaction]):enabled, button.ts-btn-primary:not([type]):not([formaction]):enabled') ?? form.querySelector('button.ts-btn:not([type]):not([formaction]):enabled, button.ts-btn[type="submit"]:not([formaction]):enabled'));
}, true);

window.addEventListener('submit', event => {
  const form = event.target;
  const button = submitters.get(form);
  const method = event.submitter?.getAttribute('formmethod') ?? form.method;
  const target = event.submitter?.getAttribute('formtarget') ?? form.target;
  if (!event.defaultPrevented && method !== 'dialog' && (!target || target === '_self')) start(button, true);
});

document.addEventListener('datastar-fetch', event => {
  const {type, el} = event.detail;
  if (type === 'started') {
    // Datastar announces a form request before checking HTML validity. An
    // invalid form sends nothing, so it must not leave a busy button behind.
    const form = el instanceof HTMLFormElement ? el : el.closest('form');
    if (form && !form.noValidate && !form.checkValidity()) return;
    const button = el instanceof HTMLFormElement ? submitters.get(el) : el.closest('button.ts-btn, a.ts-btn');
    if (!button) return;
    const queue = requests.get(el) ?? [];
    queue.push(button);
    requests.set(el, queue);
    start(button);
  } else if (type === 'finished') {
    const queue = requests.get(el);
    stop(queue?.shift());
    if (!queue?.length) requests.delete(el);
  }
});

// The browser can restore the old document from its back/forward cache.
window.addEventListener('pageshow', () => {
  for (const button of pending.keys()) stop(button, true);
  requests.clear();
});
window.addEventListener('keydown', event => {
  if (event.key === 'Escape') {
    for (const [button, state] of pending) {
      if (state.native) stop(button, true);
    }
  }
});
