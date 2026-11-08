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
(function () {
  var form = document.getElementById('passkey-add');
  if (!form) return;
  function encode(buffer) {
    var bytes = new Uint8Array(buffer), binary = '';
    for (var i = 0; i < bytes.length; i++) binary += String.fromCharCode(bytes[i]);
    return btoa(binary).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
  }
  function decode(text) {
    var base64 = text.replace(/-/g, '+').replace(/_/g, '/').replace(/=+$/, '');
    base64 = base64 + '='.repeat((4 - base64.length % 4) % 4);
    var binary = atob(base64), bytes = new Uint8Array(binary.length);
    for (var i = 0; i < binary.length; i++) bytes[i] = binary.charCodeAt(i);
    return bytes;
  }
  function json(response) {
    return response.json().then(function (body) {
      if (!response.ok) throw new Error(body.error && body.error.message || 'The passkey was not added.');
      return body;
    });
  }
  function fail(button, message) {
    button.disabled = false;
    button.classList.remove('is-loading');
    button.removeAttribute('aria-busy');
    var alert = document.getElementById('sign-in-error');
    if (!alert) {
      alert = document.createElement('div');
      alert.id = 'sign-in-error';
      alert.className = 'alert';
      alert.setAttribute('data-state', 'error');
      alert.setAttribute('role', 'alert');
      var head = document.querySelector('.head');
      (head || document.body).after(alert);
    }
    alert.innerHTML = '<strong>Passkey not added</strong><p></p>';
    alert.querySelector('p').textContent = message;
  }
  form.addEventListener('submit', function (event) {
    event.preventDefault();
    var button = form.querySelector('button[type="submit"]');
    if (!window.PublicKeyCredential) { fail(button, 'This browser cannot create passkeys.'); return; }
    var name = (form.querySelector('input[name="name"]') || { value: '' }).value;
    var sealed = null;
    fetch(form.dataset.options, { method: 'POST', headers: { 'Accept': 'application/json' } })
      .then(json)
      .then(function (body) {
        sealed = body.challenge;
        var options = body.options;
        return navigator.credentials.create({ publicKey: {
          challenge: decode(options.challenge),
          rp: options.rp,
          user: { id: decode(options.user.id), name: options.user.name, displayName: options.user.displayName },
          pubKeyCredParams: options.pubKeyCredParams,
          timeout: options.timeout,
          excludeCredentials: (options.excludeCredentials || []).map(function (credential) {
            return { type: credential.type, id: decode(credential.id) };
          }),
          authenticatorSelection: options.authenticatorSelection,
          attestation: options.attestation
        } });
      })
      .then(function (assertion) {
        if (!assertion) throw new Error('The passkey was not created.');
        return fetch(form.dataset.verify, {
          method: 'POST',
          headers: { 'Content-Type': 'application/json', 'Accept': 'application/json' },
          body: JSON.stringify({
            challenge: sealed,
            name: name,
            response: {
              id: assertion.id,
              rawId: encode(assertion.rawId),
              type: assertion.type,
              response: {
                clientDataJSON: encode(assertion.response.clientDataJSON),
                attestationObject: encode(assertion.response.attestationObject),
                transports: assertion.response.getTransports ? assertion.response.getTransports() : []
              }
            }
          })
        }).then(json);
      })
      .then(function () { window.location.reload(); })
      .catch(function (error) { fail(button, error.message || 'The passkey was not added.'); });
  });
})();
