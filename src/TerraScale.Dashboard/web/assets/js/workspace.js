// Each document carries its own workspace through navigation, forms and SSE.
// A different tab can change the account's opening preference without changing this document.
const organizationId = document.documentElement.dataset.workspaceOrganization;
const projectId = document.documentElement.dataset.workspaceProject;
const selection = {organizationId, projectId};

function scoped(value) {
  const url = new URL(value, location.href);
  // Invitation tokens carry their own authority, independent of the sender's workspace.
  if (url.origin !== location.origin || /^\/(login|logout|invitations|js|css|fonts|brand|flags|img)(\/|$)/.test(url.pathname)) return url;
  const targetOrganization = url.searchParams.get('organizationId') ?? url.searchParams.get('organizationID');
  if (targetOrganization && targetOrganization !== organizationId) return url;
  if (!targetOrganization && url.searchParams.has('projectId') && url.searchParams.get('projectId') !== projectId) return url;
  for (const [key, id] of Object.entries(selection)) {
    if (id && !url.searchParams.has(key)) url.searchParams.set(key, id);
  }
  return url;
}

if (organizationId) {
  const current = new URL(location.href);
  current.searchParams.delete('organizationID');
  for (const [key, id] of Object.entries(selection)) {
    current.searchParams.delete(key);
    if (id) current.searchParams.set(key, id);
  }
  history.replaceState(history.state, '', current);
  const update = root => {
    const elements = root.querySelectorAll?.('a[href], form') ?? [];
    for (const element of elements) {
      if (element instanceof HTMLAnchorElement) {
        if (element.getAttribute('href').startsWith('#')) continue;
        const url = scoped(element.href).href;
        if (element.href !== url) {
          const target = new URL(url);
          element.setAttribute('href', target.origin === location.origin ? target.pathname + target.search + target.hash : target.href);
        }
      } else {
        // A control named "action" shadows the form's action property.
        const action = element.getAttribute('action') || location.href;
        const url = scoped(action);
        if (url.origin !== location.origin) continue;
        if (new URL(action, location.href).href !== url.href) element.setAttribute('action', url.pathname + url.search + url.hash);
        // Browsers replace a GET form action's query with its successful controls.
        if (element.method === 'get') {
          for (const key of Object.keys(selection)) {
            if (!url.searchParams.has(key) || element.elements.namedItem(key)) continue;
            const input = document.createElement('input');
            input.type = 'hidden'; input.name = key; input.value = url.searchParams.get(key);
            element.append(input);
          }
        }
      }
    }
  };
  update(document);
  new MutationObserver(() => update(document)).observe(document.body, {childList: true, subtree: true, attributes: true, attributeFilter: ['href', 'action']});

  // Datastar and the editor use fetch directly; scope those requests as well as native forms.
  const fetch = window.fetch.bind(window);
  window.fetch = (input, options) => {
    const url = scoped(input instanceof Request ? input.url : input);
    return fetch(input instanceof Request ? new Request(url, input) : url, options);
  };
}
