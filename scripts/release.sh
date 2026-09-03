#!/usr/bin/env bash
# One-command release: build the six self-contained archives + the dotnet-tool
# nupkg, create the GitHub release, and push to nuget.org.
#
#   scripts/release.sh                # release the version in the csproj
#   scripts/release.sh --skip-nuget   # everything except the nuget.org push
#
# Expects: dotnet 10, gh (authenticated), and the nuget.org API key in the
# environment variable literally named "nuget.org" (EnvPane/launchd on macOS —
# it may not be in the shell env, so we fall back to `launchctl getenv`).
set -euo pipefail
cd "$(dirname "$0")/.."

CSPROJ=src/Telekinesis.Cli/Telekinesis.Cli.csproj
VERSION=$(sed -n 's/.*<Version>\(.*\)<\/Version>.*/\1/p' "$CSPROJ")
[[ -n "$VERSION" ]] || { echo "No <Version> in $CSPROJ"; exit 1; }
OUT=artifacts/release-$VERSION
rm -rf "$OUT" && mkdir -p "$OUT"
echo "■ releasing v$VERSION → $OUT"

# Windows archives are FOLDER publishes: WPF's UI Automation client crashes
# under single-file publish (System.Windows.Automation type initializer).
for rid in win-x64 win-arm64; do
  echo "  publish $rid"
  dotnet publish src/Telekinesis.Cli -c Release -f net10.0-windows -r "$rid" \
    --self-contained -o "$OUT/$rid" -v q
  (cd "$OUT/$rid" && zip -qr "../telekinesis-$VERSION-$rid.zip" .)
done
for rid in linux-x64 linux-arm64 osx-x64 osx-arm64; do
  echo "  publish $rid"
  dotnet publish src/Telekinesis.Cli -c Release -f net10.0 -r "$rid" \
    --self-contained -p:PublishSingleFile=true -o "$OUT/$rid" -v q
  (cd "$OUT/$rid" && tar czf "../telekinesis-$VERSION-$rid.tar.gz" .)
done

# dotnet-tool nupkg: portable net10.0 build bundling the net10.0-windows
# publish under win/ (see the csproj packing notes).
echo "  pack nupkg"
dotnet publish src/Telekinesis.Cli -c Release -f net10.0-windows -v q
dotnet pack src/Telekinesis.Cli -c Release -p:TargetFrameworks=net10.0 \
  -p:PackWinPayload=true -o "$OUT" -v q

echo "  tag + GitHub release"
git tag "v$VERSION" 2>/dev/null || echo "  (tag v$VERSION exists)"
git push origin "v$VERSION"
gh release create "v$VERSION" \
  "$OUT"/telekinesis-"$VERSION"-*.zip \
  "$OUT"/telekinesis-"$VERSION"-*.tar.gz \
  "$OUT"/Telekinesis."$VERSION".nupkg \
  --title "Telekinesis v$VERSION" --generate-notes

if [[ "${1:-}" != "--skip-nuget" ]]; then
  KEY="${nuget_org:-$(printenv nuget.org || true)}"
  [[ -n "${KEY:-}" ]] || KEY=$(launchctl getenv nuget.org 2>/dev/null || true)
  [[ -n "$KEY" ]] || { echo "No nuget.org API key found (env var 'nuget.org')."; exit 1; }
  echo "  nuget push"
  dotnet nuget push "$OUT/Telekinesis.$VERSION.nupkg" \
    --api-key "$KEY" --source https://api.nuget.org/v3/index.json
fi
echo "✓ v$VERSION released"
