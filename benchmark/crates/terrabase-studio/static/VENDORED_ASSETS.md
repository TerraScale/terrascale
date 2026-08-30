# Vendored browser assets

All browser dependencies are served from the Studio binary. Updating one requires pinning a new upstream release, copying its license and notices, and replacing the checksums below.

## Datastar

- Version: `1.0.3`
- Upstream tag: `v1.0.3`
- Upstream commit: `73ab00e7c06d8c2bad030fdddafba800fcccbde2`
- Source: `https://raw.githubusercontent.com/starfederation/datastar/v1.0.3/bundles/datastar.js`
- Local file: `datastar-1.0.3.js`
- SHA-256: `5d6b7794a50a83d82da962aec5e382f5ae83ac7afbc751f903f7a9c6bd433c65`
- License file: `LICENSE.datastar.md`
- Upstream license SHA-256: `17da0e216d3c6a819526f5b72a6a44d84d34bcd9beb4191ac6af89c1508ef08f`
- Local license SHA-256: `d3744837a0204e16cce7dfbcc77a1b72f302a74d10089a4dd552147582151cb0` (the local copy adds a final newline)
- Retrieved: `2026-08-30`

Studio's Monaco-only integration is maintained in this repository:

- Local file: `studio-monaco.js`
- SHA-256: `298fa053283ed077fff5fd545f12cf4678d2138b25eef6b7a0a9acde6c2b5d58`
- Scope: Monaco mount, model synchronization, and disposal only. Datastar owns actions, signals, and SSE handling.

## Basecoat CSS

- Package: `basecoat-css`
- Version: `1.0.2`
- Upstream tag and commit: `1.0.2`, `6953a4adbe63cb5ba25e2bda30d7b76202d4f2b4`
- Distribution source: `https://cdn.jsdelivr.net/npm/basecoat-css@1.0.2/dist/basecoat.cdn.css`
- Distribution SHA-256 before the local provenance header: `b141fedba05673faedc5f70d0fb7fb18bab9d5f1449f68945a151824941d51dd`
- Local file: `basecoat.css`
- Local SHA-256: `7159a2e4e182e373cf093a82debb6cb3fa5f1c97dcdffbd01df3804a5b4a2de2`
- License file: `LICENSE.basecoat.md`
- Upstream license SHA-256: `160a6cea26ff1931297957499ab739e94c4bc79ecea18b490c9f533fa76ace78`
- Local license SHA-256: `0fc06a4298f05b38473b01c86c978861c7ab01c2ceaebeeb9c3cc67b7a2b77ef` (the local copy adds a final newline)
- npm package integrity: `sha512-C6I5rr7HziIAoBYNGPO5U6HaU/6FuF3iVp++IgoEqqJ836is3XoRty6l1ibXqxnoIRZNnOAYDMX8QxVC+58zhw==`
- Retrieved: `2026-08-30`

## Monaco Editor

- Version: `0.52.2`
- Package: `https://registry.npmjs.org/monaco-editor/-/monaco-editor-0.52.2.tgz`
- npm package integrity: `sha512-GEQWEZmfkOGLdd3XK8ryrfWz3AIP8YymVXiPHEdewrUq7mh0qrKrfHLNCXcbB6sTnMLnOZ3ztSiKcciFUkIJwQ==`
- License: `monaco/LICENSE`
- Third-party notices: `monaco/ThirdPartyNotices.txt`
- Retrieved: `2026-08-30`

The selected Monaco files have these SHA-256 checksums:

```text
28f3584fd04b182dfce15a9a1ce35b25bea22b31464aee500372bed18b7fee1a  monaco/vs/loader.js
90b588bc0b624e24052a576e1bcab2eaffec7bc666895188862eebd9c9745782  monaco/vs/editor/editor.main.js
857bc0beafefbcc83ab5cf3510e929391c0314add43ee1a4cb8ecaf4726abbac  monaco/vs/editor/editor.main.css
dbe4aa4f2874f5768e8d98e94281667c0bc493e02dd9760adca930699ff3279b  monaco/vs/base/worker/workerMain.js
9200ffce4ac390675f6cb4c64ffaf4b4d8e650d3888caf631709cb136615e379  monaco/vs/language/json/jsonMode.js
4b197c4165e356a04857e9317ec3d536fdbb92b91077bc483590c4f51056e3df  monaco/vs/language/json/jsonWorker.js
0f1d5219934e96e83b8db162d60b4d8c09b5de1e7d38031cbafe4a3c0f2889c9  monaco/vs/base/browser/ui/codicons/codicon/codicon.ttf
33e4ff1a06ef62ba21788ea162564ee8165269a24a9ce6ef301837447eab0ac6  monaco/LICENSE
790537262fc78a764e121e6b92b959bcd3f5c310b47d9d9b9e92e17fe0af5336  monaco/ThirdPartyNotices.txt
```
