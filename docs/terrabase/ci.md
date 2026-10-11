# CI and releases

There are no CI workflows in this repository. Nothing runs on push, pull request or schedule.

- Before pushing, run `make check`. It runs the libs, auth and TerraBase gates, the benchmark checks and the Fast suites.
- The slow suites run on demand: `make test-slow`.
- Releases and packages are not published automatically. Build an artifact you need with its `make` target, for example `make terrabase-publish`.
