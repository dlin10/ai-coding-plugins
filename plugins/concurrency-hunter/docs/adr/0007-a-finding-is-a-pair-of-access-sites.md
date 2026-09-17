# A finding is a pair of access sites, and its roots are occurrences

Three actions call one helper on a singleton, and the helper writes a field. The write is one
access site, reached from three execution roots. Phase 2a reported it as six findings in one group:
every unordered pair of roots, the self-pairs included, because a finding's identity carried the
two roots beside the two access sites. The demo's expectation format never did: an expectation
names the rule, the resource and the unordered pair of accesses, and roots are not part of it. The
report said six things where a reader sees one, and the number grew with every new caller.

TD-106 also put the execution roots into the fingerprint. From phase 6 a fingerprint is what a
repository's suppression file names, so a fingerprint that changes when a fourth action starts
calling the helper is a suppression that silently stops applying after an unrelated edit.

A finding is the rule, the resource and the unordered pair of access sites: the containing member,
the operation and the source position of each. The execution roots that reach the two sites, with
the call path from each root, are the finding's occurrences; the report shows a few as
representative locations and counts them all. The fingerprint hashes the rule, the containing
symbols of both sites, the resource's shape without its context, the operation kinds and roles and
the protection result kind. It survives moved lines, renamed locals and new callers. It changes
when the rule, a site, the resource or the protection result changes, which is a change of cause.
A group's fingerprint is the same hash over its rule and resource.

Rejected: keep the roots in the identity and collapse them only in the report. The narrative cites
finding ids, the validator checks every cited id, and `findings.json` is the structured contract;
three accounts of what a finding is would drift. Rejected: keep the roots in the fingerprint as
TD-106 said, sorted. It makes a suppression a statement about who calls the helper today rather
than about the race, and no user reads it that way.

The consequence to hold: `findings.json` gains occurrences on every finding and loses one finding
per root pair; a suppression written in phase 6 against a fingerprint keeps hiding the finding when
callers are added or code moves, and stops when the cause changes. The confidence of a finding is
the highest over its occurrences, so an occurrence whose overlap is only conjectured never lowers a
proved one.
