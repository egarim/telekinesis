# Scenario files — `telekinesis run`

A scenario is a JSON file of steps that Telekinesis executes against a live
desktop, narrating as it goes and stopping at the first failure with a nonzero
exit. It is the scripted-demo format and the closest thing the project has to an
end-to-end UI test.

```
telekinesis run demos/calc-add.json --enable-actions
```

Actions are gated the same way as everywhere else: if any step (or any `pre`
sub-step) uses an action tool, the run is **refused without
`--enable-actions`** — before it connects to anything.

## File shape

```jsonc
{
  "name": "calc-add",
  "narration": "printed once under the name",
  "notes": "free text, never executed — where to record what was validated and how",
  "steps": [ /* … */ ]
}
```

Comments and trailing commas are allowed: the parser runs with
`JsonCommentHandling.Skip` and `AllowTrailingCommas`, so a scenario can be
annotated inline.

Only `steps` is required. `name` is printed at the start and in the final
`all N step(s) passed` line.

## Steps

| Key | Meaning |
|---|---|
| `say` | caption, printed before the step runs — this is what makes a recording narrate itself |
| `tool` | the tool to call (see the table below) |
| `args` | object of tool arguments; string values get `{{binding}}` substitution |
| `pre` | array of setup sub-steps run first, quietly — normally the `find_elements` that locates the target |
| `bind` | store this step's result under a name for later steps |
| `assert` | a condition evaluated after the step; failing it fails the run |
| `expect` | informational note; not evaluated |

A step may have no `tool` at all — a `pre` + `assert` step is valid.

### Bindings and substitution

`bind` stores the result. If the result is an **array** (as `find_elements`
returns), the **first element** is bound, and binding an empty array is an
error — so a scenario fails loudly at the step that found nothing rather than
three steps later with a null reference.

Any string argument may contain `{{binding.path}}`, resolved by walking the
stored JSON:

```jsonc
{ "tool": "find_elements",
  "args": { "role": "Button", "nameContains": "Seven" },
  "bind": "seven" },

{ "tool": "invoke",
  "args": { "elementId": "{{seven.Ref.Id}}", "applicationId": "{{seven.Ref.ApplicationId}}" } }
```

An unknown binding name or a path that resolves to null is an error, not an
empty string.

### Assertions

Three operators, and either side may be a binding path or a `'quoted literal'`:

```
"assert": "display.Name contains '14'"
"assert": "after.Ref.Id == before.Ref.Id"
"assert": "title.Name != 'Untitled'"
```

`==` and `!=` are ordinal string comparisons; `contains` is case-insensitive.
Anything else throws.

### Automatic result checks

You do not have to assert that an action worked. The runner fails the step when:

- an action tool returns `Success: false` (the error text is reported), or
- `assert_element` returns `Ok: false` (reported with the time waited), or
- a `bind` on an empty array finds nothing to bind.

## Tools available in a scenario

Perception — always allowed:

`find_elements`, `read_page`, `read_element`, `get_focused`, `wait_for`,
`highlight`, `assert_element`, `recall_targets`

Actions — the whole run needs `--enable-actions`:

`invoke`, `set_text`, `set_value`, `click`, `type_text`, `press_keys`,
`click_at`, `fill_credential`, `navigate`

Argument names match the MCP tools. Defaults worth knowing: `wait_for` uses
`timeoutMs` 2000, `assert_element` uses 3000, `fill_credential` defaults `field`
to `password`.

## Output

Each step prints its caption, then a one-line result — `N match(es)` for a
find, `success path=NativeAction` or `path=InputInjection` for an action (so a
recording shows whether the native accessibility action or the input-injection
fallback ran), `ok in N ms` for an assert.

Exit `0` when every step passes, `1` on the first failure, `2` for a missing or
malformed file or a refused action.

## Shipped scenarios

| File | What it does | Validated on |
|---|---|---|
| [`calc-add.json`](../demos/calc-add.json) | computes 7+7 on Windows Calculator by button name, native invoke only, verifies by reading the display back | Windows 11 (UIA) |
| [`blog-navigate.json`](../demos/blog-navigate.json) | navigates a real blog: find a post link, native invoke, verify by title, Back, verify home | Windows 11 + Edge |
| [`thunar-navigate.json`](../demos/thunar-navigate.json) | drives the Thunar file manager | Linux (AT-SPI) |
| [`thunar-fill-location.json`](../demos/thunar-fill-location.json) | fills Thunar's location bar | Linux (AT-SPI) |
| [`cross-app-copy.json`](../demos/cross-app-copy.json) | reads from one app, acts in another | needs source + destination windows open |
| [`fill-out-contact.json`](../demos/fill-out-contact.json) | fills a real GUI form | needs a form app open (GNOME Contacts) |

Run `telekinesis doctor` first — on Linux it confirms the accessibility bus and
`/dev/uinput` are ready.
