#!/usr/bin/env sh
set -eu

plugin_root=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
os=$(uname -s)
arch=$(uname -m)

case "$os:$arch" in
  Darwin:arm64|Darwin:aarch64) rid='osx-arm64' ;;
  Darwin:x86_64|Darwin:amd64) rid='osx-x64' ;;
  Linux:aarch64|Linux:arm64) rid='linux-arm64' ;;
  Linux:x86_64|Linux:amd64) rid='linux-x64' ;;
  *)
    printf 'Unsupported Plan Forge host: %s/%s\n' "$os" "$arch" >&2
    exit 1
    ;;
esac

executable="$plugin_root/bin/$rid/planforge"
if [ ! -x "$executable" ]; then
  printf 'Plan Forge executable is missing or not executable: %s\n' "$executable" >&2
  exit 1
fi

exec "$executable" "$@"
