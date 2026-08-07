# Project team member schema parity

`TeamMember.displayName` is required and `TeamMember.joinedAt` is required but nullable (`Timestamp | null`), matching the committed `ProjectTeamMemberResponse` descriptor. The public schema parity test pins both TypeScript shape and descriptor requiredness/nullability.
