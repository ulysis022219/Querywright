# Non-AI feature inventory

Baseline researched September 28, 2026. This is an initial inventory, not a
claim of exhaustive parity. Missing or unverified items remain in scope.
Status vocabulary: missing, core-only, integrated, verified in SSMS.

| Area | Required behavior | Status | Acceptance evidence required |
|---|---|---|---|
| Host | SSMS 22.10.1 package, menus, settings, shortcuts, undo, install/uninstall | verified in SSMS (install, package load, menus, formatting undo/redo; e2e job) | Uninstall pending |
| Completion | Objects, columns, aliases, parameters, joins, fuzzy matches, column picker, metadata refresh | integrated: as-you-type popup, live metadata (tables, views, columns, FK joins), Ctrl+Shift+D refresh, column picker, INSERT/EXEC fill, hover quick info | e2e keyword and live column scenarios |
| Snippets | User templates, Tab shortcuts (ssf), context values, date/time, caret/selection, sharing | integrated: 56 seeded, all placeholders, listed in popup, Tab with popup open | e2e ssf and typed-ssf scenarios |
| Formatting | Selection/document, styles, shared settings, batch operation | verified in SSMS (document); casing, brackets, qualify integrated | Bulk files pending |
| Refactoring | Local rename, qualification, wildcard expansion, brackets, semicolons, procedure extraction | integrated: variable/alias rename, qualification, wildcard (live or offline), brackets, semicolons, encapsulate as procedure (script in new window) | e2e wildcard scenario |
| Database rename | Dependency-aware object rename script preview | integrated: sp_rename + dependent ALTERs in a new window, unparsed/dynamic dependents flagged; never executed; find invalid objects (read-only); split table script | Identify unresolved dynamic references; never execute silently |
| Analysis | Rule inventory, configurable severity/enablement, issue navigation, fixes, shared settings | integrated: 46 rules, squiggles while typing, Error List, suppression; fix at caret and fix all (7 rules) | See analysis-rules.md |
| Navigation | Definitions, ALTER script generation, object explorer, script overview, unused declarations | integrated: F12 locals/aliases/CTEs; modules scripted as ALTER in a new query; summarize script, unused declarations | e2e F12 variable scenario |
| Editor | Execute statement, environment colors | integrated: Shift+F5 (explicit keypress only), environment color strip | Statement boundaries checked in core |
| History | Open/closed tabs, search, restore, rename, retention, crash recovery | integrated: local store (60 s + on close, 200 kept), search, preview, reopen, rename, delete, opt-out | Crash-safe writes, local privacy, restart recovery |
| Results | IN-list copy, INSERT scripts, spreadsheet export | integrated via grid right-click menu: copy as IN clause, script as INSERT (new window, not executed), open in Excel, save as CSV; formula-injection guard | e2e grid script-as-INSERT scenario; grid read by reflection (no public API) |
| Teams | Shared snippets, formatting and analysis policies; bulk processing | partial: file-based snippet folder and settings file | Bulk processing pending |

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

- User requested local NOAH_GDC_C001 for testing. Verified via Windows authentication:
  server localhost (reported ulysis-pc), database NOAH_GDC_C001, ONLINE.
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
