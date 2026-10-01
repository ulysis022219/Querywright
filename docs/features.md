# Non-AI feature inventory

Baseline researched September 28, 2026. This is an initial inventory, not a
claim of exhaustive parity. Missing or unverified items remain in scope.
Status vocabulary: missing, core-only, integrated, verified in SSMS.

| Area | Required behavior | Status | Acceptance evidence required |
|---|---|---|---|
| Host | SSMS 22.10.1 package, menus, settings, shortcuts, undo, install/uninstall | verified in SSMS (install, package load, menus, formatting undo/redo; e2e job) | Uninstall pending |
| Completion | Objects, columns, aliases, parameters, joins, fuzzy matches, column picker, metadata refresh | integrated: as-you-type popup, live metadata (tables, views, columns, FK joins), schema cache on disk (names and types only, hashed file names, option "Cache schema on disk"), Ctrl+Shift+D refresh, column picker, procedure suggestions after EXEC, INSERT/EXEC fill (Tab after the name, or when a suggestion is committed by Tab, Enter or click), hover quick info (column type, nullability and default; tables, views and procedures link to a Script/Summary popup with CREATE TABLE DDL and columns, read on click) | e2e keyword, live column, EXEC fill and schema cache file scenarios |
| Snippets | User templates, Tab shortcuts (ssf), context values, date/time, caret/selection, sharing | integrated: 56 seeded, all placeholders, `$name$` tab-stop fields, listed in popup, Tab with popup open | e2e ssf and typed-ssf scenarios |
| Formatting | Selection/document, styles, shared settings, batch operation | verified in SSMS (document); casing, brackets, qualify, style editor (13 options), bulk folder formatting, optional format on save integrated | Bulk encoding/newline preservation checked in core; e2e format on save |
| Refactoring | Local rename, qualification, wildcard expansion, brackets, semicolons, procedure extraction | integrated: variable/alias rename, qualification, wildcard (live or offline), brackets, semicolons, encapsulate as procedure (script in new window) | e2e wildcard scenario |
| Database rename | Dependency-aware object rename script preview | integrated: sp_rename + dependent ALTERs in a new window, unparsed/dynamic dependents flagged; never executed; find invalid objects (read-only); split table script | Identify unresolved dynamic references; never execute silently |
| Analysis | Rule inventory, configurable severity/enablement (Tools > Querywright > Formatting style and rules... > Analysis rules), issue navigation, fixes, shared settings | integrated: 46 rules, squiggles while typing, unmatched BEGIN/END/CASE/parentheses underlined, Error List, suppression; fix at caret and fix all (7 rules) | See analysis-rules.md |
| Navigation | Definitions, ALTER script generation, object explorer, script overview, unused declarations | integrated: F12 locals/aliases/CTEs; modules scripted as ALTER and tables as CREATE TABLE in a new query (also `OtherDb.dbo.Name` on the same server); summarize script, unused declarations | e2e F12 variable, procedure and table scenarios |
| Editor | Execute statement, environment colors | integrated: Shift+F5 (explicit keypress only), prompt before executing DELETE/UPDATE without WHERE, "Production servers" patterns that always prompt for DROP, ALTER, TRUNCATE and unfiltered changes (#temp tables excepted), environment color strip, server · database label at the bottom right; Object Explorer server right-click "Querywright: server color..." adds a server rule | Statement boundaries and color rule edits checked in core; e2e opens the server menu and cancels a production DROP |
| History | Open/closed tabs, search, restore, rename, retention, crash recovery | integrated: local store (60 s + on close, 200 kept), search, preview, reopen, rename, delete, opt-out | Crash-safe writes, local privacy, restart recovery |
| Results | IN-list copy, INSERT scripts, spreadsheet export | integrated via grid right-click menu: copy as IN clause, script as INSERT (DROP/CREATE #Results temp table plus VALUES, new window, not executed), open in Excel (.xlsx with the grid text kept 1:1; only numeric columns become numbers, with the grid's decimals), save as CSV; formula-injection guard | e2e grid script-as-INSERT scenario; grid read by reflection (no public API) |
| Compare | Object compare across databases | integrated: editor right-click (and Tools) "compare object with database..." scripts the object at the caret/selection from the current and a chosen database on the same server (read-only) and reports identical or opens the diff window | Script equality checked in core; SSMS menu placement not e2e-tested |
| Teams | Shared snippets, formatting and analysis policies; bulk processing | integrated: shared snippet folder, shared settings file (style + rule severities), bulk folder formatting | Shared paths are ordinary files; no server component |

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

## Integration testing

- Manual checks ran against a large local SQL Server database with Windows authentication,
  read-only: bounded metadata queries only; no writes, DDL, procedure execution or row data logged.
- Automated end-to-end tests run SSMS 22 on the CI runner against a throwaway LocalDB database.

## Next implementation checkpoint

Validate package installation/loading when setup is finished. Verify snippets in SQL
editor with undo and diagnostics in Error List. Implement connected snippet context.
Completion must resolve real SQL scopes rather than infer aliases using regex alone.
Expand the inventory into individual current commands and analysis rules before any
parity claim. This initial package is not a completed SQL Prompt replacement.

## Cross-database and dynamic SQL

| Feature | Notes |
|---|---|
| Run in multiple databases (merged results) | Tick databases, run the script (one batch, no GO) in each, first result set of each merged with a leading Database column in a window; failed databases listed. Confirms before DROP, ALTER, TRUNCATE or unfiltered changes, naming databases that match "Production servers". Runs with the window's login; 100,000 row cap. |
| Find object or column in all databases | Name contains text (literal, wildcards escaped) across tables, views, procedures, functions, triggers and columns; read-only catalog query. |
| Find in database code / usages | Procedures, views, functions and triggers in the current database whose code contains the text, one row per matching line; double-click opens the script at that line. A table, view or synonym name uses the dependency catalog instead of scanning definitions. Explicit command only; first 500 objects, no ORDER BY, READ UNCOMMITTED, 3 s lock timeout, low deadlock priority, 60 s timeout. |
| Wrap in dynamic SQL / Unwrap dynamic SQL | Selection (or whole window) to `DECLARE @sql ... PRINT ... sp_executesql` with quotes doubled, and back from the first string literal. |
| SW047 | EXEC of a concatenated string. |
| Warn before USE then change data | Execution prompt when a script switches database with USE and then modifies data (Options, on by default). |

Completion after `OtherDb.` offers that database's schemas, then tables, then columns. The other database's catalog loads in the background on first use, so the list appears on a later keystroke.

Column suggestions list only the tables visible at the caret: a derived table or CTE body does not leak its inner FROM into the outer query. Columns are grouped per table, nearest scope first, and are never dropped by the item cap. `"quoted"` names resolve like `[bracketed]` ones.
