# Concurrency Hunter report

| Field | Value |
| --- | --- |
| Status | CompleteWithFindings |
| Reasons | none |
| Target | C:\Dev\CodexPlugins\plugins\concurrency-hunter\demo\Demo.slnx |
| Run | 20260914-081412-fce069 |
| Started | 2026-09-14T08:14:12.7611360+00:00 |
| Duration | 62.7 s |
| Version | concurrency-hunter 0.1.0 |

### Executive summary
A High-confidence `DCA1001` group identifies unprotected shared static `_lastVisitor` state: reads can overlap writes and concurrent writes can replace one another depending on request timing [E:F1.R] [E:F1.O] [E:F1.S] [E:F2.R] [E:F2.O] [E:F2.S].

### Coverage
- Projects loaded: 3 of 3
- Missing projects: none
- Execution roots: 26 public actions of ControllerBase descendants
- Not analyzed in this version: calls made from an action, dependency-injected state, spawned work, locks other than lock on a static field, path feasibility

### High findings
#### G1 · DCA1001 · _lastVisitor on static:Demo.Web.Cases.StaticFieldUnlockedReadWrite.LastVisitorController (2 findings)
##### What can be lost
A read of `_lastVisitor` can see a value before or after a concurrent update, so the result depends on request timing. Concurrent updates can also overwrite one another, leaving only the last completed visitor value [E:F1.S] [E:F2.S].

##### Why the accesses overlap
The controller actions may run for separate requests at the same time; the read at `StaticFieldUnlockedReadWrite.cs:17` can coincide with a write at `StaticFieldUnlockedReadWrite.cs:14`, and two writes can coincide with each other [E:F1.O] [E:F2.O].

##### Why the protection is insufficient
Neither access pair has an evidenced shared synchronization mechanism around `_lastVisitor`, so their ordering is not established [E:F1.P] [E:F2.P].

##### Interleaving
One request can read `_lastVisitor` while another writes it, producing either the prior or replacement value. Two Post requests can each write based on independent request execution, with the later write replacing the earlier one [E:F1.A] [E:F1.B] [E:F1.S] [E:F2.A] [E:F2.B] [E:F2.S].

##### Evidence mode
This is a deterministic `DCA1001` analysis of static state and its unprotected accesses [E:F1.R] [E:F2.R].

##### Uncertainty
The evidence establishes possible concurrent access, not which request order occurs in a particular deployment; confirm the intended semantics for simultaneous visitor updates [E:F1.S] [E:F2.S].

##### Remediation
- Guard every read and write of `_lastVisitor` with the same `lock` when a read and write must be mutually ordered; verify manually [E:F1.P] [E:F2.P].
  - Check: confirm accesses at `StaticFieldUnlockedReadWrite.cs:17` and `StaticFieldUnlockedReadWrite.cs:14` use one shared guard [E:F1.A] [E:F1.B] [E:F2.A] [E:F2.B].
- Move request-specific visitor state out of the shared static field if requests should not replace each other’s values; verify manually [E:F1.R] [E:F2.R].
  - Check: confirm concurrent requests cannot write the same shared `_lastVisitor` state [E:F2.S].

##### F1
- Access A: Demo.Web.Cases.StaticFieldUnlockedReadWrite.LastVisitorController.Get() performs read at Demo.Web/Cases/StaticFieldUnlockedReadWrite.cs:17 under root ControllerBase action Demo.Web.Cases.StaticFieldUnlockedReadWrite.LastVisitorController.Get(); holds no protection.
- Access B: Demo.Web.Cases.StaticFieldUnlockedReadWrite.LastVisitorController.Post(string) performs write at Demo.Web/Cases/StaticFieldUnlockedReadWrite.cs:14 under root ControllerBase action Demo.Web.Cases.StaticFieldUnlockedReadWrite.LastVisitorController.Post(string); holds no protection.
- Resource: Demo.Web · static:Demo.Web.Cases.StaticFieldUnlockedReadWrite.LastVisitorController · _lastVisitor
- Protection: unprotected
- Scenario: B writes `_lastVisitor`; A reads `_lastVisitor` at the same time; A observes either the old or the new value
- Confidence: High (85) · Path feasibility is not analyzed in this version.
- Evidence: F1.A, F1.B, F1.R, F1.O, F1.P, F1.S

##### F2
- Access A: Demo.Web.Cases.StaticFieldUnlockedReadWrite.LastVisitorController.Post(string) performs write at Demo.Web/Cases/StaticFieldUnlockedReadWrite.cs:14 under root ControllerBase action Demo.Web.Cases.StaticFieldUnlockedReadWrite.LastVisitorController.Post(string); holds no protection.
- Access B: Demo.Web.Cases.StaticFieldUnlockedReadWrite.LastVisitorController.Post(string) performs write at Demo.Web/Cases/StaticFieldUnlockedReadWrite.cs:14 under root ControllerBase action Demo.Web.Cases.StaticFieldUnlockedReadWrite.LastVisitorController.Post(string); holds no protection.
- Resource: Demo.Web · static:Demo.Web.Cases.StaticFieldUnlockedReadWrite.LastVisitorController · _lastVisitor
- Protection: unprotected
- Scenario: A writes `_lastVisitor`; B writes `_lastVisitor` before A's value is used; One of the two writes is lost
- Confidence: High (85) · Path feasibility is not analyzed in this version.
- Evidence: F2.A, F2.B, F2.R, F2.O, F2.P, F2.S

### Medium findings
_None._

### Low findings
_None._

### Diagnostics
- Load: 2.3 s
- Analysis: 2.0 s
- Late responses: 0
