# Separate Worker telemetry from the Run log

Worker usage records live in the Run's typed `telemetry.json`, while operational events remain in
`forge.log`. The telemetry file is an indented JSON array for direct human reading. Adding a record
is a locally serialized read–append–rewrite that atomically replaces the file; the expected record
count makes that linear rewrite preferable to the harder-to-read JSON Lines alternative. Keeping
the records separate avoids forcing numeric telemetry into the Run log's string-valued field
format and gives analysis tools a stable machine-readable input; the two files do not duplicate
token data, and telemetry failures never change a Worker outcome. An existing telemetry file that
is not the expected JSON array is left untouched rather than repaired or overwritten; the safe
write-failure event in `forge.log` makes the problem visible without discarding collected data.
The per-file gate coordinates writers within one Plan Forge process only; the file follows the
existing Run-state contract and does not add cross-process coordination for concurrent mutation of
the same Run.
