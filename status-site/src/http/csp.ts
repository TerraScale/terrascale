/* Content security policies. Pages allow same-origin stylesheets and
   scripts; routes can widen connect-src or frame-ancestors. */

export interface PolicyOptions {
  /** Extra origins the client script may fetch. */
  connect?: readonly string[];
  /** Who may frame the page; nobody by default. */
  frameAncestors?: string;
  /** Where forms may submit; nowhere by default. */
  formAction?: string;
  /** What the page may frame; nothing by default. */
  frame?: readonly string[];
}

const policies = new Map<string, Promise<string>>();

export function contentSecurityPolicy(options: PolicyOptions = {}): Promise<string> {
  const key = JSON.stringify(options);
  let policy = policies.get(key);
  if (!policy) {
    policy = Promise.resolve(
      [
        "default-src 'none'",
        "style-src 'self'",
        // Cloudflare injects its Web Analytics beacon into the HTML response.
        "script-src 'self' https://static.cloudflareinsights.com",
        ["connect-src 'self'", ...(options.connect ?? []), "https://cloudflareinsights.com"].join(" "),
        "font-src 'self'",
        ...(options.frame ? [`frame-src ${options.frame.join(" ")}`] : []),
        "img-src 'self' data:",
        "base-uri 'none'",
        `form-action ${options.formAction ?? "'none'"}`,
        `frame-ancestors ${options.frameAncestors ?? "'none'"}`,
      ].join("; "),
    );
    policies.set(key, policy);
  }
  return policy;
}
