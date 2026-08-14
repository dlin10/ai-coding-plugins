# OpenAI App Server reviewer prompt fragment

Append the complete [Roslyn-first reviewer contract](roslyn-first-review.md) to
every fresh OpenAI App Server reviewer prompt. The read-only thread must use
available tool discovery before C# semantic inspection, verify the intended
solution from an absolute path plus returned compilation identity, and emit
exactly one `ROSLYN: USED`, `ROSLYN: FALLBACK`, or
`ROSLYN: NOT_APPLICABLE` marker. Missing or inconclusive Roslyn capability is a
nonblocking audited fallback, not an automatic partial-coverage result.
