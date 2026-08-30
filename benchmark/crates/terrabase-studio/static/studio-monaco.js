/* TerraBase Studio's Monaco JSON editor integration. Datastar owns actions,
 * signals, and server-sent event handling through the vendored official
 * runtime. This file only mounts and cleans up Monaco editors. */
(function () {
  'use strict';

  var MONACO_ROOT = '/static/monaco';
  var monacoPromise = null;
  var mounted = new Map();

  function loadMonaco() {
    if (monacoPromise) return monacoPromise;
    monacoPromise = new Promise(function (resolve, reject) {
      if (window.monaco && window.monaco.editor) {
        resolve(window.monaco);
        return;
      }

      var finished = false;
      var timeout = window.setTimeout(function () {
        if (finished) return;
        finished = true;
        reject(new Error('Monaco loader timed out'));
      }, 10000);
      var loader = document.createElement('script');
      loader.src = MONACO_ROOT + '/vs/loader.js';
      loader.onload = function () {
        if (finished) return;
        window.MonacoEnvironment = {
          getWorkerUrl: function () {
            var source = "self.MonacoEnvironment={baseUrl:'" + MONACO_ROOT + "/'};" +
              "importScripts('" + MONACO_ROOT + "/vs/base/worker/workerMain.js');";
            return 'data:text/javascript;charset=utf-8,' + encodeURIComponent(source);
          }
        };
        window.require.config({ paths: { vs: MONACO_ROOT + '/vs' } });
        window.require(['vs/editor/editor.main'], function () {
          if (finished) return;
          finished = true;
          window.clearTimeout(timeout);
          configureMonaco(window.monaco);
          resolve(window.monaco);
        }, function (error) {
          if (finished) return;
          finished = true;
          window.clearTimeout(timeout);
          reject(error);
        });
      };
      loader.onerror = function () {
        if (finished) return;
        finished = true;
        window.clearTimeout(timeout);
        reject(new Error('Monaco loader failed'));
      };
      document.head.appendChild(loader);
    });
    return monacoPromise;
  }

  function configureMonaco(monaco) {
    monaco.editor.defineTheme('terrabase-midnight', {
      base: 'vs-dark',
      inherit: true,
      rules: [
        { token: 'string.key.json', foreground: '7DD3FC' },
        { token: 'string.value.json', foreground: 'A7F3D0' },
        { token: 'number', foreground: 'FDE68A' },
        { token: 'keyword', foreground: 'C4B5FD' }
      ],
      colors: {
        'editor.background': '#0B1020',
        'editor.foreground': '#D8DEE9',
        'editorLineNumber.foreground': '#526078',
        'editorLineNumber.activeForeground': '#93C5FD',
        'editorCursor.foreground': '#67E8F9',
        'editor.selectionBackground': '#1D4ED866',
        'editor.inactiveSelectionBackground': '#1E3A5F66',
        'editorIndentGuide.background1': '#243047',
        'editorIndentGuide.activeBackground1': '#4B6387'
      }
    });
    monaco.languages.json.jsonDefaults.setDiagnosticsOptions({
      validate: true,
      allowComments: false,
      trailingCommas: 'error',
      enableSchemaRequest: false,
      schemas: []
    });
  }

  function editorNodes(root) {
    var editors = [];
    if (root.nodeType !== Node.ELEMENT_NODE && root.nodeType !== Node.DOCUMENT_NODE) {
      return editors;
    }
    if (root.matches && root.matches('[data-monaco-json]:not([data-monaco-ready])')) {
      editors.push(root);
    }
    root.querySelectorAll('[data-monaco-json]:not([data-monaco-ready])').forEach(function (node) {
      editors.push(node);
    });
    return editors;
  }

  function setupJsonEditors(root) {
    var editors = editorNodes(root);
    if (!editors.length) return;
    editors.forEach(function (container) {
      container.setAttribute('data-monaco-ready', 'loading');
    });
    loadMonaco().then(function (monaco) {
      editors.forEach(function (container) {
        mountJsonEditor(monaco, container);
      });
    }).catch(function (error) {
      editors.forEach(function (container) {
        if (!container.isConnected) return;
        container.setAttribute('data-monaco-ready', 'failed');
        var status = container.parentElement.querySelector('[data-monaco-status]');
        if (status) {
          status.textContent = 'Monaco could not load. The plain JSON editor remains available.';
        }
      });
      console.error('studio: Monaco failed to load', error);
    });
  }

  function mountJsonEditor(monaco, container) {
    if (!container.isConnected || container.getAttribute('data-monaco-ready') === 'mounted') return;
    var parent = container.parentElement;
    var field = parent.querySelector('[data-monaco-fallback]');
    var status = parent.querySelector('[data-monaco-status]');
    if (!field) {
      container.setAttribute('data-monaco-ready', 'failed');
      return;
    }

    var model = monaco.editor.createModel(field.value, 'json');
    var editor = monaco.editor.create(container, {
      model: model,
      theme: 'terrabase-midnight',
      automaticLayout: true,
      minimap: { enabled: false },
      fontFamily: "'JetBrains Mono', 'SFMono-Regular', Consolas, monospace",
      fontLigatures: true,
      fontSize: 14,
      lineHeight: 22,
      formatOnPaste: true,
      formatOnType: true,
      folding: true,
      bracketPairColorization: { enabled: true },
      guides: { bracketPairs: true, indentation: true },
      scrollBeyondLastLine: false,
      smoothScrolling: true,
      padding: { top: 14, bottom: 14 }
    });

    container.classList.remove('hidden');
    field.classList.add('hidden');
    container.setAttribute('data-monaco-ready', 'mounted');

    var contentSubscription = model.onDidChangeContent(function () {
      field.value = model.getValue();
      field.dispatchEvent(new Event('input', { bubbles: true }));
    });
    var markerSubscription = monaco.editor.onDidChangeMarkers(function (resources) {
      if (!resources.some(function (resource) {
        return resource.toString() === model.uri.toString();
      })) return;
      var markers = monaco.editor.getModelMarkers({ resource: model.uri });
      if (!status) return;
      if (markers.length) {
        status.textContent = markers.length + ' JSON problem' +
          (markers.length === 1 ? '' : 's') + '. Fix them before saving.';
        status.classList.add('text-destructive');
      } else {
        status.textContent = 'Valid JSON. Formatting and lint checks are active.';
        status.classList.remove('text-destructive');
      }
    });
    editor.addAction({
      id: 'terrabase-format-json',
      label: 'Format JSON',
      keybindings: [monaco.KeyMod.Shift | monaco.KeyMod.Alt | monaco.KeyCode.KeyF],
      run: function () {
        var action = editor.getAction('editor.action.formatDocument');
        return action ? action.run() : undefined;
      }
    });
    mounted.set(container, {
      editor: editor,
      model: model,
      contentSubscription: contentSubscription,
      markerSubscription: markerSubscription
    });
  }

  function disposeRemovedEditors() {
    mounted.forEach(function (resources, container) {
      if (container.isConnected) return;
      resources.markerSubscription.dispose();
      resources.contentSubscription.dispose();
      resources.editor.dispose();
      resources.model.dispose();
      mounted.delete(container);
    });
  }

  function start() {
    setupJsonEditors(document);
    new MutationObserver(function (mutations) {
      mutations.forEach(function (mutation) {
        mutation.addedNodes.forEach(setupJsonEditors);
      });
      disposeRemovedEditors();
    }).observe(document.documentElement, { childList: true, subtree: true });
  }

  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', start, { once: true });
  } else {
    start();
  }
})();
