# Non-AI feature inventory

Baseline researched September 28, 2026. This is an initial inventory, not a
claim of exhaustive parity. Missing or unverified items remain in scope.
Status vocabulary: missing, core-only, integrated, verified in SSMS.

| Area | Required behavior | Status | Acceptance evidence required |
|---|---|---|---|
| Host | SSMS 22.10.1 package, menus, settings, shortcuts, undo, install/uninstall | install, package load, six menus and formatting undo/redo verified | Settings, shortcuts, other commands and uninstall pending |
| Completion | Objects, columns, aliases, parameters, joins, fuzzy matches, column picker, metadata refresh | offline resolver checked; picker wiring unverified | Alias/CTE/derived scopes checked; live metadata, inline popup and broader SQL recovery pending |
| Snippets | User templates, context values, date/time, caret/selection, sharing | core verified; host wiring unverified | 17 core/file checks; editor undo and connected context pending |
| Formatting | Selection/document, styles, shared settings, batch operation | core checked; document formatting and undo/redo verified in SSMS | Selection/styles host tests, SQLCMD and bulk files pending |
| Refactoring | Local rename, qualification, wildcard expansion, brackets, semicolons, procedure extraction | local-variable rename checked; preview wiring unverified | Batch isolation, collision/EXEC handling checked; alias/public parameter rename and other refactorings pending |
| Database rename | Dependency-aware object rename script preview | missing | Identify unresolved dynamic references; never execute silently |
| Analysis | Rule inventory, configurable severity/enablement, issue navigation, fixes, shared settings | four rules and settings checked; host wiring unverified | See analysis-rules.md; fixes and remaining catalog pending |
| Navigation | Definitions, ALTER script generation, object explorer, script overview, unused declarations | missing | Correct object and scope; generated text only |
| Editor | Execute statement, environment colors | missing | Exact statement boundaries; explicit execution action; accessible labels |
| History | Open/closed tabs, search, restore, rename, retention, crash recovery | missing | Crash-safe writes, local privacy, restart recovery |
| Results | IN-list copy, INSERT scripts, spreadsheet export | missing | Nulls, Unicode, quotes, types, large grids, spreadsheet formula injection |
| Teams | Shared snippets, formatting and analysis policies; bulk processing | missing | File-based sharing, conflict/error reporting and deterministic output |

AI excluded explicitly: text-to-SQL, explanations, AI suggestions, index/query AI
analysis, database chat. Static diagnostics and ordinary IntelliSense remain in scope.
Separate Redgate products are not automatically included by their bundle membership.

## Sources

- Product scope: https://www.red-gate.com/products/sql-prompt/
- Feature access and result helpers: https://www.red-gate.com/products/sql-prompt/faq/
- Current documentation: https://documentation.red-gate.com/sp11
- Older quick reference, historical evidence requiring current confirmation:
  https://assets.red-gate.com/products/sql-development/sql-prompt/assets/sql-prompt-quick-reference.pdf
- Analysis: https://documentation.red-gate.com/sp/sql-code-analysis
- Host release: https://learn.microsoft.com/en-us/ssms/release-notes-22
- Host extension support caveat: https://learn.microsoft.com/en-us/ssms/faq
- Existing formatter: https://learn.microsoft.com/en-us/ssms/scripting/format-t-sql
- Parser candidate (MIT): https://github.com/microsoft/SqlScriptDOM

## Environment observed

- SSMS 22.10.1 installed at C:\Program Files\Microsoft SQL Server Management Studio 22\Release.
- Running user session observed on SSMS 19; left untouched.
- .NET SDK 10.0.401 available.
- Visual Studio Build Tools 2026 installer reports incomplete installation.
- Development VSIX installer returned exit code 0 on September 29, 2026 after correcting
  the manifest target to Microsoft.VisualStudio.Ssms and selecting the discovered instance ID.
  Version 0.1.1 update also installed successfully. Six Tools menu entries and snippet
  smoke command verified in SSMS 22.10.1. Document formatting of examples/host-smoke.sql,
  single-step undo and redo passed in the disconnected SQL editor. Script not executed.
- Initial host formatting exposed MissingMethodException for PersistTrailingGo:
  SSMS uses ScriptDOM 18.0.56.2 despite the bundled newer assembly sharing version 18.0.0.0.
  Removed redundant option (GO separator handling already preserves those lines).
  scripts/Test-HostParser.ps1 now exercises formatting with the installed host parser.
  Remaining commands and connected editor behavior still need runtime verification.

## Authorized integration target

- User requested local SampleDb for testing. Verified via Windows authentication:
  server localhost (reported dev-pc), database SampleDb, ONLINE.
- This is the default SQL Server instance, not (localdb)\MSSQLLocalDB. The latter exposed
  only system databases during discovery.
- Read-only metadata checks passed: 8,320 visible user tables, 95,584 columns,
  SQL_Latin1_General_CP1_CI_AS collation, dbo default schema.
- Use bounded read-only queries/metadata for integration tests. No database writes,
  DDL, procedure execution or deployment authorized. Do not log application row data.
- User is installing SSMS 22. Leave installer and user sessions untouched during setup.
- Later check: SSMS 22 running with an unsaved connected query; no setup process seen.
  User session left untouched. Runtime tests need an isolated test session or restart window.

## Next implementation checkpoint

Validate package installation/loading when setup is finished. Verify snippets in SQL
editor with undo and diagnostics in Error List. Implement connected snippet context.
Completion must resolve real SQL scopes rather than infer aliases using regex alone.
Expand the inventory into individual current commands and analysis rules before any
parity claim. This initial package is not a completed SQL Prompt replacement.
