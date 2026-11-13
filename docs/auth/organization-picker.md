# Organization picker at dashboard sign-in

Project settings return `organization_picker`, defaulting to false, and accept it in partial settings updates. The dashboard Authentication > Settings section saves it with project management permission and CSRF protection.

The reserved `terrascale` tenant uses the bootstrap auth project's setting. Only the dashboard OAuth client receives `ts_org_picker: "true"` in its ID token when that setting is on. Access tokens and customer project sign-ins carry no such claim. Customer projects store and return the setting without adding an organization step to hosted login.

Without the claim, dashboard sign-in continues to the return target without requesting an organization count. With the claim, users belonging to several organizations choose one before continuing. API and onboarding continuations retain their targets.

Authentication management requires the live admin project role and an active, unarchived organization without a pending deletion or MFA requirement. This identity permission also applies to the bootstrap projects. Platform project data writes retain their separate prohibition.
