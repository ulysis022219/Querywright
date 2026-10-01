# SQL Prompt parity roadmap

Researched September 28, 2026 from public sources (Redgate's own docs site was not reachable from
the build environment; entries marked *unconfirmed* need checking against a live SQL Prompt).
AI features are out of scope. Status: done (core checked), host (wired, unverified in SSMS), missing.

## 1. Editor behavior (what makes it feel like SQL Prompt)

| Behavior | SQL Prompt | Querywright | Next step |
|---|---|---|---|
| Suggestions as you type | Popup on every identifier, keyword, after `.`/space | host (live connection metadata or offline schema; keywords, functions, variables, CTEs, aliases, snippets) | — |
| Ctrl+Space / refresh suggestions | Show list; Ctrl+Shift+D refreshes metadata | host (Ctrl+Space is the editor's own; Ctrl+Shift+D bound) | — |
| JOIN ON suggestions | Proposes join conditions from foreign keys | host (FKs from live metadata or offline DDL) | — |
| Column picker | Tick columns in a list for SELECT/INSERT | host (Querywright > Pick columns for *: checkbox list replaces `*` or `alias.*`) | — |
| INSERT/EXEC parameter fill | Expands column list / parameters for INSERT and EXEC | host (Tab after `INSERT INTO t` / `EXEC p`; generated columns skipped; procedures from the connection or the script) | — |
| Snippet shortcut + Tab | `ssf` Tab → `SELECT * FROM` | host (also listed in the popup; Tab expands with the list open) | — |
| Wildcard expansion with Tab | `*` Tab → column list | host | — |
| F12 | Scripts object as ALTER in new tab; local declarations | host (locals; procedures, views, functions, triggers scripted as ALTER from the live connection; tables and unreadable objects defer to SSMS) | — |
| Execute current statement | Shift+F5 runs the statement under the caret | host (selects the statement, then SSMS's own Query.Execute — only on explicit keypress) | — |
| Tab coloring | Color query tabs per server/database environment | host (colored strip above the editor, plus "server · database" at the bottom right in the same color; rules in Options) | — |
| Tab history | Searchable history of opened/closed tabs, restore after crash | host (local files, timestamped version per tab after each edit and on execute, executed versions marked, newest 100 per tab, 200 tabs and 256 MB in all, favorites aside; secrets saved as ***; search, preview, reopen, rename, delete; opt out in Options) | — |
| Quick info | Hover shows object definition / column type | host (variables, procedure parameters, table columns and types; cached metadata only) | — |

## 2. Formatting (Ctrl+K Ctrl+Y)

| Feature | Querywright |
|---|---|
| Format document / selection | done + verified in SSMS |
| Style editor, shared styles | host (13 options, dialog with live preview, shared XML settings file) |
| Apply casing (keywords, types, functions) | host |
| Add/remove square brackets | host |
| Qualify object names | host (dbo default) |
| Insert semicolons | host |
| Expand wildcards | host |
| Format on bulk files / folders | host (folder of .sql files; preview count + confirmation; encoding and line endings kept; failures left untouched and reported) |

## 3. Snippets

Popular SQL Prompt shortcuts (community list) that Querywright should seed:
`ssf sst ss0 st100 scf sd smf ii df ij lj rj fj cj j loj roj foj gb ob be bt ctr rt tc cte ct ctt cv cp csf ctf citf at ata atd ac ap af dt dp dv dfn di inn lk isns isnn rnum cw ifs today trim sph spt w2`.
Placeholders used by those: `$CURSOR$`, `$SELECTEDTEXT$`, `$PASTE$`, `$DATE$`, `$TIME$`, `$USER$`, `$SERVER$`, `$DBNAME$`.
Querywright: 56 seeded (the list above) plus Select / Create procedure; all listed placeholders supported.

## 4. Refactoring

| Refactor | Querywright |
|---|---|
| Rename local variable / alias | host (variable and alias, with preview) |
| Smart rename object (updates dependents) | host (sp_rename + ALTER for dependent modules, opened in a new window; never executed) |
| Split table | host: pick columns to move; script creates the new table keyed and foreign-keyed on the primary key, copies data, drops moved columns in one transaction; opened in a new window, never executed |
| Encapsulate as new stored procedure | host (CREATE PROCEDURE script from the selection, variables become parameters; new window) |
| Script for multiple databases | host (tick databases on the connected server; USE/GO block per database in a new window, never executed; optional SQLCMD stop on first error) |
| Find unused variables and parameters | host (Error List) |
| Summarize script (outline of statements) | host (Error List, click to navigate) |
| Find invalid objects | host: read-only binding check of up to 2000 modules (sys.dm_sql_referenced_entities in TRY/CATCH + unresolved sys.sql_expression_dependencies); report opens in a new window |
| Script object as ALTER / CREATE | host (F12, ALTER in a new query; never executed) |
| Remove square brackets / add brackets | host |

## 5. Results grid

| Action | Querywright |
|---|---|
| Copy as IN clause | host (distinct values, numbers bare, NULL dropped) |
| Script as INSERT (temp table + values) | host (`#Results` with reported column types, 1000-row batches, new window, never executed) |
| Open in Excel | host (.xlsx in %TEMP%, removed after a day; cells are Text so dates and codes match the grid exactly, numeric columns stay numbers with the grid's decimals; no formulas) |
| Save as CSV | host (RFC 4180, UTF-8, formula-injection guard) |
| Copy as Markdown / JSON | host (Markdown table with '|' escaped; JSON array with numbers for numeric columns, NULL as null) |
| Script as UPDATE / MERGE | host (keyed on the first column; target is the query's first table; MERGE updates and inserts, never deletes; rowversion skipped; opens in a new window, never executed) |
| Script as CREATE TABLE | host (grid's column types; NOT NULL where no row is NULL; opens in a new window, never executed) |

In the results grid's right-click menu and the Querywright menu. SSMS has no public grid API, so the
host reflects over the focused `GridControl` and its `IGridStorage` (the approach used by
SSMSDataAnalyzer and SQLExtended). Values are the grid's display text: floats are rounded and very
long text may be truncated, exactly as SSMS shows them. One selected cell (or none) means the whole
grid; up to 500,000 cells are read. Cell data is never logged. If a future SSMS changes the grid
internals, the commands report that and do nothing.

## 6. Code analysis (as-you-type squiggles + Error List)

Querywright rules today: SW001–SW047, mapped in analysis-rules.md, with inline `-- querywright-disable`
suppression. Remaining syntax-only candidates:

| SQL Prompt | Meaning |
|---|---|
| PE002 | Unqualified table/view name (needs default-schema awareness to avoid noise) |
| ST011 / ST012 | Temp table vs table variable hints |

Squiggles while typing: host (700 ms after typing pauses). Auto-fixes: host (fix at caret / fix all for SW001, SW003, SW009, SW010, SW015, SW016, SW017; every fix must re-parse).

## Remaining

Everything above is at least host-wired; SSMS e2e covers the scenarios listed in features.md.

## Sources

- Snippet list: https://github.com/gvohra/sqlpromptsnippets/blob/master/snippet-list.txt
- Rule catalog: https://github.com/Phil-Factor/SQLCodeSmells/blob/master/CodeSmells.adoc
- Results grid: https://www.red-gate.com/hub/product-learning/sql-prompt/3-results-grid-features-sql-prompt-brings-to-ssms
- Refactoring: https://www.red-gate.com/hub/product-learning/sql-prompt/refactoring-databases-with-sql-prompt/
- Keyboard: https://www.red-gate.com/hub/product-learning/sql-prompt/sql-prompt-by-keyboard/
