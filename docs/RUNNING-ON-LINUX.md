# Running Telekinesis on Linux — operational notes

Hard-won findings from live validation on Ubuntu 26.04 (arm64). These are the things
that make the difference between "0 applications" and a working accessible tree.

## 1. Accessibility must be *enabled*, not just running
The AT-SPI bus can be up while GTK/Qt apps still refuse to register their trees.
Toolkits check `org.a11y.Status.IsEnabled` and skip accessibility entirely when it's
false — the #1 cause of an empty `list_applications`.

```bash
gsettings set org.gnome.desktop.interface toolkit-accessibility true
```

`telekinesis doctor` now reports this as the `a11y-enabled` check.

## 2. Start apps in a properly-ordered session
On a stale or improperly-initialised session bus, GTK4 apps may never connect to the
a11y bus. A fresh `dbus-run-session` brings AT-SPI up in the right order:

```bash
dbus-run-session -- bash -c 'gnome-calculator & sleep 5; telekinesis probe'
```

In a real desktop session (GNOME/KDE) this is already handled by the session manager.

## 3. Input injection needs /dev/uinput access
The action path writes to `/dev/uinput`. Grant access once:

```bash
telekinesis setup            # prints the udev rule
# quick-and-dirty for a dev box:
sudo chmod 666 /dev/uinput
```

`telekinesis doctor` reports `uinput` as ok/not-ok.

## 4. Headless testing
For a desktop-less box (CI, a server VM), a virtual X display works:

```bash
sudo apt-get install -y xvfb at-spi2-core gtk-4-examples
Xvfb :99 -screen 0 1280x1024x24 &
export DISPLAY=:99
dbus-run-session -- bash -c 'gtk4-widget-factory & sleep 6; telekinesis probe'
```

## Validation quickstart
```bash
./scripts/vm-validate.sh                # read-only checks
./scripts/vm-validate.sh --with-actions # also exercises uinput
```

## What's proven working
Connection + a11y-bus discovery, `list_applications`, tree walk (roles/names/bounds),
`find_elements` with state filters, AT-SPI state (`au`) and extents (`(iiii)`) parsing,
and uinput injection (`press_keys`). Validated against `gtk4-widget-factory` on the host,
**and against a live Lun.Os XFCE container desktop** (10 real apps, full panel tree,
native button click).

## Containers (the Lun.Os webtop/XFCE case)
- The a11y bus GUID from `org.a11y.Bus.GetAddress` may be stale when the bus was
  restarted; Telekinesis strips it and connects to the live socket. (If you see
  "Unexpected GUID" on an older build, update.)
- `/dev/uinput` usually isn't present in a container. **Native AT-SPI actions
  (`invoke`/`set_text`/`set_value`) still work without it** — they use
  `Action.DoAction`/`EditableText`, validated by clicking a real XFCE panel button
  (`path=NativeAction`). Only the injection fallbacks (`click` by coordinate,
  `type_text`, `press_keys`) need `/dev/uinput` passed into the container.
- Enable a11y inside the container the same way (`toolkit-accessibility true` /
  `org.a11y.Status.IsEnabled=true`) — it defaults off there too.
- Find the session bus with: read `DBUS_SESSION_BUS_ADDRESS` from a GUI process's
  `/proc/<pid>/environ`, or test the `/tmp/dbus-*` sockets for `org.a11y.Bus`.
