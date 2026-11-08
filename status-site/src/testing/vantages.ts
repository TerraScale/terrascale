/* Private Worker bindings and the remaining Durable Object namespace. */

interface Stub {
  fetch(url: string, init: RequestInit): Promise<Response>;
}

export const vantageBindings = (stubs: Record<string, Stub>) => {
  const stub = (code: string) => stubs[code] ?? { fetch: async () => Promise.reject(new Error(`no vantage ${code}`)) };
  return {
    VANTAGE_SFO: stub("sfo"),
    VANTAGE_GRU: stub("gru"),
    VANTAGE: { idFromName: (code: string) => code, get: stub },
  };
};
