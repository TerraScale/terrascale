import * as universal from '../entries/pages/_layout.ts.js';

export const index = 0;
let component_cache;
export const component = async () => component_cache ??= (await import('../entries/pages/_layout.svelte.js')).default;
export { universal };
export const universal_id = "src/routes/+layout.ts";
export const imports = ["_app/immutable/nodes/0.-6sDLxq7.js","_app/immutable/chunks/l1zXGMTh.js","_app/immutable/chunks/Dpgm8aOI.js","_app/immutable/chunks/Kp7fUL91.js","_app/immutable/chunks/CFKVnMbq.js","_app/immutable/chunks/DM-4B6sx.js","_app/immutable/chunks/DWqgpYtO.js"];
export const stylesheets = ["_app/immutable/assets/Icon.DL8K1WpH.css","_app/immutable/assets/0.C_xZyqdC.css"];
export const fonts = [];
