# Form Gauntlet — fill, get rejected, read the errors, fix, resubmit

An insurance-claim form with real validation, driven end-to-end by name through
`telekinesis repl`. The interesting part is not the filling — it's the **recovery loop**:
the first submission is (deliberately) rejected, and the agent *reads the validation
errors through UIA*, fixes exactly the failing fields, and resubmits to acceptance.

```
dotnet run --project samples/FormGauntlet

telekinesis repl --enable-actions
> settext pid:<id> "Full name" Maria Lopez
> settext pid:<id> "Email address" maria.lopez#example.com     # typo, on purpose
> settext pid:<id> "Policy number" POL-77301
> ...
> expand pid:<id> "Incident type selector"
> click pid:<id> Collision
> collapse pid:<id> "Incident type selector"
> click pid:<id> "Submit claim"
> find pid:<id> Rejected            → "Rejected (attempt 1) — 2 error(s)"
> find pid:<id> "not a valid email" → the exact failing fields, as text
> settext pid:<id> "Email address" maria.lopez@example.com
> click pid:<id> "Accuracy consent"
> click pid:<id> "Submit claim"
> find pid:<id> Accepted            → "Accepted on attempt 2"
```

Every action lands `path=NativeAction` (ValuePattern for text, ExpandCollapse +
SelectionItem for the combo, Toggle for the consent checkbox, Invoke for submit), each in
~80–200 ms warm. Full round — 9 fields, submit, read errors, fix 2 fields, resubmit,
confirm — in well under a minute, most of it deliberate pacing for the camera.

This sample earned its keep: building it surfaced two real disambiguation bugs
(a field's *label* and an *error message* both match the field's name — `settext` now
prefers Edit/Document roles and `click` prefers Button/CheckBox/ListItem/MenuItem before
falling back to any match).

Video of the full run: [formgauntlet-uia-demo.mp4](formgauntlet-uia-demo.mp4)
