# Everything search syntax

Read this before composing a `query` that is more than a plain name. The `query` argument is passed to Everything verbatim — nothing is escaped on your behalf, because escaping would disable the operators below.

Everything's full reference lives at <https://www.voidtools.com/support/everything/searching/>. What follows is the subset that matters for this tool, plus the parts that bite.

## Operators

| Form | Meaning |
|---|---|
| `space` | AND — `report 2024` matches names containing both |
| `\|` | OR — `*.png\|*.jpg` |
| `!term` | NOT — `config !backup` |
| `<a b>` | Grouping — `<config \| settings> !backup` |
| `"exact phrase"` | Treats the quoted run as one term |
| `*` `?` | Wildcards. `*` is any run of characters, `?` is one |

Matching is substring-based on the file name unless you anchor it, so `SKILL.md` also matches `SKILL.md.diff`.

## Filters

| Filter | Example | Notes |
|---|---|---|
| `ext:` | `ext:cs;csproj` | Semicolon-separated extension list, no dots |
| `size:` | `size:>10mb`, `size:1kb..1mb` | `kb`, `mb`, `gb` accepted |
| `dm:` | `dm:today`, `dm:lastweek`, `dm:2026` | Date modified |
| `dc:` | `dc:yesterday` | Date created |
| `file:` `folder:` | `folder:src` | Prefer the tool's `kind` argument instead |
| `parent:` | `parent:C:\Dev` | Prefer the tool's `path` argument instead |
| `regex:` | `regex:^SKILL.*\.md$` | Prefer the tool's `regex` argument instead |
| `content:` | `content:TODO` | **Not indexed** — Everything reads files to answer this, so it is slow and unbounded. Use `Grep` instead |

Where an argument on the tool covers the same ground as a filter, use the argument: `path` maps onto Everything's native `-path`, `kind` onto its attribute filters, and `regex` onto its regex mode, and all three are applied to the match total as well as to the listed rows.

## Path fragments

Including a backslash in the query makes Everything match against the full path, not just the name. That is the way to pin a folder without scoping the whole search:

```
plugins\find-files\SKILL.md
```

Use a fragment distinctive enough to identify the folder — a bare folder name will over-match.

## Escaping

Per `es.exe -help`, these characters are meaningful and must be escaped to be matched literally: `\ & | > < ^`. Escape with a leading `^`, or wrap the run in double quotes.

So `find ^| this` searches for a literal pipe, while `a | b` is an OR. If a query behaves strangely, an unescaped operator is the first thing to check.

## Sorting

The tool ranks results itself — shortest path first, or by modification date with `recent: true` — so Everything's own sort options are not exposed. Everything cannot sort by path length, so shortest-path ranking is computed locally over a window of the first 1000 matches. When the summary line reports a total above that window, the ranking is exact only within it; narrow the query if precise ranking matters.
