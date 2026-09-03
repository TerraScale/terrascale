# ts-typescript goal

## Authority

The [workspace goal](../GOAL.md) is the sole authority for ts-typescript.

## Mission

The public SDK `@terrascale/sdk` for browser and Node: named typed operations
over the public API, the reactive MessagePack client, and the server-only
Better Auth adapter. Never speaks TBEP.

## Verification

```bash
npm install
npm run typecheck
npm run build
npm test                       # unit + msgpack + integration
npm run check:openapi-drift && npm run check:route-manifest \
  && npm run check:forbidden-surface
npm run test:browser           # when the task touches the browser client
```
