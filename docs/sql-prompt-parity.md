# SQL Prompt parity roadmap

Researched September 28, 2026 from public sources (Redgate's own docs site was not reachable from
the build environment; entries marked *unconfirmed* need checking against a live SQL Prompt).
AI features are out of scope. Status: done (core checked), host (wired, unverified in SSMS), missing.

## 1. Editor behavior (what makes it feel like SQL Prompt)

| Behavior | SQL Prompt | Querywright | Next step |
|---|---|---|---|
| Suggestions as you type | Popup on every identifier, keyword, after `.`/space | host (offline schema only) | Live metadata from the SSMS connection; keywords, functions, variables |
| Ctrl+Space / refresh suggestions | Show list; Ctrl+Shift+D refreshes metadata | missing | Bind to popup trigger + cache refresh |
| JOIN ON suggestions | Proposes join conditions from foreign keys | missing | Needs FK metadata |
| Column picker | Tick columns in a list for SELECT/INSERT | missing | WPF picker over resolved scope |
| INSERT/EXEC parameter fill | Expands column list / parameters for INSERT and EXEC | missing | Needs metadata |
| Snippet shortcut + Tab | `ssf` Tab → `SELECT * FROM` | host | Also offer snippets in the popup |
| Wildcard expansion with Tab | `*` Tab → column list | host (offline schema) | Live metadata |
| F12 | Scripts object as ALTER in new tab; local declarations | host (locals); objects defer to SSMS | Script ALTER from metadata |
| Execute current statement | Shift+F5 runs the statement under the caret | missing | Statement bounds from ScriptDOM → select + native Execute |
| Tab coloring | Color query tabs per server/database environment | missing | Rules in settings file |
| Tab history | Searchable history of opened/closed tabs, restore after crash | missing | Local, crash-safe store |
| Quick info | Hover shows object definition / column type | missing | Needs metadata |

## 2. Formatting (Ctrl+K Ctrl+Y)

| Feature | Querywright |
|---|---|
| Format document / selection | done + verified in SSMS |
| Style editor, shared styles | partial (5 options, XML file) |
| Apply casing (keywords, types, functions) | missing |
| Add/remove square brackets | missing |
| Qualify object names (Ctrl+B Ctrl+Q) | missing (needs default schema / metadata) |
| Insert semicolons | host |
| Expand wildcards | host |
| Format on bulk files / folders | missing |

## 3. Snippets

Popular SQL Prompt shortcuts (community list) that Querywright should seed:
`ssf sst ss0 st100 scf sd smf ii df ij lj rj fj cj j loj roj foj gb ob be bt ctr rt tc cte ct ctt cv cp csf ctf citf at ata atd ac ap af dt dp dv dfn di inn lk isns isnn rnum cw ifs today trim sph spt w2`.
Placeholders used by those: `$CURSOR$`, `$SELECTEDTEXT$`, `$PASTE$`, `$DATE$`, `$TIME$`, `$USER$`, `$SERVER$`, `$DBNAME$`.
Querywright: 7 seeded; `$SELECTEDTEXT$` and connected placeholders missing.

## 4. Refactoring

| Refactor | Querywright |
|---|---|
| Rename local variable / alias | variable done; alias missing |
| Smart rename object (updates dependents) | missing (script preview only, never silent execution) |
| Split table | missing |
| Encapsulate as new stored procedure | missing |
| Find unused variables and parameters | missing (also MI005 below) |
| Summarize script (outline of statements) | missing |
| Find invalid objects | missing (metadata) |
| Script object as ALTER / CREATE | missing |
| Remove square brackets / add brackets | missing |

## 5. Results grid

| Action | Querywright |
|---|---|
| Copy as IN clause | missing |
| Script as INSERT (temp table + values) | missing |
| Open in Excel | missing (CSV/xlsx with formula-injection guard) |

These need SSMS results-grid integration, which has no public API; investigate first.

## 6. Code analysis (as-you-type squiggles + Error List)

Querywright rules today: SW001–SW017, mapped in analysis-rules.md. Remaining syntax-only candidates:

| SQL Prompt | Meaning |
|---|---|
| PE002 | Unqualified table/view name (needs default-schema awareness to avoid noise) |
| BP015 | Cursor scope not specified |
| BP016 | RETURN without value |
| BP014 | NOT NULL not specified in CREATE/DECLARE TABLE |
| DEP002 | READTEXT/WRITETEXT/UPDATETEXT |
| EI028 | Adding NOT NULL column without default |
| ST011 / ST012 | Temp table vs table variable hints |

Also: live squiggles while typing (currently command-driven), inline `-- querywright-disable` suppression,
and auto-fixes where one is unambiguous.

## Suggested order

1. Live metadata from the active SSMS connection (unblocks completion, wildcard, qualify, F12 ALTER, JOIN ON).
2. Next analysis batch above + squiggles while typing.
3. Execute current statement, seeded snippet library, `$SELECTEDTEXT$`.
4. Casing / brackets / qualify actions; alias rename; unused declarations.
5. Tab coloring and tab history.
6. Results-grid actions (after confirming an integration route).
7. Object-level refactors (smart rename, split table, encapsulate) as reviewable scripts.

## Sources

- Snippet list: https://github.com/gvohra/sqlpromptsnippets/blob/master/snippet-list.txt
- Rule catalog: https://github.com/Phil-Factor/SQLCodeSmells/blob/master/CodeSmells.adoc
- Results grid: https://www.red-gate.com/hub/product-learning/sql-prompt/3-results-grid-features-sql-prompt-brings-to-ssms
- Refactoring: https://www.red-gate.com/hub/product-learning/sql-prompt/refactoring-databases-with-sql-prompt/
- Keyboard: https://www.red-gate.com/hub/product-learning/sql-prompt/sql-prompt-by-keyboard/
