#!/usr/bin/env bash
# Materialises the eval fixtures as git repositories and verifies their starting state.
# Usage: bash setup.sh <output-root>
#
# After this script: <root>/pricing, <root>/rates, <root>/orders, <root>/customers are committed
# repositories whose own tests pass; <root>/<name>-hidden holds the grader-only test projects.
# The hidden tests for pricing and rates are expected to FAIL on the untouched fixture (the bug is
# present, the cache is absent); the orders hidden tests are expected to PASS (nothing removed yet).
set -euo pipefail

root="$1"
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1

rm -rf "$root"
mkdir -p "$root"
node "$here/make-fixtures.mjs" "$root"

commit() {
  git -C "$1" -c user.name=fixture -c user.email=fixture@example.com add -A
  git -C "$1" -c user.name=fixture -c user.email=fixture@example.com commit -q -m "$2"
}

for name in pricing rates orders customers; do
  dir="$root/$name"
  (cd "$dir" && dotnet new sln --name "$name" >/dev/null && dotnet sln add src/*/*.csproj tests/*/*.csproj >/dev/null)
  git -C "$dir" init -q
done

commit "$root/pricing" "Invoice and refund pricing"
commit "$root/rates" "Exchange rate service"
commit "$root/orders" "Order export"
commit "$root/customers" "Customer service and city report"

# The review fixture gets its PR on top of the base commit.
cp -r "$root/customers.pr/." "$root/customers/"
rm -rf "$root/customers.pr"
commit "$root/customers" "Add customer lookup by email and simplify report writing"

echo
echo "== fixture tests (all expected to pass) =="
for name in pricing rates orders customers; do
  printf '%s: ' "$name"
  (cd "$root/$name" && dotnet test --nologo -v q 2>&1 | grep -E "Passed!|Failed!|error" | tail -1)
done

echo
echo "== hidden tests on the untouched fixtures =="
for name in pricing rates orders; do
  proj="$(ls -d "$root/$name-hidden"/*.HiddenTests)"
  target="$root/$name/tests/$(basename "$proj")"
  cp -r "$proj" "$target"
  printf '%s (expect %s): ' "$name" "$([ "$name" = orders ] && echo pass || echo FAIL)"
  # A failing hidden test is the expected outcome here, so the pipeline must not abort the script.
  (cd "$root/$name" && { dotnet test --nologo -v q "tests/$(basename "$proj")" 2>&1 || true; } | grep -E "Passed!|Failed!|error" | tail -1)
  rm -rf "$target"
  git -C "$root/$name" status --short | grep -q . && echo "WARNING: $name working tree is dirty" || true
done
