/*
 * TerraBase Studio — a hand-written, read-only-subset implementation of the
 * Datastar protocol (https://data-star.dev).
 *
 * Why this exists instead of a vendored datastar.js: the repository is
 * Rust-only and the studio brief asks for the Datastar *protocol semantics*
 * implemented by hand — a tiny SSE emitter on the server and a small runtime
 * in the browser. This file is that runtime. It is deliberately small and
 * auditable.
 *
 * What it implements (the subset the studio pages use):
 *   data-signals                 declare/merge reactive signals
 *   data-on-<event>="<expr>"     evaluate an expression on a DOM event
 *   data-text="<expr>"           bind an element's text content
 *   data-show="<expr>"           toggle display by truthiness
 *   data-attr-<name>="<expr>"    bind one attribute
 *
 * Expressions are JavaScript evaluated with the signal store in scope, so
 * `data-on-click="page = page + 1"` writes the `page` signal, exactly as
 * Datastar's expression grammar does. Actions available in every expression:
 *   $$get(url)                   fetch SSE and apply its events
 *   $$post(url, body?)           POST (urlencoded object) then apply SSE
 *   $$submit(url)                POST the enclosing form, then apply SSE
 *   $$set(name, value)           set one signal
 *
 * SSE events applied (current Datastar wire names):
 *   datastar-patch-elements data: selector / mode / elements
 *   datastar-patch-signals  data: signals / onlyIfMissing
 *   (the old merge names remain accepted for compatibility with saved
 *    fixtures)
 *
 * Merge modes: outer (default), inner, replace, prepend, append, before,
 * after, remove.
 *
 * NOTE: this file must stay in sloppy mode — expression evaluation uses
 * `with (signals)`, which strict mode forbids. There is no "use strict" here
 * on purpose, and the file is loaded as a classic script, never a module.
 */

