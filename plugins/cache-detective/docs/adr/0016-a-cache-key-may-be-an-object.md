# A cache key may be an object, and the recognizer says how to read it

A recognizer addresses the key by argument ordinal and nothing else, and everything downstream assumed
that argument was a string expression: an interpolation, a concatenation, a `string.Format`, a helper
returning one. Two of the four corpora do not pass strings. nopCommerce's `IStaticCacheManager` takes a
`Nop.Core.Caching.CacheKey`; Orchard's `IDynamicCacheService` takes a `CacheContext`. The recognizer
found the call sites, the folder met an object construction, fell into its default arm, and produced no
literal segment, so every one of those sites became an unresolved row. Measured on nopCommerce at
release-4.90.8, with its `IStaticCacheManager` recognizer declared: three cache operations against 121
unresolved keys, cache coverage 0.023.

The shape nopCommerce uses is not exotic, and it is not lossy. The template is a literal in a
`static CacheKey` property, `new CacheKey("Nop.category.all.{0}-{1}-{2}")` on a `Defaults` class, and
it meets its arguments in a factory, `PrepareKey` or `PrepareKeyForDefaultCache`, which is a
`string.Format` over that template. Everything the folder needs is present in the compilation: the
literal, the factory, and the argument expressions whose names become the placeholders. What is
genuinely not derivable is the argument's value, since `CreateCacheKeyParameters` collapses an entity
to its id and a list of ids to a hash, but the folder never claimed values, only names, so nothing is
lost that it ever had.

So the recognizer gains a description of the key object: the type, where the template literal comes
from (a constructor argument), and the factory methods that substitute arguments into it positionally.
The hole `{0}` becomes `{storeId}` from the argument expression, exactly as an interpolation hole does.
This is a recognizer, not a branch: nopCommerce is described, not special-cased, and a library with the
same shape is described the same way.

It is declared in `.cache-detective/workspace.json`, in a new `caches` section beside `events`. Until
now the two halves were asymmetric for no reason anyone chose: an event bus could be declared in the
config and a caching library could not, so a cache recognizer arrived only through `annotate` with kind
`cache_api` and died with the session. That asymmetry made nopCommerce unrepeatable, because every run
had to be annotated again before it measured anything. The `cache_api` annotation gains the same
fields, so the agent can still resolve one live. A recognizer from the config produces `confirmed`
edges, as `events` from the config already do; one from an annotation produces `likely`, because every
edge an annotation creates is `likely`. The schema version stays 1: a new optional property does not
invalidate a config written before it, and raising the version would invalidate all of them.

Orchard's `CacheContext` is deliberately not done here. Its grammar is a different one, not a template
with positional holes but a constructor id plus a fluent chain of `AddContext` calls, composed at run
time into the cache id followed by a hash of the sorted contexts, and it would need a second kind of
description rather than a second entry of this one. The return would also be small: in v3.0.1 the cache
id is usually a run-time string from a Razor tag-helper attribute or a Liquid filter argument, so even
a perfect reader of `CacheContext` folds a handful of sites, not hundreds. Orchard's coverage is
therefore reported with that as its measured reason, which is worth more than a recognizer that
describes a shape the corpus does not write down.

Rejected: leaving cache recognizers to `annotate` alone. It would satisfy the letter of "the key is
folded by constructor and factory" while leaving the corpus unmeasurable twice running, and it would
keep an asymmetry against `events` that nobody would be able to justify to a reader. Rejected too:
teaching the folder about `Nop.Core.Caching.CacheKey` by name. One corpus written into the analyser is
how an analyser stops being about the language and starts being about the corpus.

## What it measured

Measured on 2026-09-08 against nopCommerce at the pinned revision, with the configuration file
byte-identical between the two runs so that the analyser is the only difference: cache operations 3 to
116, unresolved keys 121 to 28, cache-site coverage 0.023 to 0.784 — the first third-party corpus to
pass the specification's 0.7 target. Orchard Core, measured the same way through the same change,
reproduces every count to the digit, which is what makes nopCommerce's movement readable as an effect
of the analyser rather than of drift.

Most of that came from a detail this record did not anticipate. The description above resolves the key
object where the call site names it, and the first measurement after it read 27 operations against 97
unresolved keys. Code review then found that a factory call reached through a local — `var key =
PrepareKey(defaults.Key, id); cache.GetAsync(key, …)` — was not recognised at all, because the walk
re-entered construction but not factory recognition. That is not one site: it is how nopCommerce is
written throughout. Recognising a factory wherever it is met, rather than only at the outermost
expression, is what took 27 to 116.

The 28 that remain are the finding, not a rounding error, and no coverage target is set anywhere in the
code. A count that appeared with them is worth recording: unresolved rows of kind `role` went from zero
to four, because a classifier with 116 operations finally has keys to classify. That does not discharge
the role-accuracy debt, which wants labelled rows rather than a count of unsettled ones — but it is the
first evidence that the debt is now payable, which it was not while every key in the corpus was an
unfolded object.
