# Project hosted-page settings

The existing `auth.Projects.GetSettingsAsync` and `UpdateSettingsAsync` methods
carry project branding and policy. Both use the original platform sign-in access
token and `/v1/{project}/settings`; the producer checks project viewer/admin
permissions. There are no new SDK operations or retry behavior.

`TerraScaleAuthProjectBranding` adds optional `TitleSize`, `DefaultTheme`,
`FooterLinks`, `BackgroundColor` and `TextColor`. Footer entries are
`TerraScaleAuthProjectFooterLink(Label, Url)`. New nullable properties are omitted
when null, preserving calls that only send name, logo and accent. Sending a
branding object replaces branding; fields omitted from that object reset to
producer defaults. Omit the entire branding object to leave it unchanged.

`TerraScaleAuthProjectSettingsUpdate.Configuration` takes
`TerraScaleAuthProjectConfiguration(RedirectUrls, SessionLifetimeSeconds)`;
settings responses expose the same type. Its default lifetime is 1209600
seconds. Omit configuration to leave it unchanged; sending it replaces the
whole policy. An empty redirect list adds no restriction beyond the OAuth
client's exact registered callbacks.

The producer validates HTTPS logo/footer URLs, six-digit hex colours, title
sizes 20–40, light/dark/system appearance, up to five footer links, at most 100
exact redirect URLs and session lifetimes 300–2592000 seconds. Redirects allow
HTTP only on loopback development addresses and reject userinfo and fragments.
Invalid settings return the producer's typed `400 invalid_request` error.

Existing constructor calls remain valid through optional parameters. Positional
record deconstruction changes arity; consumers that deconstruct these records
must update their patterns. This SDK
contract test uses an HTTP fixture; it does not establish live-service or
installed-dashboard qualification. The concrete producer revision and source hashes are in
`hosted-settings-provenance.json`. The owning producer contract landed in TerraScale/terrascale#609. Immutable
package qualification follows the SDK merge.
