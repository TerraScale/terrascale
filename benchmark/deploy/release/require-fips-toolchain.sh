#!/usr/bin/env bash
set -euo pipefail

version_at_least() {
  local observed_major=$1
  local observed_minor=$2
  local observed_patch=$3
  local required_major=$4
  local required_minor=$5
  local required_patch=$6

  ((observed_major > required_major)) \
    || ((observed_major == required_major && observed_minor > required_minor)) \
    || ((observed_major == required_major \
      && observed_minor == required_minor \
      && observed_patch >= required_patch))
}

cmake_command=${CMAKE:-cmake}
build_executor=${CMAKE_MAKE_PROGRAM:-}
go_command=${GO:-go}
perl_command=${PERL:-perl}

for tool in "$cmake_command" "$go_command" "$perl_command"; do
  command -v "$tool" >/dev/null || {
    echo "AWS-LC FIPS source builds require $tool" >&2
    exit 1
  }
done

"$perl_command" -e 'exit 0' || {
  echo "AWS-LC FIPS source builds require a working Perl interpreter" >&2
  exit 1
}

go_version=$("$go_command" env GOVERSION)
if [[ ! $go_version =~ ^go([0-9]+)\.([0-9]+)(\.([0-9]+))?$ ]]; then
  echo "cannot parse Go version for AWS-LC FIPS source build: $go_version" >&2
  exit 1
fi
go_major=${BASH_REMATCH[1]}
go_minor=${BASH_REMATCH[2]}
go_patch=${BASH_REMATCH[4]:-0}
version_at_least "$go_major" "$go_minor" "$go_patch" 1 17 13 || {
  echo "AWS-LC FIPS source builds require Go >= 1.17.13, observed: $go_version" >&2
  exit 1
}
"$go_command" list std >/dev/null || {
  echo "AWS-LC FIPS source builds require a working 'go list std'" >&2
  exit 1
}

cmake_line=$("$cmake_command" --version | sed -n '1p')
if [[ ! $cmake_line =~ ^cmake[[:space:]]+version[[:space:]]+([0-9]+)\.([0-9]+)(\.([0-9]+))? ]]; then
  echo "cannot parse CMake version for AWS-LC FIPS source build: $cmake_line" >&2
  exit 1
fi
cmake_major=${BASH_REMATCH[1]}
cmake_minor=${BASH_REMATCH[2]}
cmake_patch=${BASH_REMATCH[4]:-0}
version_at_least "$cmake_major" "$cmake_minor" "$cmake_patch" 3 5 0 || {
  echo "AWS-LC FIPS source builds require CMake >= 3.5, observed: $cmake_line" >&2
  exit 1
}

if [[ -n ${CC:-} ]]; then
  compiler=$CC
elif command -v clang >/dev/null; then
  compiler=clang
elif command -v gcc >/dev/null; then
  compiler=gcc
else
  echo "AWS-LC FIPS source builds require Clang or GCC" >&2
  exit 1
fi
"$compiler" --version >/dev/null || {
  echo "AWS-LC FIPS source builds require a working $compiler" >&2
  exit 1
}
if [[ -n ${CXX:-} ]]; then
  "$CXX" --version >/dev/null || {
    echo "AWS-LC FIPS source builds require a working C++ compiler: $CXX" >&2
    exit 1
  }
fi

if [[ -n $build_executor ]]; then
  :
elif command -v ninja >/dev/null; then
  build_executor=ninja
elif command -v make >/dev/null; then
  build_executor=make
else
  echo "AWS-LC FIPS source builds require Ninja or Make" >&2
  exit 1
fi
"$build_executor" --version >/dev/null || {
  echo "AWS-LC FIPS source builds require a working $build_executor" >&2
  exit 1
}

echo "validated AWS-LC FIPS source-build prerequisites"
