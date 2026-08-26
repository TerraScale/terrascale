// Type declarations for the `next` module surface the studio uses.
//
// vinext aliases `next/*` to its shims at build time (mapped in tsconfig
// paths); the bare `next` module's typed exports (Metadata, Viewport) come
// from the metadata shim.

declare module "next" {
  export type { Metadata, Viewport } from "vinext/dist/shims/metadata";
}
