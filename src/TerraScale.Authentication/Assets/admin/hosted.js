(function () {
  function mark(button) {
    if (button.classList.contains('is-loading')) return;
    button.classList.add('is-loading');
    button.setAttribute('aria-busy', 'true');
    button.disabled = true;
    var name = button.getAttribute('name');
    if (name === null || name === '' || !button.form) return;
    var hidden = button.ownerDocument.createElement('input');
    hidden.type = 'hidden';
    hidden.name = name;
    hidden.value = button.getAttribute('value') || '';
    hidden.setAttribute('data-loading-submitter', '');
    button.form.append(hidden);
  }
  document.addEventListener('submit', function (event) {
    var form = event.target;
    if (!(form instanceof HTMLFormElement)) return;
    var submitter = event.submitter;
    if (submitter instanceof HTMLButtonElement || submitter instanceof HTMLInputElement) mark(submitter);
  }, true);
  window.addEventListener('pageshow', function (event) {
    if (!event.persisted) return;
    document.querySelectorAll('.is-loading, [aria-busy="true"]').forEach(function (control) {
      control.classList.remove('is-loading');
      control.removeAttribute('aria-busy');
      control.disabled = false;
    });
    document.querySelectorAll('[data-loading-submitter]').forEach(function (hidden) { hidden.remove(); });
  });
})();
