---
---
<!-- i18n:fallback:hi -->
> **अनुवाद लंबित।** इस पृष्ठ का मानव-समीक्षित हिंदी अनुवाद अभी उपलब्ध नहीं है, इसलिए मूल अंग्रेज़ी सामग्री दिखाई जा रही है।

# Getting started

From `npx create-terrascale-app` to a browser tab updating live from another
tab in under 5 minutes (D-74), on the free tier.

```bash
npx create-terrascale-app my-app
cd my-app
go install ./cmd/terrascale   # from a ts-cli checkout; CLI is not an npm package
terrascale dev <database> --watch=false --no-log-tail
```

The CRUD, SQL, and reactive snippets run against a real sandbox branch
pre-filled with the reader's key (D-56). Tenant provisioning is shown as a
pending stub because its public route is not published. Try CRUD:

<<< @/snippets/crud.ts

Next: [Concepts](./concepts.md) · [Runnable snippets](./snippets.md) · [Pricing](./pricing.md).
