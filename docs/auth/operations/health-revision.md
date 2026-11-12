# Health answers name the build's commit

ts-auth's anonymous `/healthz` and `/readyz` answer `version` with the commit the running build was made from, and send
the same commit in `X-TerraScale-Revision`. This reverses pull request #446, which kept the commit off these answers so
that a running build could not be matched to known bugs. The dashboard has published its commit in its health answers
since 2026-11-04, the repository is public, and the production journey and the status site need the commit to confirm
that a ts-auth release is live. TerraBase still keeps its own commit off its public listener.
