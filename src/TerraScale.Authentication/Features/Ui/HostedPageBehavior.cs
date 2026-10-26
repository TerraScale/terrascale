namespace TerraScale.Authentication.Features.Ui;

/// <summary>Progressive browser form controls. Credentials are submitted only in the existing same-origin forms.</summary>
internal static class HostedPageBehavior
{
    public const string Script = """
(function () {
  document.querySelectorAll('input[type="password"]').forEach(function (input) {
    var group = document.createElement('div'), toggle = document.createElement('button');
    group.className = 'password-control';
    input.before(group); group.append(input);
    toggle.type = 'button'; toggle.className = 'password-toggle';
    toggle.setAttribute('aria-label', 'Show password');
    toggle.setAttribute('aria-controls', input.id); toggle.setAttribute('aria-pressed', 'false');
    // Lucide eye (ISC), matching the approved v3 sprite.
    toggle.innerHTML = '<svg class="icon" viewBox="0 0 24 24" aria-hidden="true"><path d="M2.062 12.348a1 1 0 0 1 0-.696 10.75 10.75 0 0 1 19.876 0 1 1 0 0 1 0 .696 10.75 10.75 0 0 1-19.876 0"/><circle cx="12" cy="12" r="3"/></svg>';
    toggle.addEventListener('click', function () {
      var show = input.type === 'password'; input.type = show ? 'text' : 'password';
      toggle.setAttribute('aria-label', show ? 'Hide password' : 'Show password');
      toggle.setAttribute('aria-pressed', String(show));
    });
    group.append(toggle);
  });
  document.querySelectorAll('input.code[name="code"][pattern="[0-9]{6}"][maxlength="6"]').forEach(function (value) {
    var group = document.createElement('div'), digits = [], label = document.querySelector('label[for="' + value.id + '"]');
    group.className = 'otp'; group.setAttribute('role', 'group');
    group.setAttribute('aria-label', label ? label.textContent : 'Verification code');
    value.type = 'hidden'; value.after(group);
    function sync() { value.value = digits.map(function (d) { return d.value; }).join(''); }
    function fill(index, text) {
      var chars = text.replace(/[^0-9]/g, '').slice(0, 6 - index);
      for (var n = 0; n < chars.length; n++) digits[index + n].value = chars[n];
      sync(); digits[Math.min(index + chars.length, 5)].focus();
    }
    for (var i = 0; i < 6; i++) {
      (function (index) {
        var digit = document.createElement('input');
        digit.type = 'text'; digit.inputMode = 'numeric'; digit.pattern = '[0-9]'; digit.required = true;
        digit.maxLength = 6; digit.placeholder = '·'; digit.autocomplete = index === 0 ? 'one-time-code' : 'off';
        digit.setAttribute('aria-label', 'Digit ' + (index + 1));
        if (index === 0) { digit.id = value.id + '-digit'; if (label) label.htmlFor = digit.id; }
        digit.addEventListener('input', function () {
          if (digit.value.length > 1) fill(index, digit.value);
          else { digit.value = digit.value.replace(/[^0-9]/g, ''); sync(); if (digit.value && index < 5) digits[index + 1].focus(); }
        });
        digit.addEventListener('paste', function (event) {
          var text = event.clipboardData.getData('text');
          if (/[0-9]/.test(text)) { event.preventDefault(); fill(index, text); }
        });
        digit.addEventListener('keydown', function (event) {
          if ((event.key === 'Backspace' && !digit.value || event.key === 'ArrowLeft') && index > 0) {
            event.preventDefault(); digits[index - 1].focus();
          } else if (event.key === 'ArrowRight' && index < 5) { event.preventDefault(); digits[index + 1].focus(); }
        });
        digit.addEventListener('focus', function () { digit.select(); });
        digits.push(digit); group.append(digit);
      })(i);
    }
    if (value.autofocus) digits[0].focus();
  });
})();
""";
}
