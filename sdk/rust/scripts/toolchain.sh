toolchain_bin() {
  local tool=$1 label=$2 resolved
  if command -v rustup >/dev/null 2>&1; then
    resolved=$(rustup which --toolchain 1.99.1 "$tool")
  else
    for resolved in "${RUSTUP_HOME:-$HOME/.rustup}"/toolchains/1.99.1-*/bin/"$tool"; do
      [ -x "$resolved" ] && break
      resolved=
    done
    if [ -z "$resolved" ]; then
      resolved=$(command -v "$tool" || true)
      if [ -n "$resolved" ] && [[ "$("$resolved" --version 2>/dev/null)" != "$label 1.99.1 "* ]]; then
        resolved=
      fi
    fi
  fi
  [ -n "$resolved" ] && [ -x "$resolved" ] || {
    echo "sdk/rust needs Rust toolchain 1.99.1: install it with 'rustup toolchain install 1.99.1' or put a 1.99.1 $tool on PATH" >&2
    return 1
  }
  printf '%s\n' "$resolved"
}

toolchain_use() {
  local bin
  bin=$(toolchain_bin rustc 'rustc') || return 1
  bin=${bin%/*}
  export RUSTC="$bin/rustc"
  export RUSTDOC="$bin/rustdoc"
  export CARGO="$bin/cargo"
  [ -x "$CARGO" ] || {
    echo "Rust toolchain 1.99.1 at $bin has no cargo" >&2
    return 1
  }
  export PATH="$bin:$PATH"
}
