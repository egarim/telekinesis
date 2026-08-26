# Telekinesis

**Move things without touching them.** Telekinesis is an MCP server that lets AI agents
see and control the desktop through the platform accessibility APIs — the same channel
screen readers use. Semantic perception ("the Save button") instead of pixel-guessing,
at a fraction of the cost of screenshot-driven computer use.

```
dotnet tool install -g Telekinesis
```

MCP client config:

```json
{ "mcpServers": { "telekinesis": { "command": "telekinesis" } } }
```

## Modes

- **Clairvoyant mode** (`telekinesis --read-only`) — perception only: `list_applications`,
  `get_tree`, `find_elements`, `read_element`, `get_focused`. Safe to expose; needs no
  input permissions. Password-field content is never exposed.
- **Telekinesis mode** (default) — adds actions: `invoke`, `set_text`, `click`,
  `type_text`, `press_keys`. Each action tries the native accessibility action first
  and falls back to OS input injection, reporting which path ran. Every action is
  audit-logged.

## Platform backends

| OS | Perception | Actions | Status |
|---|---|---|---|
| Linux | AT-SPI over D-Bus (Tmds.DBus.Protocol) — list, tree, find, states, bounds, text | AT-SPI Action/EditableText/Value → uinput fallback | perception + actions done; focus/events next |
| Windows | UI Automation | UIA patterns → SendInput | planned |
| macOS | AXAPI | AXPress → CGEvent | planned |

> Actions are implemented against the spec but await runtime testing on a Linux
> desktop session with `/dev/uinput` access.

All backends implement `IAccessibilityBackend` from `Telekinesis.Abstractions`, with a
normalized role/state vocabulary (UIA-modeled); the native role is always preserved in
`NativeRole` for when the abstraction leaks.

Uno Platform apps on Linux become visible to Telekinesis via
[uno-atspi-bridge](https://github.com/egarim/uno-atspi-bridge), which publishes Uno's
`AutomationPeer` tree onto the accessibility bus.

## Setup

Run `telekinesis doctor` to diagnose your environment and `telekinesis setup` for the
platform steps (Linux udev rule for `/dev/uinput`, enabling the a11y bus, macOS
Accessibility permission).

## Security

This is total-machine-control tooling. Run it only for agents you trust, prefer
`--read-only` when actions aren't needed, and never expose the server on an open port —
keep it on stdio or behind authenticated tunnels.
