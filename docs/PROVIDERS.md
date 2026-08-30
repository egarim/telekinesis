# Provider plugins — app-matched fidelity behind one interface

Telekinesis resolves every request down a ladder: native accessibility action →
input injection → vision tier. The provider registry formalizes the top of that
ladder: **plugins claim specific applications and wrap the OS backend with
higher-fidelity perception or action for them**, while the agent keeps talking
to one unified `find_elements`/`invoke` surface.

## Provider plugin vs MCP composition

Use **MCP composition** — connecting Telekinesis *and* a dedicated second
server, picking per step — when the goal is just "add a browser tool" or any
independent capability. That needs no plugin format at all.

Write a **provider plugin** when you want one interface that *transparently*
uses the best available provider per app: same tools, same element model, the
upgrade invisible to the agent.

## How resolution works

`BackendProvider.GetForAppAsync(applicationId)` asks the registry: the
highest-priority plugin whose `Handles(app)` claims the application wraps the
base backend (cached per app); no claim → base backend unchanged. Claiming sees
the application id, process id, and process name.

## Built-in providers

- **browser** (priority 10) — claims browser processes (`msedge`, `chrome`,
  `firefox`, …). Upgrades the *default* `find_elements` scope: page content is
  searched first, then browser chrome, so same-named browser controls never
  shadow page links. Explicit `scope: page|chrome` is honored untouched.
  ([docs/BROWSERS.md](BROWSERS.md))
- **vision-fallback** (priority min) — the vision tier expressed as a plugin.
  It claims no application (pixels are an explicit last resort, not a
  transparent upgrade) and contributes the vision tools — `screenshot`,
  `parse_screen`, `recall_targets` — to the perception set.
  ([docs/VISION.md](VISION.md))

## Writing a plugin

Implement `Telekinesis.Abstractions.IProviderPlugin`:

```csharp
public sealed class MyProvider : IProviderPlugin
{
    public string Name => "my-app";
    public int Priority => 20;                       // higher wins
    public bool Handles(ApplicationInfo app) => app.Name == "MyApp";
    public IAccessibilityBackend Wrap(IAccessibilityBackend baseBackend, ApplicationInfo app)
        => new MyBackend(baseBackend);               // subclass DelegatingAccessibilityBackend
    public IEnumerable<Type> ToolTypes => [];        // optional extra MCP tools
}
```

`DelegatingAccessibilityBackend` forwards every member to the wrapped backend,
so override only what you upgrade. In-tree providers register in
`ProviderRegistry.Load()`.

## External plugins — security posture

This is total-machine-control tooling (README §Security): **an external plugin
has the same power as the server itself.**

- **Explicit opt-in only.** There is no directory scanning and no auto-load.
  External assemblies load only when listed in `plugins.json` next to the audit
  log (`$XDG_STATE_HOME/telekinesis/`, Windows
  `%LOCALAPPDATA%\Telekinesis\state\telekinesis\`):

  ```json
  { "plugins": [ { "path": "C:\\plugins\\MyProvider.dll", "enabled": true } ] }
  ```

- **`doctor` discloses everything**: every loaded provider is listed, and
  external ones are flagged `EXTERNAL, unsigned` with their origin path.
- **Tool contribution is gated**: tools from external plugins are only exposed
  when actions are enabled (`--enable-actions` / not `--read-only`); built-in
  providers' tools load with the perception set.
- A misbehaving plugin never breaks resolution — a throwing `Handles`/`Wrap`
  is skipped and the base backend serves the app.

## Non-goals

Not an extension marketplace — the surface stays claim + wrap + optional tools.
A Chromium DevTools Protocol provider is the canonical *future* plugin (deep
DOM/JS access without polluting the OS-agnostic core); the a11y tree remains
the primary browser path.
