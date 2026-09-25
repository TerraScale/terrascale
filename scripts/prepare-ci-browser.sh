#!/bin/sh
# Source only inside a terrascale-builder CI job before browser checks.

if [ "${CI:-}" != 1 ] || [ "${PLAYWRIGHT_BROWSERS_PATH:-}" != /usr/local/share/ms-playwright ] || [ ! -d /nix/store ]; then
  printf '%s\n' 'Browser setup requires the terrascale-builder CI container' >&2
  return 1
fi

before_mode=$(stat -c '%a' /tmp) || return 1
if [ "$before_mode" != 1777 ]; then
  chmod 1777 /tmp || return 1
fi
after_mode=$(stat -c '%a' /tmp) || return 1
printf 'Browser /tmp mode: %s -> %s\n' "$before_mode" "$after_mode"
if [ "$after_mode" != 1777 ]; then
  printf '%s\n' 'Browser /tmp must have mode 1777' >&2
  return 1
fi

if [ -z "${FONTCONFIG_FILE:-}" ]; then
  fontconfig_lib=
  remaining=${LD_LIBRARY_PATH:-}
  while [ -n "$remaining" ]; do
    entry=${remaining%%:*}
    if [ "$remaining" = "$entry" ]; then
      remaining=
    else
      remaining=${remaining#*:}
    fi
    case "$entry" in
      /nix/store/*-fontconfig-*-lib/lib)
        fontconfig_lib=$entry
        break
        ;;
    esac
  done
  if [ -z "$fontconfig_lib" ] || [ ! -r "$fontconfig_lib/libfontconfig.so.1" ]; then
    printf '%s\n' 'Active Nix Fontconfig library was not found' >&2
    return 1
  fi
  fontconfig_version=${fontconfig_lib##*-fontconfig-}
  fontconfig_version=${fontconfig_version%-lib/lib}
  fontconfig_matches=0
  for candidate in /nix/store/*-fontconfig-"$fontconfig_version"/etc/fonts/fonts.conf; do
    if [ -r "$candidate" ]; then
      fontconfig_matches=$((fontconfig_matches + 1))
      FONTCONFIG_FILE=$candidate
    fi
  done
  if [ "$fontconfig_matches" -ne 1 ]; then
    printf 'Expected one readable Fontconfig %s configuration in /nix/store\n' "$fontconfig_version" >&2
    return 1
  fi

  # The pinned Nix config names a DejaVu font store path. Check that its
  # referenced asset is present, rather than using a config with no fonts.
  font_dir=$(sed -n 's#^[[:space:]]*<dir>\(/nix/store/[^<]*-dejavu-fonts[^<]*\)</dir>.*#\1#p' "$FONTCONFIG_FILE")
  if [ -z "$font_dir" ] || [ ! -r "$font_dir/share/fonts/truetype/DejaVuSans.ttf" ]; then
    printf '%s\n' 'Nix Fontconfig references no readable DejaVu Sans font' >&2
    return 1
  fi
fi

if [ ! -r "$FONTCONFIG_FILE" ]; then
  printf 'FONTCONFIG_FILE is unreadable: %s\n' "$FONTCONFIG_FILE" >&2
  return 1
fi
export FONTCONFIG_FILE
printf 'Browser Fontconfig file: %s\n' "$FONTCONFIG_FILE"
