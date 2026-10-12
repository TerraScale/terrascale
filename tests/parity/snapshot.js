() => {
  const visible = el => el.getClientRects().length && getComputedStyle(el).visibility !== 'hidden';
  const clean = text => text.replace(/\s+/g, ' ').trim();
  const local = value => {
    if (!value) return '';
    const url = new URL(value, location.href);
    return url.origin === location.origin ? url.pathname + url.search : value;
  };
  const box = el => {
    const rect = el.getBoundingClientRect();
    return [rect.x, rect.y, rect.width, rect.height].map(value => Math.round(value * 100) / 100);
  };
  const main = document.querySelector('main');
  return {
    title: document.title,
    text: clean(main?.innerText ?? document.body.innerText),
    headings: [...document.querySelectorAll('h1,h2,h3')].filter(visible).map(el => clean(el.textContent)),
    links: [...document.querySelectorAll('main a')].filter(visible).map(el => ({ text: clean(el.textContent), href: local(el.getAttribute('href')) })),
    forms: [...document.querySelectorAll('main form')].map(el => ({
      action: local(el.getAttribute('action')), method: el.method,
      controls: [...el.querySelectorAll('input,select,textarea,button')].filter(control => control.type !== 'hidden').map(control => ({
        id: control.id, name: control.name, type: control.type, text: control.tagName === 'BUTTON' ? clean(control.textContent) : '',
        placeholder: control.getAttribute('placeholder'), autocomplete: control.getAttribute('autocomplete'),
        required: control.required ?? false, disabled: control.disabled ?? false, readOnly: control.readOnly ?? false,
        minLength: control.getAttribute('minlength'), maxLength: control.getAttribute('maxlength'),
        min: control.getAttribute('min'), max: control.getAttribute('max'), step: control.getAttribute('step'),
        pattern: control.getAttribute('pattern'), invalid: control.getAttribute('aria-invalid'), describedBy: control.getAttribute('aria-describedby'),
        autofocus: control.autofocus ?? false
      }))
    })),
    geometry: [...document.querySelectorAll('.ts-login-panel,.ts-login-logo,.ts-auth-heading,main form,.ts-auth-footer,.ts-alert,.ts-field-error,.ts-email-code')].filter(visible).map(el => ({ selector: el.className, box: box(el) })),
    mapAnimations: [...document.querySelectorAll('.ts-region-node-ring,.ts-region-arc')].map(el => {
      const style = getComputedStyle(el);
      return { region: el.closest('[data-region]')?.getAttribute('data-region') ?? '',
        opacity: style.opacity, transform: style.transform, strokeDashoffset: style.strokeDashoffset,
        animations: el.getAnimations().map(animation => ({ name: animation.animationName ?? '',
          playState: animation.playState, pending: animation.pending, currentTime: animation.currentTime })) };
    }),
    overflow: document.documentElement.scrollWidth > innerWidth,
    focus: document.activeElement?.id ?? ''
  };
}
