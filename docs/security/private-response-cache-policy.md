# Private response cache policy

OIDC userinfo responses carry `Cache-Control: no-store` and `Pragma: no-cache` for successful identity claims, denials and failures. An early response-header callback applies this policy at header commit, including protocol errors before handler execution and error envelopes that clear prepared headers.

TerraBase defaults protected responses to `Cache-Control: no-store` when no explicit cache policy is present. The policy runs when headers are committed and preserves explicit endpoint policies and ETags. Anonymous endpoints, discovery metadata and static assets are outside this default.

Shape origin responses use `private, no-store`, including key release and manifest responses. Successful `200` and `304` live shape polls without a `local` query parameter instead use `private, max-age=1, stale-while-revalidate=1`. This permits brief reuse in the client's private cache. Encrypted shape objects use `public, max-age=31536000, immutable`; their ciphertext policy does not grant access to shape keys. The public region list uses `public, max-age=5`.

The implementations are `src/TerraBase/Hosting/PrivateResponses.cs`, `src/TerraBase/Features/Shapes/ShapesExtensions.cs` and the userinfo header callback in `src/TerraScale.Authentication/App.cs`. Retained Fast coverage checks userinfo success and denial policies in `OidcFlowTests` and live shape cache headers in `TableShapeTests`.
