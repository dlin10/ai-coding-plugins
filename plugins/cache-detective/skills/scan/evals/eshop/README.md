# eShopOnContainers eval

Set `CD_ESHOP_ROOT` to the root of a current `C:\Dev\eShopOnContainers` checkout. The test reads that
checkout only; it does not configure databases or create, modify, or delete any checkout files.

To plant the optional cache case, run:

```powershell
git -C C:\Dev\eShopOnContainers apply <absolute-path-to-cache-detective>\skills\scan\evals\eshop\planted-webmvc-catalog-cache.patch
```

Verify the patch before applying it with `git -C C:\Dev\eShopOnContainers apply --check` and the same
absolute patch path. The marker `src/Web/WebMVC/.cache-detective-planted` tells the eval to assert the
planted finding; without it, the eval reports that the optional case was not applied and succeeds.

No database is configured deliberately: this workspace contains four databases while Cache Detective has
one database catalogue per workspace, and these code paths contain no stored procedures or triggers that
would benefit from a catalogue. `eventsWithoutCrossProjectConsumer` is an allowlist and remains empty;
add an exception only with its justification here.

The Ordering API recognizer is publisher-only: adding an integration event to its outbox is the
observable publish point. The later deserialised outbox replay is intentionally not treated as a
new publish because its base-typed payload cannot be recovered. Event configuration accepts a
publisher-only or consumer-only entry; at least one side is required.

The WebMVC items URL has branch-dependent filters, so its endpoint tail is intentionally unknown.
When the planted marker is present, the eval applies the listed `call` annotation in memory before
checking the planted finding; it never writes that annotation to the checkout. The separate
`catalogbrands` call has the observed statically-folded `{purchaseurl}/c/api/{v}/catalog/catalogbrands`
template. The annotated items call is observed as `{purchaseurl}/c/api/{v}/catalog/items{?}`.

`report.md` is written only after the G5 review; the builder does not create it.
