AI IS NOT ALLOWED TO EDIT THIS FILE ONLY HUMANS CAN!

# Rules
- We need to use vitepress
- This is a public docs repo, we should not have internal information here like which database we are using interally and similar information
- This repo must not have internal information

## Local CPU Priority

- When building or testing locally, run every build, test, lint, and benchmark
  command at low CPU priority by prefixing it with `nice -n 19` (for example
  `nice -n 19 make test-fast`). Local dev servers and watchers count too.
- In CI (`CI=true`) use full CPU priority: do nothing. Never add `nice` or
  `renice` to CI workflows, Makefiles, scripts, or Containerfiles; the low
  priority is applied by whoever runs the command locally.
