// Delegation also covers snippets inserted by dashboard SSE or docs navigation.
// Frameworks own their selection; plain server-rendered groups own their panels here.
export function installSdkTabs() {
  document.addEventListener('keydown', event => {
    const tab = event.target.closest?.('.sdk-tabs > [role="tab"]');
    if (!tab || event.altKey || event.ctrlKey || event.metaKey) return;
    const tabs = [...tab.parentElement.querySelectorAll('[role="tab"]')];
    const index = tabs.indexOf(tab);
    const rtl = getComputedStyle(tab.parentElement).direction === 'rtl';
    let next;
    if (event.key === 'Home') next = 0;
    else if (event.key === 'End') next = tabs.length - 1;
    else if (event.key === 'ArrowRight') next = (index + (rtl ? -1 : 1) + tabs.length) % tabs.length;
    else if (event.key === 'ArrowLeft') next = (index + (rtl ? 1 : -1) + tabs.length) % tabs.length;
    else return;
    event.preventDefault();
    tabs[next].focus();
    tabs[next].click();
    tabs[next].scrollIntoView({ block: 'nearest', inline: 'nearest' });
  });

  document.addEventListener('click', event => {
    const tab = event.target.closest?.('[data-sdk-tabs] > [role="tab"]');
    if (!tab) return;
    for (const sibling of tab.parentElement.querySelectorAll('[role="tab"]')) {
      const selected = sibling === tab;
      sibling.setAttribute('aria-selected', String(selected));
      sibling.tabIndex = selected ? 0 : -1;
      const panel = document.getElementById(sibling.getAttribute('aria-controls'));
      if (panel) panel.hidden = !selected;
    }
  });
}
