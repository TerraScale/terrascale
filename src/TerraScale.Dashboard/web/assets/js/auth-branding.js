// Unsaved preview only. Persistent settings belong to ts-auth, never browser storage.
const form = document.getElementById('auth-branding-form');
if (form) {
  const root = document.getElementById('auth-brand-preview');
  const sheet = document.getElementById('auth-brand-preview-style').sheet;
  sheet.insertRule('#auth-brand-preview {}');
  const style = sheet.cssRules[0].style;
  const field = name => form.elements.namedItem(name);
  const hex = value => /^#[0-9a-f]{6}$/i.test(value) ? value : '';
  const https = value => {
    try { const url = new URL(value); return url.protocol === 'https:' && !url.username && !url.password ? url.href : ''; }
    catch { return ''; }
  };
  const snapshot = () => JSON.stringify([...new FormData(form)].filter(([name]) => name !== '_csrf'));
  const saved = [...new FormData(form)].filter(([name]) => name !== "_csrf").map(([name, value]) => [name, field(name).dataset.saved ?? value]);
  const original = JSON.stringify(saved);
  const preferred = matchMedia('(prefers-color-scheme: light)');
  let mode = '', page = 'sign-in';
  const render = () => {
    document.querySelector('[data-brand-size]').value = field('title_size').value;
    const name = field('name').value.trim() || form.dataset.projectName;
    const theme = mode || field('default_theme').value;
    const light = theme === 'light' || theme === 'system' && preferred.matches;
    root.dataset.theme = light ? 'light' : 'dark';
    const accent = hex(field('theme_color').value), background = hex(field('background_color').value), text = hex(field('text_color').value);
    const rgb = value => value.slice(1).match(/../g).map(c => parseInt(c, 16));
    const toHex = color => '#' + color.map(v => Math.round(v).toString(16).padStart(2, '0')).join('');
    const mix = (a, b, f) => a.map((v, i) => v + (b[i] - v) * f);
    const lum = color => color.map(v => { v /= 255; return v <= .03928 ? v / 12.92 : ((v + .055) / 1.055) ** 2.4; }).reduce((sum, v, i) => sum + v * [.2126, .7152, .0722][i], 0);
    const contrast = (a, b) => (Math.max(lum(a), lum(b)) + .05) / (Math.min(lum(a), lum(b)) + .05);
    const white = [255, 255, 255], black = [10, 10, 10], away = light ? black : white;
    const on = color => contrast(color, white) >= contrast(color, black) ? white : black;
    const push = (color, target, valid) => { for (let i = 0; i < 60 && !valid(color.map(Math.round)); i++) color = mix(color, target, .06); return color.map(Math.round); };
    const surfaces = (light ? ['#ffffff', '#fafafa', '#f4f4f5'] : ['#151515', '#1b1b1b', '#222222']).map(rgb);
    const custom = accent || background || text;
    const fill = custom ? push(rgb(accent || '#2158e8'), away, color => surfaces.slice(0, 2).every(surface => contrast(color, surface) >= 3) && contrast(color, on(color)) >= 4.5) : rgb('#2158e8');
    style.setProperty('--brand-accent', toHex(fill));
    style.setProperty('--brand-on-accent', toHex(on(fill)));
    style.setProperty('--brand-text', text ? toHex(push(rgb(text), away, color => surfaces.every(surface => contrast(color, surface) >= 4.5))) : light ? '#0a0a0a' : '#f2f2f2');
    if (background) {
      style.setProperty('--brand-background', background);
      style.setProperty('--brand-footer-text', toHex(push(text ? rgb(text) : on(rgb(background)), on(rgb(background)), color => contrast(color, rgb(background)) >= 4.5)));
    } else { style.removeProperty('--brand-background'); style.removeProperty('--brand-footer-text'); }
    style.setProperty('--brand-title-size', `${Math.min(40, Math.max(20, Number(field('title_size').value) || 24))}px`);
    root.querySelector('[data-brand-name]').textContent = name;
    root.querySelector('[data-brand-initial]').textContent = name.slice(0, 1).toUpperCase();
    const logo = root.querySelector('[data-brand-logo]'), src = https(field('logo_url').value) || logo.dataset.defaultSrc;
    logo.hidden = !src;
    root.querySelector('[data-brand-initial]').hidden = !!src;
    if (src) { if (logo.getAttribute('src') !== src) logo.src = src; } else logo.removeAttribute('src');
    root.querySelector('[data-brand-heading]').textContent = page === 'reset' ? 'Reset your password' : `${page === 'sign-up' ? 'Create your account on' : 'Sign in to'} ${name}`;
    root.querySelector('[data-brand-subtitle]').textContent = page === 'reset' ? 'Enter your account email and we will send you a reset code.' : page === 'sign-up' ? `Get started with ${name} in under a minute.` : 'Welcome back. Use the email you signed up with.';
    root.querySelectorAll('[data-brand-form]').forEach(element => { element.hidden = element.dataset.brandForm !== page; });
    const footer = root.querySelector('[data-brand-footer]');
    footer.replaceChildren();
    for (const line of field('footer_links').value.split('\n').filter(line => line.trim()).slice(0, 5)) {
      const separator = line.indexOf('|');
      if (separator < 0) continue;
      const label = line.slice(0, separator).trim(), url = https(line.slice(separator + 1).trim());
      if (!label || !url) continue;
      const link = document.createElement('a');
      link.textContent = label.slice(0, 24); link.href = url; link.target = '_blank'; link.rel = 'noopener noreferrer';
      const hint = document.createElement('span');
      hint.className = 'ts-sr-only'; hint.textContent = ' (opens in a new tab)';
      link.append(hint);
      footer.append(link);
    }
    document.querySelector('[data-brand-dirty]').hidden = snapshot() === original;
  };
  document.querySelector('[data-brand-size]').addEventListener('input', event => { field('title_size').value = event.target.value; render(); });
    // Controls use the form attribute to keep the toolbar beside the heading.
  document.addEventListener('input', event => { if (event.target.form === form) { document.querySelector('[data-brand-size]').value = field('title_size').value; render(); } });
  form.addEventListener('reset', event => { event.preventDefault(); saved.forEach(([name, value]) => { field(name).value = value; }); render(); });
  document.querySelector('[data-brand-defaults]')?.addEventListener('click', () => {
    saved.forEach(([name]) => { field(name).value = name === 'title_size' ? '24' : name === 'default_theme' ? 'system' : name === 'footer_links' ? 'Status | https://status.terrascale.tech\nDocs | https://docs.terrascale.tech\nPrivacy | https://terrascale.tech/privacy\nTerms | https://terrascale.tech/terms' : ''; });
    render();
  });
  preferred.addEventListener('change', render);
  for (const [attribute, update] of [['data-brand-page', value => { page = value; }], ['data-brand-mode', value => { mode = value; }]]) {
    document.querySelectorAll(`[${attribute}]`).forEach(button => button.addEventListener('click', () => {
      update(button.getAttribute(attribute));
      button.parentElement.querySelectorAll('button').forEach(item => item.setAttribute('aria-pressed', String(item === button)));
      render();
    }));
  }
  render();
}