var TERRABASE_STUDIO = (function () {
  var SIGNALS = {};
  var dirty = false;
  var boundElements = [];
  var MONACO_ROOT = '/static/monaco';
  var monacoPromise = null;

  /* ---------------------------------------------------------------- signals */

  function setSignals(source, onlyIfMissing) {
    Object.keys(source).forEach(function (key) {
      if (onlyIfMissing && key in SIGNALS) return;
      SIGNALS[key] = source[key];
    });
    scheduleFlush();
  }

  function flush() {
    dirty = false;
    boundElements.forEach(function (entry) {
      if (!entry.el.isConnected) return;
      applyBinding(entry);
    });
  }

  function scheduleFlush() {
    if (dirty) return;
    dirty = true;
    Promise.resolve().then(flush);
  }

  /* ------------------------------------------------------------ expressions */

  var ACTIONS = {
    $$get: function (url) {
      return request('GET', url, null);
    },
    $$post: function (url, body) {
      return request('POST', url, body == null ? null : formEncode(body));
    },
    $$submit: function (url) {
      var form = currentForm;
      var body = form ? new URLSearchParams(new FormData(form)).toString() : null;
      return request('POST', url, body);
    },
    $$set: function (name, value) {
      SIGNALS[name] = value;
      scheduleFlush();
      return value;
    }
  };

  var currentEvent = null;
  var currentForm = null;

  function evaluate(expression, el, event) {
    var fn;
    try {
      // `with` puts every signal name in bare-identifier scope, so both reads
      // and assignments reach the store.
      fn = new Function('__signals', 'event', 'el', 'with (__signals) { return (function () {' +
        expression + '\n})() }');
    } catch (error) {
      console.error('studio: cannot parse expression', expression, error);
      return undefined;
    }
    var scope = Object.create(SIGNALS);
    Object.keys(ACTIONS).forEach(function (name) { scope[name] = ACTIONS[name]; });
    try {
      return fn.call(el, scope, event, el);
    } catch (error) {
      console.error('studio: expression failed', expression, error);
      return undefined;
    }
  }

  /* ------------------------------------------------------------ HTTP + SSE */

  function request(method, url, body) {
    var headers = { 'datastar-request': 'true' };
    if (body != null) headers['Content-Type'] = 'application/x-www-form-urlencoded';
    return fetch(url, {
      method: method,
      headers: headers,
      body: body == null && method === 'GET' ? undefined : body,
      redirect: 'follow'
    }).then(function (response) {
      var type = response.headers.get('content-type') || '';
      if (type.indexOf('text/event-stream') === -1) {
        console.error('studio: expected text/event-stream, got', type, url);
        return response.text().then(function (text) { console.error(text); });
      }
      return streamEvents(response);
    }).catch(function (error) {
      console.error('studio: request failed', url, error);
    });
  }

  function streamEvents(response) {
    var reader = response.body.getReader();
    var decoder = new TextDecoder();
    var buffer = '';
    function step(chunk) {
      if (chunk.done) return;
      buffer += decoder.decode(chunk.value, { stream: true });
      var frame;
      while ((frame = splitFrame()) !== null) applyEvent(parseFrame(frame));
      return reader.read().then(step);
    }
    return reader.read().then(step);
    function splitFrame() {
      var index = buffer.indexOf('\n\n');
      if (index === -1) return null;
      var frame = buffer.slice(0, index);
      buffer = buffer.slice(index + 2);
      return frame;
    }
  }

  function parseFrame(frame, eventName) {
    var event = eventName || 'message';
    var lines = [];
    frame.split('\n').forEach(function (line) {
      if (line.indexOf('event:') === 0) event = line.slice(6).trim();
      else if (line.indexOf('data:') === 0) lines.push(line.slice(5).replace(/^ /, ''));
    });
    var fields = {};
    lines.forEach(function (line) {
      var space = line.indexOf(' ');
      var key = space === -1 ? line : line.slice(0, space);
      var value = space === -1 ? '' : line.slice(space + 1);
      // Datastar uses repeated data lines for multiline HTML/JSON. SSE
      // clients expose those lines as one message, so preserve their newline
      // separators instead of silently retaining only the final line.
      if (Object.prototype.hasOwnProperty.call(fields, key)) fields[key] += '\n' + value;
      else fields[key] = value;
    });
    return { type: event, fields: fields };
  }

  // EventSource exposes only the concatenated data payload (the `data:`
  // prefixes have already been consumed), while fetch-based actions expose
  // the complete SSE frame. Reconstruct the data fields before using the
  // common parser so both transports have identical semantics.
  function parseEventData(eventName, data) {
    return parseFrame(data.split('\n').map(function (line) {
      return 'data: ' + line;
    }).join('\n'), eventName);
  }

  function applyEvent(event) {
    var f = event.fields;
    if (event.type === 'datastar-merge-fragments' || event.type === 'datastar-patch-elements') {
      mergeFragments({
        selector: f.selector || f['selector '],
        mode: f.mode || 'outer',
        html: f.elements != null ? f.elements : (f.fragments != null ? f.fragments : '')
      });
    } else if (event.type === 'datastar-remove-fragments') {
      eachSelector(f.selector, function (el) { el.remove(); });
      scan(document);
    } else if (event.type === 'datastar-merge-signals' || event.type === 'datastar-patch-signals') {
      setSignals(parseSignals(f.signals || '{}'), f.onlyIfMissing === 'true');
    } else if (event.type === 'datastar-execute-script') {
      runScript(f.script || f.src || '');
    }
  }

  function parseSignals(text) {
    try {
      return new Function('return (' + text + ')')();
    } catch (error) {
      console.error('studio: bad signals payload', text, error);
      return {};
    }
  }

  function runScript(source) {
    var script = document.createElement('script');
    script.text = source;
    document.head.appendChild(script).remove();
  }

  /* ------------------------------------------------------------- fragments */

  function parseFragment(html) {
    var template = document.createElement('template');
    template.innerHTML = html;
    return template.content;
  }

  function eachSelector(selector, visit) {
    if (!selector) return;
    document.querySelectorAll(selector).forEach(visit);
  }

  function mergeFragments(options) {
    var content = parseFragment(options.html == null ? '' : options.html);
    var roots = Array.prototype.slice.call(content.children);
    var mode = options.mode;
    if (mode === 'remove') {
      eachSelector(options.selector, function (el) { el.remove(); });
      scan(document);
      return;
    }
    if (mode === 'inner') {
      eachSelector(options.selector, function (target) {
        target.innerHTML = '';
        roots.forEach(function (root) { target.appendChild(cloneInto(root)); });
      });
      scan(document);
      return;
    }
    if (mode === 'prepend' || mode === 'append' || mode === 'before' || mode === 'after') {
      eachSelector(options.selector, function (target) {
        var nodes = roots.map(cloneInto);
        var anchor = mode === 'prepend' ? target.firstChild
          : mode === 'append' ? null
            : mode === 'before' ? target : target.nextSibling;
        if (anchor == null) target.parentNode.appendChild.apply(target.parentNode, nodes);
        else target.parentNode.insertBefore.apply(anchor.parentNode, [nodes[0], anchor].concat(nodes.slice(1)).filter(Boolean));
      });
      scan(document);
      return;
    }
    // 'outer' (the default) and 'replace': Datastar morphs by top-level ID.
    if (options.selector) {
      eachSelector(options.selector, function (target) {
        if (roots.length) target.replaceWith.apply(target, roots.map(cloneInto));
        else target.remove();
      });
    } else {
      roots.forEach(function (root) {
        var existing = root.id ? document.getElementById(root.id) : null;
        if (existing) existing.replaceWith(cloneInto(root));
        else document.body.appendChild(cloneInto(root));
      });
    }
    scan(document);
  }

  // Fragments arrive as inert template content; cloning re-activates them.
  function cloneInto(node) {
    if (node.tagName === 'SCRIPT') {
      var script = document.createElement('script');
      if (node.src) script.src = node.src;
      script.text = node.text;
      return script;
    }
    return document.importNode(node, true);
  }

  /* ------------------------------------------------------- attribute wiring */

  var ATTRIBUTE_PREFIXES = ['data-signals', 'data-on-', 'data-text', 'data-show', 'data-attr-'];

  function looksManaged(name) {
    return ATTRIBUTE_PREFIXES.some(function (prefix) { return name.lastIndexOf(prefix, 0) === 0; });
  }

  function scan(root) {
    boundElements = boundElements.filter(function (entry) { return entry.el.isConnected; });
    var nodes = [root];
    if (root.querySelectorAll) {
      root.querySelectorAll('*').forEach(function (node) { nodes.push(node); });
    }
    nodes.forEach(register);
    flush();
    setupJsonEditors(root);
  }

  /* ---------------------------------------------------------- JSON editor */

  function loadMonaco() {
    if (monacoPromise) return monacoPromise;
    monacoPromise = new Promise(function (resolve, reject) {
      if (window.monaco && window.monaco.editor) {
        resolve(window.monaco);
        return;
      }
      var loader = document.createElement('script');
      loader.src = MONACO_ROOT + '/vs/loader.js';
      loader.onload = function () {
        window.MonacoEnvironment = {
          getWorkerUrl: function () {
            var source = "self.MonacoEnvironment={baseUrl:'" + MONACO_ROOT + "/'};" +
              "importScripts('" + MONACO_ROOT + "/vs/base/worker/workerMain.js');";
            return 'data:text/javascript;charset=utf-8,' + encodeURIComponent(source);
          }
        };
        window.require.config({ paths: { vs: MONACO_ROOT + '/vs' } });
        window.require(['vs/editor/editor.main'], function () {
          window.monaco.editor.defineTheme('terrabase-midnight', {
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
          window.monaco.languages.json.jsonDefaults.setDiagnosticsOptions({
            validate: true,
            allowComments: false,
            trailingCommas: 'error',
            enableSchemaRequest: false,
            schemas: []
          });
          resolve(window.monaco);
        }, reject);
      };
      loader.onerror = function () { reject(new Error('Monaco loader failed')); };
      document.head.appendChild(loader);
    });
    return monacoPromise;
  }

  function setupJsonEditors(root) {
    if (!root.querySelectorAll) return;
    var editors = [];
    if (root.matches && root.matches('[data-monaco-json]')) editors.push(root);
    root.querySelectorAll('[data-monaco-json]:not([data-monaco-ready])').forEach(function (node) {
      editors.push(node);
    });
    if (!editors.length) return;
    editors.forEach(function (container) { container.setAttribute('data-monaco-ready', 'loading'); });
    loadMonaco().then(function (monaco) {
      editors.forEach(function (container) { mountJsonEditor(monaco, container); });
    }).catch(function (error) {
      editors.forEach(function (container) {
        container.setAttribute('data-monaco-ready', 'failed');
        var status = container.parentElement.querySelector('[data-monaco-status]');
        if (status) status.textContent = 'Monaco could not load. The plain JSON editor remains available.';
      });
      console.error('studio: Monaco failed to load', error);
    });
  }

  function mountJsonEditor(monaco, container) {
    if (!container.isConnected || container.getAttribute('data-monaco-ready') === 'mounted') return;
    var field = container.parentElement.querySelector('[data-monaco-fallback]');
    var submitted = container.parentElement.querySelector('[data-monaco-value]');
    var status = container.parentElement.querySelector('[data-monaco-status]');
    if (!field || !submitted) return;
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
    field.removeAttribute('name');
    submitted.disabled = false;
    container.setAttribute('data-monaco-ready', 'mounted');
    submitted.value = model.getValue();
    model.onDidChangeContent(function () { submitted.value = model.getValue(); });
    monaco.editor.onDidChangeMarkers(function (resources) {
      if (!resources.some(function (resource) { return resource.toString() === model.uri.toString(); })) return;
      var markers = monaco.editor.getModelMarkers({ resource: model.uri });
      if (!status) return;
      if (markers.length) {
        status.textContent = markers.length + ' JSON problem' + (markers.length === 1 ? '' : 's') + '. Fix them before saving.';
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
      run: function () { return editor.getAction('editor.action.formatDocument').run(); }
    });
  }

  function register(el) {
    if (!el.attributes) return;
    var i;
    for (i = 0; i < el.attributes.length; i += 1) {
      var attribute = el.attributes[i];
      if (attribute.name.lastIndexOf('data-on-', 0) !== 0) continue;
      bindEvent(el, attribute.name.slice('data-on-'.length), attribute.value);
    }
    for (i = 0; i < el.attributes.length; i += 1) {
      var name = el.attributes[i].name;
      var value = el.attributes[i].value;
      if (name === 'data-signals') {
        setSignals(parseSignals(value), false);
      } else if (looksManaged(name)) {
        upsert(el, name, value);
      }
    }
  }

  function bindEvent(el, kind, expression) {
    var key = 'event:' + kind;
    if (el.dataset[key]) return;
    el.dataset[key] = expression;
    el.addEventListener(kind, function (event) {
      if (kind === 'submit' || el.tagName === 'A') event.preventDefault();
      currentEvent = event;
      currentForm = el.closest ? el.closest('form') : null;
      evaluate(expression, el, event);
      currentEvent = null;
      currentForm = null;
    });
  }

  function upsert(el, name, value) {
    for (var i = 0; i < boundElements.length; i += 1) {
      if (boundElements[i].el === el && boundElements[i].name === name) {
        boundElements[i].expression = value;
        return;
      }
    }
    var entry = { el: el, name: name, expression: value };
    boundElements.push(entry);
    applyBinding(entry);
  }

  function applyBinding(entry) {
    var value = evaluate(entry.expression, entry.el, currentEvent);
    if (entry.name === 'data-text') {
      entry.el.textContent = value == null ? '' : String(value);
    } else if (entry.name === 'data-show') {
      entry.el.style.display = value ? '' : 'none';
    } else if (entry.name.lastIndexOf('data-attr-', 0) === 0) {
      var attribute = entry.name.slice('data-attr-'.length);
      if (value == null || value === false) entry.el.removeAttribute(attribute);
      else entry.el.setAttribute(attribute, value === true ? '' : String(value));
    }
  }

  function formEncode(body) {
    if (typeof body === 'string') return body;
    return new URLSearchParams(body).toString();
  }

  /* ------------------------------------------------- the live metrics stream */

  function openMetricsStream(url) {
    var source = new EventSource(url);
    ['datastar-merge-fragments', 'datastar-remove-fragments', 'datastar-merge-signals',
      'datastar-patch-elements', 'datastar-patch-signals', 'datastar-execute-script'
    ].forEach(function (kind) {
      source.addEventListener(kind, function (message) {
        applyEvent(parseEventData(kind, message.data.replace(/\r/g, '')));
      });
    });
    return source;
  }

  return {
    signals: SIGNALS,
    scan: scan,
    applyEvent: applyEvent,
    openMetricsStream: openMetricsStream
  };
})();

document.addEventListener('DOMContentLoaded', function () {
  TERRABASE_STUDIO.scan(document.documentElement);
  var metrics = document.querySelector('[data-metrics-stream]');
  if (metrics) TERRABASE_STUDIO.openMetricsStream(metrics.getAttribute('data-metrics-stream'));
});
