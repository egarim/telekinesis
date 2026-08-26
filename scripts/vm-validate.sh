#!/usr/bin/env bash
# Telekinesis VM validation — run this on the Linux desktop session (the Lun.Os VM).
# Proves the core loop end to end: environment → connect → perceive → (optional) act.
#
#   ./scripts/vm-validate.sh                 # read-only: doctor, list apps, walk a tree
#   ./scripts/vm-validate.sh --with-actions  # also runs a guarded test click/type
#
# Must run inside a graphical desktop session (needs DISPLAY/WAYLAND + the a11y bus).
set -uo pipefail

cd "$(dirname "$0")/.." || exit 1
CLI="dotnet run --project src/Telekinesis.Cli --"

section() { printf '\n\033[1;35m== %s\033[0m\n' "$1"; }
ok()      { printf '\033[1;32m✓ %s\033[0m\n' "$1"; }
warn()    { printf '\033[1;33m! %s\033[0m\n' "$1"; }

section "0. Preconditions"
command -v dotnet >/dev/null || { warn "dotnet not found — install the .NET 10 SDK"; exit 1; }
ok "dotnet $(dotnet --version)"
[ -n "${DISPLAY:-}${WAYLAND_DISPLAY:-}" ] || warn "No DISPLAY/WAYLAND_DISPLAY — are you in a graphical session?"

section "1. Build"
dotnet build -v q src/Telekinesis.Cli >/dev/null && ok "build succeeded" || { warn "build failed"; exit 1; }

section "2. doctor (environment diagnosis)"
$CLI doctor
DOCTOR=$?
[ $DOCTOR -eq 0 ] && ok "environment ready" || warn "doctor reported issues (see above) — a11y bus and/or uinput"

section "3. List applications on the accessibility bus"
$CLI probe || warn "probe failed — is the a11y bus enabled? (telekinesis setup)"

echo
warn "Pick an application id from the list above and inspect its tree:"
echo "    $CLI probe --app <id> --depth 2"
echo "    $CLI probe --find \"Save\""

if [ "${1:-}" = "--with-actions" ]; then
  section "4. Guarded action test"
  warn "This will actually move the pointer / press keys. Focus a safe window (e.g. a text editor) now."
  read -r -p "Press Enter to type 'telekinesis works' into the focused window, or Ctrl-C to skip... " _
  $CLI probe --enable-actions --type "telekinesis works"
  ACT=$?
  [ $ACT -eq 0 ] && ok "action path works — uinput injection succeeded" \
                 || warn "action failed — check /dev/uinput access (telekinesis setup)"
fi

section "Done"
echo "Next: run a full scenario once Codex ships the runner —"
echo "    telekinesis run demos/fill-out-contact.json"
