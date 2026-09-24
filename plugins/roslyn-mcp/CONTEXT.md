# Roslyn MCP — domain language

The vocabulary the tool schemas, their descriptions and the `roslyn-first` skill must use. Started
during the interview that scoped symbol IDs, project-wide diagnostics and type descriptions.

| Term | Meaning |
|---|---|
| **Position** | A file path with a 1-based line and column. One of the two ways a client names a symbol, and the only one for a local or a parameter, which have no **symbol ID**. |
| **Symbol ID** | The documentation comment ID of a declared symbol — `T:Ns.Type`, `M:Ns.Type.Method(System.Int32)`. The other way a client names a symbol. Every result that reports a symbol carries it, and a client copies it from a result rather than composing one. Never a display string or a bare fully-qualified name, which are ambiguous across overloads. |
| **Project context** | The one project compilation a **symbol ID** is resolved in, named with its target framework when the project is multi-targeted. A client may name it; otherwise it is the project declaring the symbol in source, or for a symbol from metadata the first project that resolves it. Every response names the context it used, and for metadata the assembly version, because two projects can reference different versions of one package. |
| **Enclosing span** | The line range of the declaration of the member containing a location — for a reference its containing member, for a declaration the member itself. The range a client reads to see that member whole. |
| **Type description** | A type's base-type chain, its interfaces and the members it declares that are accessible from the **project context**, then the extension methods any assembly the project references declares for it, each with the namespace a caller must import — every entry carrying its **symbol ID**. Inherited members are not repeated; a client follows the chain. Extensions on a receiver that accepts any type (`this object`, or `this T` where `T` is constrained to no type, only at most to a kind such as `class`) are left out, because they describe no type in particular. Named by symbol ID or by a **position**, where it means the type of the symbol there — the type itself, a variable's or member's type, a method's return type. |
| **Affected projects** | The projects holding the files a client changed, plus every project that transitively depends on them — where an edit can have broken a caller. |
| **Unchecked project** | A project inside a diagnostics request's scope that its time budget did not reach. Reported by name, so that no errors from it is never read as no errors in it. |

## Flagged ambiguities

- "FQN" and "full name" were used for the symbol identity a client passes — resolved: that is the
  **symbol ID**. `fullName` in results stays a display string for reading, never an input.
