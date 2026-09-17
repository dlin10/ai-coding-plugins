Return your result through the structured output tool.

Your Bash and PowerShell tools take a `timeout` of up to 1800000 ms (30 minutes); pass it for any
command that may run longer than two minutes. When a tool refuses a foreground `sleep` and suggests
`run_in_background` or `Monitor`, that advice is for a session that continues — yours does not, so
wait for the command itself in the foreground instead.
