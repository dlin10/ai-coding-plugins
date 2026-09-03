# Cache Detective report

Generated: `{{timestamp_utc}}`  
Repository: `{{repository_root}}`  
Solutions requested: {{solutions_requested}}  
Solutions indexed: {{solutions_indexed}}

## Summary

- Visible findings: {{visible_findings}}
- Suppressed findings: {{suppressed_findings}}
- Confirmed: {{confirmed_findings}}
- Likely: {{likely_findings}}
- Needs checking: {{needs_checking}}
- Solutions with load or indexing failures: {{failed_solutions}}

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
