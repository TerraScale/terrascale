# Profile save feedback

Saving profile details or the language preference redirects to the Profile tab
with an explicit success notice announced through the existing polite status
region. The notice distinguishes “Profile saved.” from “Language preference
saved.” Unknown status values do not become messages, and notices are confined
to the Profile tab.

Validation failures retain the existing error response and drafted profile
fields, without a success notice. No authentication, storage or API-body
contract changes are introduced.
