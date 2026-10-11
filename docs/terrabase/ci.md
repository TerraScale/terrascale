# CI and releases

`.github/workflows/ci.yml` runs on every pull request and push to main, on a Blacksmith runner. Its one required check, `test`, builds the `test` stage of the root `Dockerfile`: whitespace format, Release build with analyzers as errors, the libs, auth and TerraBase checks, and the Fast suites. Blacksmith keeps the Docker layer cache and the NuGet cache mount between runs.

- Reproduce it locally with `make docker-test` (needs `../ts-dotnet` and the pinned TerraBase binary ts-auth's tests use, see the Makefile). Before pushing, `make check` runs the same gates plus the benchmark checks.
- The slow suites run on pushes to main in the non-blocking `slow` job (`--target test-slow`); `make test-slow` runs them locally.
- Releases and packages are not published automatically. Build an artifact you need with its `make` target, for example `make terrabase-publish`.
