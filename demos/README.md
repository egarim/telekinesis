# Telekinesis demo scenarios

Scripted flows executed by `telekinesis run <file> --enable-actions`. The runner
prints each `say` line as a caption while it drives the desktop, so a recording
narrates itself, and it exits nonzero on the first failure — which makes these
end-to-end tests as much as demos.

**File format:** [../docs/SCENARIOS.md](../docs/SCENARIOS.md).

| File | Demo | Validated on / needs |
|---|---|---|
| `calc-add.json` | computes 7+7 on Calculator by button name, native invoke only, verified by reading the display back | **validated** on Windows 11 (UIA); open Calculator first |
| `blog-navigate.json` | navigates a real blog: find a post link → native invoke → verify by title → Back → verify home | **validated** on Windows 11 + Edge |
| `thunar-navigate.json` | drives the Thunar file manager | **validated** on Linux (AT-SPI) |
| `thunar-fill-location.json` | fills Thunar's location bar | Linux (AT-SPI) |
| `cross-app-copy.json` | read from app A, act in app B | needs source + destination windows open |
| `fill-out-contact.json` | fill a real GUI form | needs a form app open (GNOME Contacts) |

On Linux, run `telekinesis doctor` first to confirm the accessibility bus and
`/dev/uinput` access.

Password safety is demonstrated by `read_element` on a login form (protected
fields read back masked in every mode) plus the `fill_credential` handoff — see
[../docs/REMOTE.md](../docs/REMOTE.md#credentials--the-handoff-rule).
