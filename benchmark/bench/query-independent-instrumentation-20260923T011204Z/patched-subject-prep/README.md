# Patched indexing subject preparation

Base: clean TerraBase commit `7e5a1851ce356a2f4cccbfdeffeb54635f6e9cc6`, tree `6ac4eeb26a369bf87e645158802da3873f6dca23`.

Only six indexing files were overlaid from the live checkout. The exact binary patch is in `patched-indexing.diff`; hashes and build inputs are in `source-manifest.txt`.

The patched TerraBase Release build and benchmark Release build passed with zero warnings/errors. Receipt self-check passed. No performance trial has been run against this patched subject pending final same-cut spool re-review.
