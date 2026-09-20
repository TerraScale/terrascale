/** Dependency-free BLAKE3-256 for export artifact integrity checks. */
// BLAKE3-256, kept dependency-free so a blank CI runner can verify bundles.
export function blake3(data: Uint8Array): string {
  const IV = [0x6A09E667, 0xBB67AE85, 0x3C6EF372, 0xA54FF53A, 0x510E527F, 0x9B05688C, 0x1F83D9AB, 0x5BE0CD19];
  const permutation = [2, 6, 3, 10, 7, 0, 4, 13, 1, 11, 12, 5, 9, 14, 15, 8];
  const rotate = (value: number, bits: number) => ((value >>> bits) | (value << (32 - bits))) >>> 0;
  const g = (s: number[], a: number, b: number, c: number, d: number, x: number, y: number) => { s[a] = (s[a]! + s[b]! + x) >>> 0; s[d] = rotate(s[d]! ^ s[a]!, 16); s[c] = (s[c]! + s[d]!) >>> 0; s[b] = rotate(s[b]! ^ s[c]!, 12); s[a] = (s[a]! + s[b]! + y) >>> 0; s[d] = rotate(s[d]! ^ s[a]!, 8); s[c] = (s[c]! + s[d]!) >>> 0; s[b] = rotate(s[b]! ^ s[c]!, 7); };
  const compress = (cv: number[], words: number[], counter: number, length: number, flags: number) => { const s = [...cv, ...IV]; s[12] = counter >>> 0; s[13] = Math.floor(counter / 0x100000000); s[14] = length; s[15] = flags; let m = words.slice(); for (let round = 0; round < 7; round++) { g(s, 0, 4, 8, 12, m[0]!, m[1]!); g(s, 1, 5, 9, 13, m[2]!, m[3]!); g(s, 2, 6, 10, 14, m[4]!, m[5]!); g(s, 3, 7, 11, 15, m[6]!, m[7]!); g(s, 0, 5, 10, 15, m[8]!, m[9]!); g(s, 1, 6, 11, 12, m[10]!, m[11]!); g(s, 2, 7, 8, 13, m[12]!, m[13]!); g(s, 3, 4, 9, 14, m[14]!, m[15]!); m = permutation.map((i) => m[i]!); } return [...Array.from({ length: 8 }, (_, i) => (s[i]! ^ s[i + 8]!) >>> 0), ...Array.from({ length: 8 }, (_, i) => (s[i + 8]! ^ cv[i]!) >>> 0)]; };
  const words = (block: Uint8Array) => { const p = new Uint8Array(64); p.set(block); return Array.from({ length: 16 }, (_, i) => (p[i * 4]! | p[i * 4 + 1]! << 8 | p[i * 4 + 2]! << 16 | p[i * 4 + 3]! << 24) >>> 0); };
  const chunk = (bytes: Uint8Array, index: number) => { let cv = IV.slice(); const blocks = Math.max(1, Math.ceil(bytes.length / 64)); for (let i = 0; i < blocks - 1; i++) cv = compress(cv, words(bytes.slice(i * 64, i * 64 + 64)), index, 64, i === 0 ? 1 : 0).slice(0, 8); const final = bytes.slice((blocks - 1) * 64); return [cv, words(final), index, final.length, 2 | (blocks === 1 ? 1 : 0)] as const; };
  const outputCv = (value: readonly [number[], number[], number, number, number]) => compress(...value).slice(0, 8);
  const chunks = Array.from({ length: Math.max(1, Math.ceil(data.length / 1024)) }, (_, i) => data.slice(i * 1024, i * 1024 + 1024));
  const stack: number[][] = []; let current = chunk(chunks[0]!, 0); for (let i = 0; i < chunks.length; i++) { if (i > 0) current = chunk(chunks[i]!, i); let cv = outputCv(current); let total = i + 1; while ((total & 1) === 0) { current = [IV.slice(), [...stack.pop()!, ...cv], 0, 64, 4]; cv = outputCv(current); total >>= 1; } stack.push(cv); } stack.pop(); while (stack.length) current = [IV.slice(), [...stack.pop()!, ...outputCv(current)], 0, 64, 4];
  const output = compress(current[0], current[1], 0, current[3], current[4] | 8).slice(0, 8);
  return output.map((word) => Array.from({ length: 4 }, (_, i) => (word >>> (i * 8) & 0xff).toString(16).padStart(2, "0")).join("")).join("");
}
