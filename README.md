# Telekinesis

<img src="docs/media/telekinesis-mascot-512.png" alt="The Telekinesis ghost" width="160" align="right"/>

**Move things without touching them.** Telekinesis is an MCP server that lets AI agents
see and control the desktop through the platform accessibility APIs — the same channel
screen readers use. Semantic perception ("the Save button") instead of pixel-guessing,
at a fraction of the cost of screenshot-driven computer use.

**Watch it work** (YouTube Shorts — click to play):

| [![The helpful ghost](https://img.youtube.com/vi/Tv_lZBmAVGI/maxresdefault.jpg)](https://youtube.com/shorts/Tv_lZBmAVGI) | [![Three apps, zero screenshots](https://img.youtube.com/vi/-a5_3NY6MuI/maxresdefault.jpg)](https://youtube.com/shorts/-a5_3NY6MuI) | [![The principle: the accessibility tree](https://img.youtube.com/vi/fsNQ3THudmk/maxresdefault.jpg)](https://youtube.com/shorts/fsNQ3THudmk) | [![Install it in 2 minutes](https://img.youtube.com/vi/BxQB6I0dPco/maxresdefault.jpg)](https://youtube.com/shorts/BxQB6I0dPco) |
|:---:|:---:|:---:|:---:|
| [The helpful ghost](https://youtube.com/shorts/Tv_lZBmAVGI) | [Three apps, zero screenshots](https://youtube.com/shorts/-a5_3NY6MuI) | [The principle](https://youtube.com/shorts/fsNQ3THudmk) | [Install & wire it up](https://youtube.com/shorts/BxQB6I0dPco) |

```
dotnet tool install -g Telekinesis
```

No .NET on the machine? Grab a self-contained single-file build from the
[releases page](https://github.com/egarim/telekinesis/releases) — Windows/Linux/macOS,
x64 and arm64, no runtime required. (The dotnet-tool route does need the .NET 10
runtime, plus the Windows Desktop runtime on Windows.)

MCP client config:

```json
{ "mcpServers": { "telekinesis": { "command": "telekinesis" } } }
```

## Modes

- **Clairvoyant mode** (`telekinesis --read-only`) — perception only: `list_applications`,
  `get_tree`, `find_elements`, `read_element`, `read_page`, `get_focused`, `wait_for`,
  `assert_element`, `highlight`. Safe to expose; needs no input permissions.
  Password-field content is never exposed.
- **Telekinesis mode** (default) — adds actions: `invoke`, `set_text`, `set_value`,
  `click`, `click_at`, `type_text`, `press_keys`, `navigate`, `fill_credential`. Each
  action tries the native accessibility action first and falls back to OS input
  injection, reporting which path ran. Every action is audit-logged.
- **Vision tier** (last resort) — for the moments when the accessibility tree fails:
  `screenshot` captures pixels, `parse_screen` turns them into clickable elements via
  an optional [OmniParser](https://github.com/microsoft/OmniParser) sidecar, and
  `click_at` acts on them. Screens seen before answer instantly from
  [perceptual memory](docs/PERCEPTUAL-MEMORY.md), which also learns the targets that
  worked (`recall_targets`) and exports them as a training-ready dataset. See
  [docs/VISION.md](docs/VISION.md).
- **Browsers, first-class** — the web comes through the same tree: `read_page`
  snapshots a page (reading text + links/buttons/fields with actionable ids),
  `find_elements` scopes to `page` or `chrome` so browser UI stops shadowing page
  content, and `navigate` loads a URL. No browser driver, no CDP, no scraping — and an
  optional CDP tier (`TELEKINESIS_CDP=1`) adds console, network metadata and JS
  evaluation, which no accessibility tree can expose. See
  [docs/BROWSERS.md](docs/BROWSERS.md).
- **Provider plugins** — app-matched fidelity behind the same interface: a registry
  resolves each application to the highest-priority provider that claims it (the
  browser provider un-shadows page content by default; the vision tier is the built-in
  fallback provider). External plugins load only by explicit opt-in and are flagged by
  `doctor`. See [docs/PROVIDERS.md](docs/PROVIDERS.md).
- **Medium** — build accessible apps for *humans and AI agents*: annotate your code and
  a deterministic generator emits a `telekinesis.medium.json` sidecar that enriches the
  runtime tree with stable semantic IDs, intent, risk, and confirmation requirements —
  merged onto the same element model, no second automation stack. **Flutter apps are
  first-class**: the [`telekinesis_medium`](https://pub.dev/packages/telekinesis_medium)
  package on pub.dev mirrors the C# attributes with a `build_runner` generator, and
  Flutter Windows surfaces the tree via UIA (one line:
  `SemanticsBinding.instance.ensureSemantics()`). Matching survives localization: set
  the platform automation id (Flutter's `Semantics(identifier:)`) to the semantic id.
  Blazor is supported via `Telekinesis.Medium.Blazor` (note: Blazor *Server* circuits
  can publish semantics but resist synthetic input — use WebAssembly to be driven).
  See [docs/MEDIUM.md](docs/MEDIUM.md).
- **Interactive terminals** — not everything an agent needs has a GUI. `console_open`
  starts a persistent session in a **real PTY**, `console_write` types into it and
  `console_read` returns the *rendered screen* — ANSI applied, so a progress bar or a
  full TUI reads the way a human sees it, not as a stream of escape bytes. The
  working directory, the environment, an ssh hop and a half-typed REPL expression all
  survive between calls. Action tier, audit-logged; Linux/macOS supported, Windows
  behind `TELEKINESIS_CONPTY=1` until the ConPTY child-attach bug clears.
  See [docs/CONSOLE.md](docs/CONSOLE.md).
- **X-ray overlay** — see what the AI sees, on the real desktop: `highlight` boxes an
  element, `probe --overlay` draws live labeled boxes over a whole app, and
  `TELEKINESIS_SHOW_INTENT=1` makes every injected action flash its target before the
  input lands. Click-through, never steals focus. See [docs/XRAY-OVERLAY.md](docs/XRAY-OVERLAY.md).

[![X-ray overlay demo](docs/media/xray-overlay-demo.png)](docs/media/xray-overlay-demo.mp4)

*The X-ray overlay over Calculator — every element the ghost can see, boxed and labeled
live; then it computes 7+7 with each click telegraphed
([video](docs/media/xray-overlay-demo.mp4)).*

## Every tool

All 31 MCP tools, by tier. **Perception** loads in every mode, including
`--read-only` and over `serve` without `--enable-actions`; **action** needs
actions enabled.

| Tier | Tools |
|---|---|
| Perception — tree | `list_applications` `get_tree` `find_elements` `read_element` `get_focused` `wait_for` `assert_element` `highlight` |
| Perception — browsers | `read_page` |
| Perception — vision | `screenshot` `parse_screen` `recall_targets` |
| Perception — CDP *(opt-in)* | `browser_targets` `browser_console` `browser_network` |
| Action — input | `invoke` `set_text` `set_value` `click` `click_at` `type_text` `press_keys` `navigate` |
| Action — credentials | `fill_credential` |
| Action — console | `console_open` `console_write` `console_read` `console_resize` `console_close` `console_list` |
| Action — CDP *(opt-in)* | `browser_evaluate` |

The two opt-in rows need `TELEKINESIS_CDP=1`; without it those tools are not
registered at all. Every subcommand, flag and environment variable is in
[docs/CLI.md](docs/CLI.md).

## Platform backends

| OS | Perception | Actions | Status |
|---|---|---|---|
| Linux | AT-SPI over D-Bus (Tmds.DBus.Protocol) — list, tree, find, states, bounds, text | AT-SPI Action/EditableText/Value → uinput fallback | perception + actions **validated live** on Ubuntu 26.04 / XFCE ([notes](docs/RUNNING-ON-LINUX.md)); focus/events next |
| Windows | UI Automation (managed UIA client) — list, tree, find, states, bounds, text | UIA Invoke/Value/Toggle/RangeValue → SendInput fallback | perception + actions + events **validated live** ([notes](docs/RUNNING-ON-WINDOWS.md)) |
| macOS | AXAPI (`AXUIElement`) — list, tree, find, states, bounds, text | AXPress / AXSetAttributeValue → CGEvent fallback | implemented and wired in; awaiting live validation ([notes](docs/RUNNING-ON-MACOS.md)) |

> Linux native actions (AT-SPI `Action`/`EditableText`) are validated against real
> apps and need no `/dev/uinput`; the uinput **injection fallback** is what still
> wants a desktop session with `/dev/uinput` access to exercise. See
> [docs/RUNNING-ON-LINUX.md](docs/RUNNING-ON-LINUX.md#whats-proven-working).

All backends implement `IAccessibilityBackend` from `Telekinesis.Abstractions`, with a
normalized role/state vocabulary (UIA-modeled); the native role is always preserved in
`NativeRole` for when the abstraction leaks.

Uno Platform apps on Linux become visible to Telekinesis via
[uno-atspi-bridge](https://github.com/egarim/uno-atspi-bridge), which publishes Uno's
`AutomationPeer` tree onto the accessibility bus.

## Samples — real apps, driven live

Three Avalonia stress-test apps live in [`samples/`](samples/), each with a recorded
session of Telekinesis driving it (all native patterns, verified by read-back):

| | | |
|:---:|:---:|:---:|
| [![Pong Wars](docs/media/pongwars-demo.png)](samples/PongWars) | [![Whack-a-Mole](docs/media/whackamole-demo.png)](samples/WhackAMole) | [![Form Gauntlet](docs/media/formgauntlet-demo.png)](samples/FormGauntlet) |
| [**PongWars**](samples/PongWars) — drive the controls around an a11y-opaque canvas | [**WhackAMole**](samples/WhackAMole) — reaction benchmark: 46/0 hits, avg **110 ms**, best **27 ms** | [**FormGauntlet**](samples/FormGauntlet) — fill → rejected → read the errors → accepted |

## Scripted demos, CI, and remote use

`telekinesis run demos/<scenario>.json --enable-actions` executes a self-verifying
scripted demo with caption output — captions, bindings, assertions and the file format
are in [docs/SCENARIOS.md](docs/SCENARIOS.md); `telekinesis assert` gives
shell scripts a 0/1 exit probe for UI conditions. The **headless one-shot CLI**
(`telekinesis apps|tree|find|read|focused|snapshot|launch|click|click-at|invoke|set-text|type|press`)
makes every perception and action a single JSON-printing process — drive a desktop over
plain SSH with no MCP client. On Windows the CLI handles the session-0 trap itself:
`launch` starts GUI apps in the logged-on user's console session, and every other
**one-shot verb** (plus `assert`) auto-relays there transparently — `probe`, `repl`,
`run` and `pilot` do *not* relay and must be run from the console session
([docs/HEADLESS-CLI.md](docs/HEADLESS-CLI.md)). For remote clients,
`telekinesis serve` speaks MCP over HTTP on localhost — read-only unless started
with `--enable-actions`, with every action audit-logged to a file. Deployment posture
and the credential-handoff rule (`fill_credential` — secrets never pass through the
model) are in [docs/REMOTE.md](docs/REMOTE.md).

**Every subcommand, flag and environment variable** — including `probe`, `repl`,
`pilot` and the argument-dispatch order — is listed in
[docs/CLI.md](docs/CLI.md).

## Setup

Run `telekinesis doctor` to diagnose your environment and `telekinesis setup` for the
platform steps (Linux udev rule for `/dev/uinput`, enabling the a11y bus, macOS
Accessibility permission).

## License

**Dual-licensed.** Use it under [AGPL-3.0](LICENSE) for free — including commercially —
as long as you share source per the AGPL. Embedding it in a proprietary product or
closed service instead? Get a [commercial license](COMMERCIAL.md):
joche.ojeda@bitframeworks.com. (0.1.0 remains MIT; 0.2.0 remains FSL-1.1-MIT.)

## Security

This is total-machine-control tooling. Run it only for agents you trust, prefer
`--read-only` when actions aren't needed, and never expose the server on an open port —
keep it on stdio or behind authenticated tunnels.

The optional CDP browser tier has no authentication of its own — reachability of the
debugging port *is* authorization — so it is off unless `TELEKINESIS_CDP=1`, binds
loopback only, and returns metadata and scrubbed text: headers, request and response
bodies, cookies and storage are never read out of the protocol at all, and free text is
scrubbed of known secret shapes on a best-effort basis. `browser_evaluate` is an action, not a read,
because JavaScript in a logged-in page is the user's whole account.

