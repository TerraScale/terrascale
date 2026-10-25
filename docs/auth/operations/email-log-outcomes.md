# Email send diagnostics

Auth suppresses OpenIddict and Microsoft Information chatter, while retaining
Microsoft.Hosting.Lifetime startup/shutdown messages and Warning/Error events.
TerraScale email outcomes remain at Information.

Each Cloudflare send attempt records a recipient SHA-256 hash and a provider
outcome. `queued` means provider acceptance, not delivery to the inbox. `bounced`
means a permanent bounce (including provider suppression); `429` means the
provider refused the request due to its limit. Other failures report `error`.
Neither addresses nor message bodies, codes, provider response bodies or tokens
belong in logs.

Lower volume alone does not guarantee 24 hours of retention: ts-orch must retain
at least that window at the observed peak byte rate. Production rollout and host
retention changes belong to the orchestrator owner.
