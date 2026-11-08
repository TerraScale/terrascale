# Project-only navigation (T27)

Screenshots of the app shell at 1280×800, captured on the platform producer
fake (`PlatformScenario`) with Playwright Chromium. The owner owns the
organization; the collaborator holds only a "member" grant on one project
(`Storefront`) inside it.

## What changed

| Viewer | Manage group (Organization, Analytics, Usage) | Picker shortcuts | Context trigger |
| --- | --- | --- | --- |
| Project-only collaborator | Hidden; the pages refuse non-members | "Create new project" / "All workspaces" hidden | "Project member" badge next to the project name |
| Organization owner (unchanged) | Shown | Shown | No badge |

## Files

- `project-only-1280.png` — collaborator's `/databases`: Overview, Build and
  Secure only; "Project member" badge in the sidebar trigger.
- `project-only-picker-1280.png` — the collaborator's workspace picker: no
  organization shortcuts below the granted project.
- `owner-1280.png` — the owner's `/databases`: unchanged navigation with the
  Manage group and organization count.
- `owner-picker-1280.png` — the owner's picker: "Create new project" and "All
  workspaces" shortcuts still present.
