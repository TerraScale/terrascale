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
    console.warn('Code editor unavailable:', error);
    return null;
  });

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

    const language = this.dataset.language || 'plaintext';
    const extension = language === 'javascript' ? 'js' : language;
    const uri = api.Uri.file(`/${++models}/${textarea.name || 'field'}.${extension}`);
    const model = api.editor.createModel(textarea.value, language, uri);
    const editor = api.editor.create(container, {
      model,
      theme: 'terrascale',
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

    const submit = textarea.readOnly || this.dataset.submitShortcut === 'false' ? null : editor.addAction({
      id: 'terrascale.submitForm',
      label: 'Submit form',
      keybindings: [api.KeyMod.CtrlCmd | api.KeyCode.Enter],
      run: () => textarea.form?.requestSubmit(),
    });

    const sqlHost = language === 'sql' ? this.closest('[data-sql-catalog]') : null;
    const sql = sqlHost && import('./sql.js')
      .then(module => module.attach(api, editor, textarea, sqlHost))
      .catch(error => console.warn('SQL completion unavailable:', error));
    const js = language === 'javascript' ? import('./function-js.js')
      .then(module => module.attach(api, editor))
      .catch(error => console.warn('JavaScript completion unavailable:', error)) : null;

    this.editor = editor;
    const extras = [sql, js].filter(Boolean);
    const markReady = () => {
      if (this.editor === editor) {
        this.dataset.ready = '';
      }
    };
    if (extras.length === 0) {
      markReady();
    } else {
      Promise.all(extras).then(markReady);
    }
    this.cleanup = () => {
      sql?.then(dispose => dispose?.());
      js?.then(dispose => dispose?.());
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
