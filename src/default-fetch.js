/** Native browser transport; Node dependencies stay outside this module.
 * @returns {typeof globalThis.fetch | undefined}
 */
export function defaultFetch() {
  return globalThis.fetch?.bind(globalThis);
}
