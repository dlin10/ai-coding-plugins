# Keep pending-plan trust checks split between the runtime and host skill

Pending plans are untrusted transport artifacts rather than approval evidence. The runtime validates the persisted schema, host, workspace, bounded content, and materialized transaction hashes, while the host skill owns collaboration-mode sequencing and Cursor's advisory review flow because neither host exposes authoritative runtime evidence for those decisions. Cursor native edits after chat review are accepted deliberately: the transaction binds replay and materialized bytes, not identity with the reviewed draft.
