document.addEventListener('datastar-fetch', event => {
  if (event.detail.type !== 'datastar-patch-elements' || !document.querySelector('dialog:modal')) return;
  const {mode = 'outer', selector, elements = ''} = event.detail.argsRaw;
  if (!['outer', 'inner', 'replace', 'remove'].includes(mode)) return;
  const incoming = new DOMParser().parseFromString(elements, 'text/html');
  const targets = selector
    ? document.querySelectorAll(selector)
    : [...incoming.body.children].map(element => document.getElementById(element.id)).filter(Boolean);
  for (const target of targets) {
    const dialogs = [...target.querySelectorAll('dialog:modal')];
    if (mode !== 'inner' && target.matches('dialog:modal')) dialogs.push(target);
    for (const dialog of dialogs) {
      const replacement = incoming.getElementById(dialog.id);
      if (mode === 'replace' || mode === 'remove' || (!replacement?.hasAttribute('open') && !replacement?.getAttribute('data-preserve-attr')?.split(' ').includes('open'))) dialog.close();
    }
  }
}, true);
