# Profile preferences and validation

The profile stores English (`en`) or Portuguese (`pt-BR`) as an account language
preference. The dashboard currently displays in English. The preference control
explains that saving a preference does not translate the interface; neither
selection implies that a translated dashboard or onboarding flow exists.

Profile forms retain rejected drafts. Validation identifies the relevant name,
phone or language control using `aria-invalid`, a linked inline error, an error
summary link and focus on the rejected control. A blank display name is rejected
in the browser form; MCP's omitted-name behavior is unchanged. Accepted profile
and language saves keep their explicit success confirmations.

Both forms use the existing submit-once and busy-button behavior. While a save
is pending, the button announces its busy state and repeated submissions are
blocked. A validation response allows correction and retry. No shared UI,
document-language declaration, translation, storage or API-version change is
included. HTTP journeys cover preference persistence and rejected field state;
browser journeys observe native submit events and real HTTP requests to verify
busy and duplicate-submit behavior, then check correction and retry against
the running fixture.
