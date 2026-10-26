# Document dialog newline comparison

Browsers normalize textarea values to LF. A rendered default containing encoded CRLF can retain CRLF in `defaultValue`, so a direct comparison reports a pristine or restored draft as changed.

Add and Edit dialogs normalize default newlines before checking for unsaved changes. Real edits and retained rejected drafts still require discard confirmation; Monaco widgets keep their Escape handling. This changes no API, authorization, JSON value, or submission contract.

The application browser journey uses a multiline producer document and checks untouched and restored drafts with both Monaco and the native textarea fallback.
