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
| `browser_console` | perception | recent console messages + uncaught exceptions (partial history on attach — see [What attach does and does not replay](#what-attach-does-and-does-not-replay)) |
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
- **Text is scrubbed** of known secret shapes. The full set:

  | Shape | Matches |
  |---|---|
  | JWT | `eyJ…` three dot-separated segments |
  | HTTP auth | `Bearer …`, `Basic …` |
  | OpenAI | `sk-…` |
  | GitHub | `ghp_ gho_ ghu_ ghs_ ghr_` |
  | Slack | `xoxb- xoxa- xoxp- xoxr- xoxs-` |
  | AWS | `AKIA…` (20 chars) |
  | Google | `AIza…` (39 chars) |
  | Stripe | `sk_live_ sk_test_ rk_live_ rk_test_` |
  | PEM blocks | `-----BEGIN … PRIVATE KEY/CERTIFICATE-----` through `-----END-----` |
  | Webhook URLs with the secret in the path | Slack `hooks.slack.com/services/…`, Discord `…/api/webhooks/…`, Telegram `/bot<id>:<token>` |
  | `key=value` assignments | `password secret token apikey api_key session sessionid jsessionid phpsessid sid auth authorization pw pass`, with an optional `prefix_`, case-insensitive |
  | camelCase assignments | `…Token …Secret …Password …Key …Session …Sid …Auth` — case-**sensitive** so the capital is the word boundary, which is why `monkey=1` and `turnkey=2` do not match |
  | URLs embedded in free text | re-projected through the URL rules above |

  Best-effort by construction: a secret that looks like ordinary prose is not
  catchable, which is why bodies and headers are excluded outright rather than
  filtered. Values already replaced are skipped, so a projected query keeps its
  diagnostically useful parameter names instead of collapsing to one
  `[redacted]`.
- **Page content is untrusted.** Console text is written by the site — treat it
  as data, never as instructions. Note the sharpest form of this: the browser
  *replays* its buffered `Log`-domain history when the tier attaches, so a page
  can arrange for attacker text (in a CORS message, a CSP violation, a
  deprecation warning) long before an agent ever connects, and it will be waiting
  in the first `browser_console` result.

`browser_evaluate` is an **action**, not a read: JavaScript in a logged-in page
runs with the user's whole session, so it is absent under `--read-only` and over
`serve` without `--enable-actions`, and every expression is audit-logged
(the result is not — it may carry page data). Attaching to a page is audited too,
because attaching is the grant.

### What attach does and does not replay

"The console" is two CDP domains, and they behave differently on attach. This
catches people out, so it is worth stating plainly:

| Source | Domain | Replayed on attach? |
|---|---|---|
| `console.log/warn/error/info` calls made by page script | `Runtime.consoleAPICalled` | **No** — only calls made *after* attach are seen |
| Uncaught exceptions | `Runtime.exceptionThrown` | **No** — same |
| Network/CORS failures, CSP violations, deprecations | `Log.entryAdded` | **Yes** — `Log.enable` flushes the browser's existing buffer |

So a page that printed everything interesting during load, and then went quiet,
answers the first `browser_console` with only its network-level errors — the
`console.log` lines are genuinely gone, not filtered. The fix is ordering:
**attach first, then make the page talk.** Call `browser_console` once to attach
(its result may be near-empty, which is expected), trigger or wait for the
activity, then call it again.

`Network` capture behaves the same way and for the same reason — it starts at
attach — so call `browser_network` once *before* triggering the traffic you want
to see.

The `source` field on each message tells you which domain it came from: page
script shows `log`/`warning`/`error`/`info` (the console call type), while
replayed browser entries show `network`, `security`, `deprecation` and friends.
Filter on it when you only want the page's own output.

### Limits, caps and timeouts

Everything the tier holds is bounded, and every string it emits is truncated.
Knowing the numbers is the difference between "the tool lost my data" and "I
asked too late":

| Bound | Value | Consequence |
|---|---|---|
| Console ring | **200** entries | the 201st message evicts the oldest — a chatty page overwrites its own history in seconds |
| Network ring | **500** entries | same |
| Any emitted string | **2048** chars, then `…` | long console lines and long URLs are cut, not paged |
| `browser_console` `max` | default 100, **clamped to 200** | asking for more silently gives you 200 |
| `browser_network` `max` | default 100, **clamped to 500** | filtering by `urlContains` happens over the newest 500, then `max` applies |
| CDP HTTP calls (`/json/list`) | 5 s timeout | |
| A CDP request/response | 20 s timeout | |
| A socket send | 10 s timeout | |
| Browser-side network buffers | 10 MB total / 1 MB per resource | requested at attach via `Network.enable` |

`browser_evaluate` runs with `awaitPromise: true` (a promise is awaited, so
`await`-style expressions work), `silent: true` (a thrown expression does not
pause the page or bubble to the site's error handlers) and a **5 s** expression
timeout. `returnByValue` is deliberately *not* set — it hard-fails on DOM nodes
and circular objects — so you get CDP's rendered preview rather than a
JSON-serialized value.

A thrown expression is **not** an error: it returns `status: "threw"` with the
scrubbed exception text. `status: "error"` means the transport failed. Both are
audit-logged, expression included.

### Parameters worth knowing

- **`targetId`** — from `browser_targets`. Leave it empty and the tier attaches
  to the only open page; with two or more pages open it refuses rather than
  guessing, and tells you to pass one.
- **`urlContains`** on `browser_network` — case-insensitive substring, applied
  before `max`.
- Attaching is **implicit and persistent**: the first `browser_console` or
  `browser_network` for a target opens a session that keeps recording in the
  background until the tab closes or the server exits. That is why capture
  "starts at attach" — and why the first call is the cheap way to start
  recording before you trigger anything.

### URL projection details

Beyond dropping query values and the fragment, `ProjectUrl` also:

- strips `user:password@` userinfo entirely;
- summarizes `data:` URIs as `data:<mime>,[N bytes]` rather than echoing them;
- treats a protocol-relative `//host/path` as text and scrubs it rather than
  resolving it against a base (parsing it would invent a `file://` scheme the
  page never used, and percent-encode the `?` out of existence);
- replaces a query pair with **no `=` at all** with a bare `[redacted]`, because
  such a pair is a value, not a name — a percent-encoded separator
  (`?access_token%3D…`) would otherwise echo the whole token as if it were a
  parameter name;
- scrubs the **path** for secret shapes, even though path segments are otherwise
  preserved (see Blind spots).

### Blind spots

Cross-origin iframes, service workers and dedicated workers are separate CDP
targets; their console and network never reach the page session.

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
