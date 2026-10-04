// Verifies the v1 client's route table against the frozen terrabase contract
// documents (docs/api-v1.md, query-v1.md, shapes-v1.md).

/**
 * @typedef {object} RouteDeclaration
 * @property {string} operation
 * @property {string} method
 * @property {string} pathTemplate
 * @property {string} contract
 */

/**
 * The declarations a contract document may use for a route, derived from the
 * method and path the client actually sends. A document that states
 * "Base path: `B`" declares routes under B as "`METHOD …/<last segment of B>…`".
 * @param {RouteDeclaration} route
 * @param {string} text
 * @returns {string[]}
 */
export function routeDeclarations(route, text) {
  const declarations = [`\`${route.method} ${route.pathTemplate}`];
  for (const match of text.matchAll(/Base path: `([^`]+)`/gu)) {
    const base = /** @type {string} */ (match[1]);
    if (route.pathTemplate === base || route.pathTemplate.startsWith(`${base}/`))
      declarations.push(`\`${route.method} …${route.pathTemplate.slice(base.lastIndexOf("/"))}`);
  }
  return declarations;
}

/**
 * Requires every route's method and path to be declared in its contract
 * document. A declaration ends at its closing backtick or its query string.
 * @param {readonly RouteDeclaration[]} routes
 * @param {Readonly<Record<string, string>>} texts Contract document texts by file name.
 * @returns {void}
 */
export function verifyRouteDeclarations(routes, texts) {
  for (const route of routes) {
    const text = texts[route.contract];
    if (text === undefined) throw new Error(`${route.operation} names an unknown contract document ${route.contract}.`);
    const declared = routeDeclarations(route, text).some(
      (declaration) => text.includes(`${declaration}\``) || text.includes(`${declaration}?`),
    );
    if (!declared)
      throw new Error(`${route.operation} (${route.method} ${route.pathTemplate}) is not declared in ${route.contract}.`);
  }
}
