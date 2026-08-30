# Driving browsers through the accessibility tree

Browsers publish the entire DOM into the platform accessibility tree — that is
how screen readers work — so the web, the surface every pixel agent struggles
with, is first-class for Telekinesis: links are `[Link]` elements with real
names and a native `invoke`, form fields are `[Edit]`s that take `set_text`.
No browser driver, no CDP, no DOM scraping. Validated live on Windows 11 +
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
- **Deep DOM/JS/network access** is deliberately out of scope for the core —
  that is a browser-specific concern (CDP) and belongs in an optional provider
  (see issue #21), not the OS-agnostic a11y path.

## Worked example

[`demos/blog-navigate.json`](../demos/blog-navigate.json) navigates a real
blog: assert home → find a post link by name → native invoke → verify arrival
by title → Back → verify home. Run it:

```
telekinesis run demos/blog-navigate.json --enable-actions
```
