/**
 * The React entrypoint intentionally has no hard runtime dependency in the
 * core SDK. Package publication wires React as a peer dependency; this small
 * declaration keeps the contract typecheckable until that wiring lands.
 */
declare module "react" {
  export function useCallback<T extends (...args: never[]) => unknown>(callback: T, dependencies: readonly unknown[]): T;
  export function useEffect(effect: () => void | (() => void), dependencies?: readonly unknown[]): void;
  export function useMemo<T>(factory: () => T, dependencies: readonly unknown[]): T;
  export function useRef<T>(initialValue: T): { current: T };
  export function useSyncExternalStore<T>(subscribe: (listener: () => void) => () => void, getSnapshot: () => T, getServerSnapshot: () => T): T;
}
