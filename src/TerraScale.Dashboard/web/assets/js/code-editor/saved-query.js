export function copySavedQuery(source, target, dialog) {
  // Copy at activation, never while the user is editing the modal draft.
  if (dialog.dataset.sourceSql !== source.value) {
    target.value = source.value;
    dialog.dataset.sourceSql = source.value;
    target.dispatchEvent(new Event('input', { bubbles: true }));
  }
}
