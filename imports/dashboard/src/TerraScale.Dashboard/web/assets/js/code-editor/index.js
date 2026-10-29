// <ts-code-editor> upgrades the textarea it wraps into a Monaco editor. The
// textarea stays in the form as the submitted value (and as the control when
// JavaScript is unavailable); the editor and the textarea mirror each other.
import { copySavedQuery } from './saved-query.js';

document.addEventListener('click', event => {
  const button = event.target.closest?.('[command="show-modal"][commandfor="save-query"]');
  if (!button) return;
  const dialog = document.getElementById('save-query');
  const source = document.getElementById('sql');
  const target = dialog?.querySelector('#saved-sql');
  if (source && target) copySavedQuery(source, target, dialog);
}, { capture: true });

const fontSize = 13;
const lineHeight = 20;
const padding = 12;

// Monaco is downloaded and started in the background as soon as the page has
// finished loading, whether or not an editor is on screen yet.
const pageLoaded = new Promise(resolve => {
  if (document.readyState === 'complete') {
    resolve();
  } else {
    addEventListener('load', resolve, { once: true });
  }
});
const monaco = pageLoaded
  .then(() => Promise.all([import('./monaco.js'), document.fonts.load(`${fontSize}px "Geist Mono"`)]))
  .then(([module]) => module.load())
  .catch(error => {
    // The textareas stay usable as plain fields.
    console.warn('Code editor unavailable:', error);
    return null;
  });

// Every field gets its own model; ids and names may repeat across forms.
let models = 0;

class CodeEditor extends HTMLElement {
  async connectedCallback() {
    const textarea = this.querySelector(':scope > textarea');
    if (!textarea || this.editor) {
      return;
    }
    const api = await monaco;
    if (!api || !this.isConnected || this.editor) {
      return;
    }
    this.mount(api, textarea);
  }

  disconnectedCallback() {
    this.cleanup?.();
  }

  mount(api, textarea) {
    const rows = Number(textarea.getAttribute('rows')) || 8;
    const container = document.createElement('div');
    container.className = 'ts-code-editor-surface';
    this.style.height = `${rows * lineHeight + padding * 2 + 2}px`;
    this.append(container);

    // A Uri per model: modern-monaco's fallback model URI is a plain string,
    // which Monaco's marker lookups never match, so diagnostics would not show.
    const language = this.dataset.language || 'plaintext';
    const uri = api.Uri.file(`/${++models}/${textarea.name || 'field'}.${language}`);
    const model = api.editor.createModel(textarea.value, language, uri);
    const editor = api.editor.create(container, {
      model,
      theme: 'terrascale',
      // Tab indents code; announce the key that lets it move focus instead.
      ariaLabel: `${textarea.labels[0]?.textContent.trim() || textarea.name}. Press Ctrl+M to toggle Tab moving focus.`,
      readOnly: textarea.readOnly,
      fontFamily: getComputedStyle(this).getPropertyValue('--ts-font-mono').trim(),
      fontSize,
      lineHeight,
      fontLigatures: true,
      automaticLayout: true,
      fixedOverflowWidgets: true,
      padding: { top: padding, bottom: padding },
      tabSize: 2,
      wordWrap: language === 'sql' ? 'on' : 'off',
      lineNumbersMinChars: 3,
      lineDecorationsWidth: 8,
      glyphMargin: false,
      minimap: { enabled: false },
      stickyScroll: { enabled: false },
      scrollBeyondLastLine: false,
      overviewRulerLanes: 0,
      overviewRulerBorder: false,
      hideCursorInOverviewRuler: true,
      renderLineHighlightOnlyWhenFocus: true,
      cursorBlinking: 'smooth',
      cursorSmoothCaretAnimation: 'on',
      smoothScrolling: true,
      bracketPairColorization: { enabled: true },
      guides: { bracketPairs: 'active', indentation: true },
      scrollbar: { verticalScrollbarSize: 10, horizontalScrollbarSize: 10, useShadows: false },
    });

    // Editor → textarea keeps the form value current; textarea → editor picks
    // up anything else that writes the field (form reset, scripts, autofill).
    let syncing = false;
    const toTextarea = model.onDidChangeContent(() => {
      syncing = true;
      textarea.value = model.getValue();
      textarea.dispatchEvent(new Event('input', { bubbles: true }));
      syncing = false;
    });
    const toEditor = () => {
      if (!syncing && textarea.value !== model.getValue()) {
        model.setValue(textarea.value);
      }
    };
    const onReset = () => setTimeout(toEditor);
    // Labels and failed validation focus the editor rather than the hidden
    // textarea; the textarea keeps focus when something targets it directly.
    // Its :user-invalid state still marks the editor frame.
    const focusEditor = event => {
      event.preventDefault();
      editor.focus();
    };
    const labels = [...textarea.labels];
    textarea.addEventListener('input', toEditor);
    textarea.addEventListener('invalid', focusEditor);
    textarea.form?.addEventListener('reset', onReset);
    labels.forEach(label => label.addEventListener('click', focusEditor));
    textarea.tabIndex = -1;
    textarea.ariaHidden = 'true';

    // Ctrl/Cmd+Enter submits the surrounding form, like a query console. An
    // action is scoped to this editor; addCommand would bind every editor on
    // the page to the last form registered.
    const submit = textarea.readOnly ? null : editor.addAction({
      id: 'terrascale.submitForm',
      label: 'Submit form',
      keybindings: [api.KeyMod.CtrlCmd | api.KeyCode.Enter],
      run: () => textarea.form?.requestSubmit(),
    });

    // SQL editors in the SQL console complete and check against its catalog.
    const sqlHost = language === 'sql' ? this.closest('[data-sql-catalog]') : null;
    const sql = sqlHost && import('./sql.js')
      .then(module => module.attach(api, editor, textarea, sqlHost))
      .catch(error => console.warn('SQL completion unavailable:', error));

    this.editor = editor;
    this.dataset.ready = '';
    this.cleanup = () => {
      sql?.then(dispose => dispose?.());
      toTextarea.dispose();
      submit?.dispose();
      editor.dispose();
      model.dispose();
      textarea.removeEventListener('input', toEditor);
      textarea.removeEventListener('invalid', focusEditor);
      textarea.form?.removeEventListener('reset', onReset);
      labels.forEach(label => label.removeEventListener('click', focusEditor));
      textarea.removeAttribute('tabindex');
      textarea.removeAttribute('aria-hidden');
      container.remove();
      this.style.height = '';
      delete this.dataset.ready;
      this.editor = null;
      this.cleanup = null;
    };
  }
}

customElements.define('ts-code-editor', CodeEditor);
