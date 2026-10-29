// Fixed byte bound shared by both ends of the app token exchange.
export async function readTokenJson(response, signal) {
  const reader = response.body?.getReader()
  if (!reader) throw new Error('Invalid token response.')
  const bytes = new Uint8Array(16_384)
  let length = 0
  const cancel = () => { void reader.cancel().catch(() => {}) }
  signal.addEventListener('abort', cancel, { once: true })
  try {
    while (true) {
      signal.throwIfAborted()
      const { done, value } = await reader.read()
      signal.throwIfAborted()
      if (done) break
      if (value.byteLength > bytes.length - length)
        throw new Error('Invalid token response.')
      bytes.set(value, length)
      length += value.byteLength
    }
    return JSON.parse(new TextDecoder('utf-8', { fatal: true }).decode(bytes.subarray(0, length)))
  } finally {
    signal.removeEventListener('abort', cancel)
    await reader.cancel().catch(() => {})
    reader.releaseLock()
  }
}
