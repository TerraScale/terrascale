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
        if (!assertion) throw new Error('The passkey was not created.');
        return fetch(form.dataset.verify, {
          method: 'POST',
          headers: { 'Content-Type': 'application/json', 'Accept': 'application/json' },
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
      .then(function () { window.location.reload(); })
      .catch(function (error) { fail(button, error.message || 'The passkey was not added.'); });
  });
})();
(function () {
  var button = document.getElementById('passkey-sign-in');
  if (!button || !window.PublicKeyCredential) return;
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
    alert.innerHTML = '<strong>' + message + '</strong><p>' + body + '</p>';
  }
  function json(response) {
    return response.json().then(function (body) {
      if (!response.ok) throw new Error('The passkey sign-in failed.');
      return body;
    });
  }
  function follow(html) {
    var page = new DOMParser().parseFromString(html, 'text/html');
    var refresh = page.querySelector('meta[http-equiv="refresh"]');
    var url = refresh && refresh.getAttribute('content').match(/^0;\s*url=(.+)$/);
    if (url) { window.location.href = url[1]; return; }
    var link = page.querySelector('a.btn');
    if (link) { window.location.href = link.href; return; }
    window.location.reload();
  }
  button.addEventListener('click', function () {
    var address = email ? email.value.trim() : '';
    if (!address) {
      if (email) email.focus();
      fail('Enter your email address', 'Enter your email address to sign in with a passkey.');
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
        if (!assertion) throw new Error('The passkey sign-in failed.');
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
        if (!response.ok) throw new Error('The passkey sign-in failed.');
        return response.text();
      })
      .then(follow)
      .catch(function (error) {
        if (error && error.name === 'NotAllowedError') { reset(); return; }
        fail('Passkey sign-in failed', 'The passkey sign-in failed; start again.');
      });
  });
})();
(function () {
  document.querySelectorAll('button[data-wait-seconds]').forEach(function (button) {
    var remaining = parseInt(button.getAttribute('data-wait-seconds'), 10);
    var label = button.getAttribute('data-label') || button.textContent;
    function tick() {
      if (remaining > 0) {
        button.textContent = label + ' in ' + remaining + 's';
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
