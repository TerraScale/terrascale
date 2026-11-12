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
function passkeyEncode(buffer) {
  var bytes = new Uint8Array(buffer), binary = '';
  for (var i = 0; i < bytes.length; i++) binary += String.fromCharCode(bytes[i]);
  return btoa(binary).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
}
function passkeyDecode(text) {
  var base64 = text.replace(/-/g, '+').replace(/_/g, '/').replace(/=+$/, '');
  base64 = base64 + '='.repeat((4 - base64.length % 4) % 4);
  var binary = atob(base64), bytes = new Uint8Array(binary.length);
  for (var i = 0; i < binary.length; i++) bytes[i] = binary.charCodeAt(i);
  return bytes;
}
(function () {
  var form = document.getElementById('passkey-add');
  if (!form) return;
  var i18n = JSON.parse(form.dataset.i18n || '{}');
  function json(response) {
    return response.json().then(function (body) {
      if (!response.ok) throw new Error(body.error && body.error.message || i18n.notAdded || '');
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
    alert.innerHTML = '<strong></strong><p></p>';
    alert.querySelector('strong').textContent = i18n.notAddedTitle || '';
    alert.querySelector('p').textContent = message;
  }
  form.addEventListener('submit', function (event) {
    event.preventDefault();
    var button = form.querySelector('button[type="submit"]');
    if (!window.PublicKeyCredential) { fail(button, i18n.unsupported || ''); return; }
    var name = (form.querySelector('input[name="name"]') || { value: '' }).value;
    var sealed = null;
    var headers = { 'Accept': 'application/json' };
    if (form.dataset.enrollment) headers['X-TerraScale-Mfa-Challenge'] = form.dataset.enrollment;
    fetch(form.dataset.options, { method: 'POST', headers: headers })
      .then(json)
      .then(function (body) {
        sealed = body.challenge;
        var options = body.options;
        return navigator.credentials.create({ publicKey: {
          challenge: passkeyDecode(options.challenge),
          rp: options.rp,
          user: { id: passkeyDecode(options.user.id), name: options.user.name, displayName: options.user.displayName },
          pubKeyCredParams: options.pubKeyCredParams,
          timeout: options.timeout,
          excludeCredentials: (options.excludeCredentials || []).map(function (credential) {
            return { type: credential.type, id: passkeyDecode(credential.id) };
          }),
          authenticatorSelection: options.authenticatorSelection,
          attestation: options.attestation
        } });
      })
      .then(function (assertion) {
        if (!assertion) throw new Error(i18n.notCreated || '');
        return fetch(form.dataset.verify, {
          method: 'POST',
          headers: Object.assign({ 'Content-Type': 'application/json' }, headers),
          body: JSON.stringify({
            challenge: sealed,
            name: name,
            response: {
              id: assertion.id,
              rawId: passkeyEncode(assertion.rawId),
              type: assertion.type,
              response: {
                clientDataJSON: passkeyEncode(assertion.response.clientDataJSON),
                attestationObject: passkeyEncode(assertion.response.attestationObject),
                transports: assertion.response.getTransports ? assertion.response.getTransports() : []
              }
            }
          })
        }).then(json);
      })
      .then(function () { if (form.dataset.complete) window.location.assign(form.dataset.complete); else window.location.reload(); })
      .catch(function (error) { fail(button, error.message || i18n.notAdded || ''); });
  });
})();
(function () {
  var button = document.getElementById('passkey-sign-in');
  if (!button || !window.PublicKeyCredential) return;
  var i18n = JSON.parse(button.dataset.i18n || '{}');
  button.hidden = false;
  var email = document.getElementById('sign-in-email');
  function reset() {
    button.disabled = false;
    button.classList.remove('is-loading');
    button.removeAttribute('aria-busy');
  }
  function fail(message, body) {
    reset();
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
    alert.innerHTML = '<strong></strong><p></p>';
    alert.querySelector('strong').textContent = message;
    alert.querySelector('p').textContent = body;
  }
  function json(response) {
    return response.json().then(function (body) {
      if (!response.ok) throw new Error(i18n.signInFailed || '');
      return body;
    });
  }
  function follow(html) {
    var page = document.createElement('template');
    page.innerHTML = html;
    var link = page.content.querySelector('a.delayed-continue') || page.content.querySelector('a[data-continue]');
    if (link) {
      var target = new URL(link.getAttribute('href'), window.location.href);
      if (target.origin === window.location.origin || target.protocol === 'https:') window.location.replace(target.href);
      else window.location.reload();
    } else if (button.dataset.signIn) {
      var signIn = new URL(button.dataset.signIn, window.location.href);
      window.location.replace(signIn.origin === window.location.origin ? signIn.href : window.location.href);
    } else window.location.reload();
  }
  button.addEventListener('click', function () {
    var address = email ? email.value.trim() : '';
    if (!address) {
      if (email) email.focus();
      fail(i18n.emailTitle || '', i18n.emailBody || '');
      return;
    }
    button.disabled = true;
    button.classList.add('is-loading');
    button.setAttribute('aria-busy', 'true');
    var next = button.form && button.form.querySelector('input[name="next"]');
    var sealed = null;
    fetch(button.dataset.options, { method: 'POST', headers: { 'Accept': 'application/json' }, body: new URLSearchParams({ email: address }) })
      .then(json)
      .then(function (body) {
        sealed = body.challenge;
        var options = body.options;
        return navigator.credentials.get({ publicKey: {
          challenge: passkeyDecode(options.challenge),
          rpId: options.rpId,
          timeout: options.timeout,
          userVerification: options.userVerification,
          allowCredentials: (options.allowCredentials || []).map(function (credential) {
            return { type: credential.type, id: passkeyDecode(credential.id) };
          })
        } });
      })
      .then(function (assertion) {
        if (!assertion) throw new Error(i18n.signInFailed || '');
        return fetch(button.dataset.verify, {
          method: 'POST',
          headers: { 'Content-Type': 'application/json', 'Accept': 'application/json' },
          body: JSON.stringify({
            challenge: sealed,
            email: address,
            next: next ? next.value : null,
            response: {
              id: assertion.id,
              rawId: passkeyEncode(assertion.rawId),
              type: assertion.type,
              response: {
                clientDataJSON: passkeyEncode(assertion.response.clientDataJSON),
                authenticatorData: passkeyEncode(assertion.response.authenticatorData),
                signature: passkeyEncode(assertion.response.signature),
                userHandle: assertion.response.userHandle ? passkeyEncode(assertion.response.userHandle) : null
              }
            }
          })
        });
      })
      .then(function (response) {
        if (response.redirected) { window.location.replace(response.url); return; }
        if (!response.ok) throw new Error(i18n.signInFailed || '');
        return response.text().then(follow);
      })
      .catch(function (error) {
        if (error && error.name === 'NotAllowedError') { reset(); return; }
        fail(i18n.signInFailedTitle || '', i18n.signInAgain || '');
      });
  });
})();
(function () {
  document.querySelectorAll('button[data-wait-seconds]').forEach(function (button) {
    var remaining = parseInt(button.getAttribute('data-wait-seconds'), 10);
    var label = button.getAttribute('data-label') || button.textContent;
    var format = button.getAttribute('data-wait-format') || '{0} {1}';
    function tick() {
      if (remaining > 0) {
        button.textContent = format.replace('{0}', label).replace('{1}', remaining);
        remaining -= 1;
        setTimeout(tick, 1000);
      } else {
        button.textContent = label;
        button.disabled = false;
        button.removeAttribute('data-wait-seconds');
      }
    }
    tick();
  });
})();
