<!--
Chain line forms. A chain is one linear top-to-bottom path and every line names where it came from.
Code has a file and a line; a database object has neither, so it carries its own name instead:

    writes    Pricing.API/Pricing/DiscountsController.cs:44   ApplyDiscount calls dbo.ApplyDiscount
    writes    shop.dbo.ApplyDiscount              -> dbo.Discounts
    fires     shop.dbo.trg_Discounts_Audit        on dbo.Discounts (insert, update)
    writes    shop.dbo.trg_Discounts_Audit        -> dbo.PriceHistory
    reads     shop.dbo.vw_ProductCard             -> dbo.PriceHistory
    publishes Catalog.API/CatalogController.cs:44 -> Contracts.PriceChanged
    consumes  Notifications/PriceChangedConsumer.cs:18 <- Contracts.PriceChanged
    serves    Notifications/DigestController.cs:31 -> Catalog.API/CatalogController.cs:76
    reads     Catalog.API/Catalog/ProductsController.cs:12    product card
    caches    Catalog.API/Catalog/ProductsController.cs:14    product:{id} (no TTL)

Use `<database>.<schema>.<object>` for a procedure, a trigger, or a view. Never invent a file and a
line for one, and never leave a line without either form.

The subject of a finding is the handler at the head of the chain, which the tool reports directly.
It is not always the line that performed the write: for a hidden write the write is done by a
procedure or a trigger, and the handler is the one that has to carry the invalidation.
-->
# Cache Detective report

Generated: `{{timestamp_utc}}`  
Repository: `{{repository_root}}`  
Solutions requested: {{solutions_requested}}  
Solutions indexed: {{solutions_indexed}}  
Database: {{database_status}}

## Summary

- Visible findings: {{visible_findings}}
- Suppressed findings: {{suppressed_findings}}
- Confirmed: {{confirmed_findings}}
- Likely: {{likely_findings}}
- Needs checking: {{needs_checking}}
- Solutions with load or indexing failures: {{failed_solutions}}
- Database objects indexed: {{database_objects}}

## Confirmed findings

{{#confirmed}}
### {{rule}} — {{subject}}

- Finding: `{{finding_id}}`
- Confidence: `confirmed`
- Solution: `{{solution}}`
- TTL: {{ttl_seconds}}
- Budget: {{budget_seconds}}

Chain:

{{linear_chain}}

{{#invalidationSearchedIn}}
invalidation: not found in {{invalidationSearchedIn}}
{{/invalidationSearchedIn}}

{{/confirmed}}
{{^confirmed}}None.{{/confirmed}}

## Likely findings

{{#likely}}
### {{rule}} — {{subject}}

- Finding: `{{finding_id}}`
- Confidence: `likely`
- Solution: `{{solution}}`
- TTL: {{ttl_seconds}}
- Budget: {{budget_seconds}}

Chain:

{{linear_chain}}

Assumption: {{annotation_fragment}}
Reason: {{reason}}

{{#invalidationSearchedIn}}
invalidation: not found in {{invalidationSearchedIn}}
{{/invalidationSearchedIn}}

{{/likely}}
{{^likely}}None.{{/likely}}

## Needs checking

{{#needs_checking}}
### {{title}}

- Kind: `{{kind}}`
- Solution: `{{solution}}`
- Reason: {{reason}}

Chain or diagnostic:

{{linear_chain_or_diagnostic}}

{{/needs_checking}}
{{^needs_checking}}None.{{/needs_checking}}

## Unresolved

<!--
One row per unresolved item, with the site in whichever form it has: `file:line` for a code site,
`<database>.<object>` for a database one. Keep the two derived reasons distinct — they are the two
ways a chain can stop at a stored procedure, and they call for different actions:

  - "the dependencies of <procedure> are unknown: no database is indexed" — configure a database and
    scan again; every chain through a procedure is incomplete until then.
  - "<procedure> is not in the catalogue of <database>" — the code calls a procedure that database
    does not have. Nothing further can be learned about it from this database.
-->

{{#unresolved}}
- `{{kind}}` at {{site}} — {{reason}}
{{/unresolved}}
{{^unresolved}}None.{{/unresolved}}

## Annotations this run

{{#annotations}}
- `{{id}}` `{{kind}}`: {{resolution}}{{#note}} — {{note}}{{/note}}
{{/annotations}}
{{^annotations}}None.{{/annotations}}
