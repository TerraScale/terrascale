/* A tagged template that escapes every interpolated value unless it is
   already Html, so views compose markup without a runtime framework. */

export class Html {
  constructor(readonly value: string) {}

  toString(): string {
    return this.value;
  }
}

const ENTITIES: Record<string, string> = { "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" };

export function escape(text: string): string {
  return text.replace(/[&<>"']/g, (char) => ENTITIES[char] ?? char);
}

/** Trusted markup, such as bundled SVG and CSS. */
export function raw(markup: string): Html {
  return new Html(markup);
}

function render(value: unknown): string {
  if (value instanceof Html) return value.value;
  if (Array.isArray(value)) return value.map(render).join("");
  if (value === null || value === undefined || value === false) return "";
  return escape(String(value));
}

export function html(strings: TemplateStringsArray, ...values: unknown[]): Html {
  let out = strings[0] ?? "";
  values.forEach((value, index) => {
    out += render(value) + (strings[index + 1] ?? "");
  });
  return new Html(out);
}
