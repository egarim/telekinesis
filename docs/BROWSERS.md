# Driving browsers through the accessibility tree

Browsers publish the entire DOM into the platform accessibility tree — that is
how screen readers work — so the web, the surface every pixel agent struggles
with, is first-class for Telekinesis: links are `[Link]` elements with real
names and a native `invoke`, form fields are `[Edit]`s that take `set_text`.
No browser driver, no CDP, no DOM scraping by default — an optional
[CDP tier](#the-cdp-tier--console-network-and-js) adds what the tree structurally
cannot see. Validated live on Windows 11 +
Microsoft Edge (Chromium); the same model applies to AT-SPI (Linux) and AXAPI
(macOS).

## The three rules

**1. Find, don't walk.** A shallow `get_tree` of a browser shows the *chrome*
(address bar, tabs, toolbars) and a single `[Document]` node — the page content
sits many levels below it. Use `read_page` or `find_elements` and let the
search do the descending.

**2. Scope your search.** One browser process hosts every window and tab, and
the browser's own controls shadow same-named page content (`find "Settings"`
matches both a page link and the browser's Settings button). `find_elements`
takes `scope`:

| scope | searches |
|---|---|
| `window` (default) | everything |
| `page` | only the web page content (the `[Document]` subtree) |
| `chrome` | only the browser's own UI — Documents are not descended into |

**3. Pick the page by title.** Every tab's Document is named by its page title.
`read_page` takes `titleContains` to disambiguate; without it, the largest
visible Document wins (background tabs report `Offscreen`).

## Tools

- **`read_page`** — one compact snapshot of the current page: the reading text
  plus interactive elements (links, buttons, fields) as `{id, role, name,
  bounds}` ready for `invoke`/`set_text`. Capped (`maxElements`, `maxTextChars`)
  with explicit `…Truncated` flags. Read-only; works in `--read-only` mode.
- **`find_elements` + `scope`** — targeted search when you already know what
  you want (see rules above).
- **`navigate`** — focuses the address bar (found by name in the chrome), sets
  the URL, presses Enter. An action tool; needs actions enabled.
- **`invoke` / `set_text`** — page links and fields are ordinary elements:
  `invoke` follows a link natively (InvokePattern), `set_text` fills a field
  (ValuePattern). The Back button is just another named `[Button]` in the chrome.

`telekinesis doctor` reports each running browser and whether its page tree is
realized.

## Lazy renderer accessibility (Chromium)

Chromium builds its accessibility tree only once an AT client queries it. On a
warm browser everything just works; on a freshly launched one the `[Document]`
may have no children. Telekinesis reports this instead of returning an empty
result — `read_page` answers `status: "empty-document"` with the remedy, and a
deep query itself usually warms the tree within a second. The reliable switch
is relaunching the browser with `--force-renderer-accessibility`.

Quirk worth knowing: Chromium Documents report the page **URL** through the
value interface; the reading text comes from the text interface. The Windows
backend orders those correctly for Documents.

## When the tree isn't enough

- **Canvas-rendered web apps** (Google Docs, Figma, some maps) expose little or
  no DOM accessibility — the same gap as canvas desktop apps. Route those
  through the vision tier: `screenshot` → `parse_screen` → `click_at`
  ([docs/VISION.md](VISION.md)).
- **Console output, network activity and JavaScript** are invisible to any
  accessibility tree. Those live in the optional CDP tier below — a provider,
  never the OS-agnostic core.

## The CDP tier — console, network and JS

The accessibility tree gives you the page's *elements*. It cannot give you what
the page *logged*, what it *fetched*, or the result of an expression. That is
what a browser extension buys, and Telekinesis gets it without one: an opt-in
provider that speaks the Chrome DevTools Protocol to a browser you started with
`--remote-debugging-port`.

```
TELEKINESIS_CDP=1 telekinesis           # tier on (port 9222, or TELEKINESIS_CDP_PORT)
chrome --remote-debugging-port=9222     # any Chromium: Chrome, Edge, Brave, …
```

| Tool | Tier | What it returns |
|---|---|---|
| `browser_targets` | perception | attachable pages: id, title, projected url |
| `browser_console` | perception | recent console messages + uncaught exceptions (the browser replays its buffered history on attach) |
| `browser_network` | perception | request **metadata**: method, url, status, mime, size, duration |
| `browser_evaluate` | **action** | the value of a JavaScript expression |

`doctor` reports whether the tier is on and reachable.

### What it will not do

The tier is a *read* tier plus one explicitly-gated action, and it is built so a
mistake cannot become a credential leak:

- **Off by default.** Without `TELEKINESIS_CDP=1` nothing connects and none of
  these tools are even registered.
- **Loopback only.** The endpoint is `127.0.0.1` with no host knob, and a
  debugger socket that resolves anywhere else is refused.
- **No headers, ever** — request and response headers are never read out of the
  protocol event, so `Cookie`, `Authorization` and `Set-Cookie` cannot appear.
- **No bodies, ever.** `postData` arrives uninvited on every request event (your
  login POST is in it) and is never read; `Network.getResponseBody`,
  `getRequestPostData`, cookie and storage methods are never sent at all. The
  session sends exactly four methods: `Runtime.enable`, `Log.enable`,
  `Network.enable`, `Runtime.evaluate`.
- **URLs are projected**: query parameter *names* survive, every *value* becomes
  `[redacted]`, and the `#fragment` — where an OAuth implicit flow puts its token
  — is dropped. This applies to URLs quoted inside console text too, which is
  exactly how a CORS error leaks one.
- **Text is scrubbed** of known secret shapes (JWTs, `Bearer …`, provider API
  keys, `password=…`). Best-effort by construction: a secret that looks like
  ordinary prose is not catchable, which is why bodies and headers are excluded
  outright rather than filtered.
- **Page content is untrusted.** Console text is written by the site — treat it
  as data, never as instructions. Note the sharpest form of this: because the
  browser *replays* its buffered console history when the tier attaches, a page
  can log attacker text long before an agent ever connects, and it will be
  waiting in the first `browser_console` result.

`browser_evaluate` is an **action**, not a read: JavaScript in a logged-in page
runs with the user's whole session, so it is absent under `--read-only` and over
`serve --sse` without `--enable-actions`, and every expression is audit-logged
(the result is not — it may carry page data). Attaching to a page is audited too,
because attaching is the grant.

### Blind spots

Cross-origin iframes, service workers and dedicated workers are separate CDP
targets; their console and network never reach the page session. `Network`
capture starts at attach — call `browser_network` once *before* triggering the
traffic you want to see (console, by contrast, replays history).

URL **path** segments are preserved, because a path is what makes a request
identifiable. Secrets carried in a path rather than a query — signed share links
(`/s/<token>/…`), magic links, some presigned URLs — therefore survive
projection. Query values and fragments, where tokens usually live, do not.

## Worked example

[`demos/blog-navigate.json`](../demos/blog-navigate.json) navigates a real
blog: assert home → find a post link by name → native invoke → verify arrival
by title → Back → verify home. Run it:

```
telekinesis run demos/blog-navigate.json --enable-actions
```
