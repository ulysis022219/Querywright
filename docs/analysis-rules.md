# Static analysis coverage

27 syntax-only rules; this is not full SQL Prompt rule parity (see sql-prompt-parity.md). Source reference:
https://documentation.red-gate.com/codeanalysis/code-analysis-for-sql-server/best-practice-rules

| Our ID | Behavior | Related Redgate rule | Evidence |
|---|---|---|---|
| SW001 | Wildcard in output list | BP005 | Wildcard flagged; COUNT(*) excluded |
| SW002 | INSERT lacks explicit target columns | BP004 | Column list and DEFAULT VALUES excluded |
| SW003 | Direct or parenthesized NULL comparison | BP011 subset | IS NULL excluded; expressions inside strings/comments ignored |
| SW004 | CASE without fallback expression | BP012 | Both CASE forms; explicit ELSE excluded |
| SW005 | DELETE without WHERE | BP017 | WHERE excluded |
| SW006 | UPDATE without WHERE | BP018 | WHERE excluded |
| SW007 | ORDER BY constant or ordinal | BP002 | Column names excluded |
| SW008 | char/varchar/nchar/nvarchar/binary/varbinary without length | BP007, BP008 | Declarations, CAST/CONVERT, columns; (n)/(max) excluded |
| SW009 | @@IDENTITY | BP010 | SCOPE_IDENTITY excluded |
| SW010 | TEXT, NTEXT, IMAGE | DEP001 | |
| SW011 | Comma-separated FROM (old-style join) | ST001 | Explicit JOIN excluded |
| SW012 | Procedure named sp_ | EI024 | |
| SW013 | ISNUMERIC | EI029 | |
| SW014 | NOT IN with subquery | PE019 | NOT IN value list excluded |
| SW015 | Procedure without SET NOCOUNT ON | PE009 | Anywhere in body counts; CLR procedures excluded |
| SW016 | Variable declared but never used (per batch) | MI005 | Parameters excluded; GO isolates batches |
| SW017 | EXEC without schema | PE001 | sp_ system procedures and #temp procedures excluded |
| SW018 | Cursor without LOCAL/GLOBAL scope | BP015 | DECLARE CURSOR and SET @c = CURSOR; explicit LOCAL or GLOBAL excluded |
| SW019 | RETURN without a value in a stored procedure | BP016 | RETURN n excluded; bare RETURN outside procedures excluded |
| SW020 | Column without explicit NULL/NOT NULL | BP014 | CREATE TABLE (incl. #temp) and DECLARE @t TABLE; PRIMARY KEY (column or table level), IDENTITY and computed columns excluded |
| SW021 | READTEXT, WRITETEXT, UPDATETEXT | DEP002 | .WRITE excluded |
| SW022 | ALTER TABLE ADD NOT NULL column without DEFAULT | EI028 | DEFAULT, NULL, computed and IDENTITY columns excluded; flagged even if the table may be empty |
| SW023 | SET ROWCOUNT | DEP014 | Deprecated for INSERT/UPDATE/DELETE; flagged wherever it appears |
| SW024 | NOLOCK / READUNCOMMITTED table hint | Hint rules (PE004–PE007 family; exact ID unverified) | Other table hints excluded |
| SW025 | WAITFOR DELAY inside a stored procedure | Performance (ID unverified) | WAITFOR in ad hoc batches and WAITFOR TIME excluded |
| SW026 | SELECT TOP without ORDER BY | Best practice (ID unverified) | ORDER BY excluded; TOP inside EXISTS excluded; UPDATE/DELETE TOP not checked |
| SW027 | EXECUTE(string) dynamic SQL | BP013 | sp_executesql excluded |

SW027 replaces the originally planned "comparison with NULL using =/<>" rule, which duplicates SW003 (BP011).
Rule IDs marked unverified could not be confirmed against the Redgate catalog; treat them as related, not equivalent.

## Inline suppression

Comments (`--` or `/* */`, case-insensitive) control rule diagnostics:

```sql
-- querywright-disable SW005, SW006       suppress these rules until enabled again or end of script
-- querywright-enable SW005               re-enable one rule
-- querywright-disable                    no IDs: suppress every rule
-- querywright-enable                     no IDs: re-enable every rule
-- querywright-disable-next-line SW001    suppress on the line after the comment only
```

A diagnostic is suppressed by its start position. IDs are read until the first word that is not a rule ID,
so a trailing reason is allowed. Directives inside strings are ignored. Parse errors are never suppressed.
Directives span GO batches.

These are review hints, not proof of incorrect SQL. No automatic fixes implemented.
Per-rule severity and disabling use shared XML settings; inline suppression as above. Our diagnostic
messages and code are independently authored. Do not claim identical edge-case behavior.

Parsing uses SQL Server 2025 grammar, QUOTED_IDENTIFIER ON. Syntax errors are returned
separately and prevent rule evaluation for that batch; zero rule hints after a parse
failure never means clean SQL. Dynamic SQL strings are not recursively analyzed.
Cancellation is checked before/after parsing and per statement during rule evaluation; ScriptDOM
parse itself is synchronous. A 5,000-line script (2,000 statements) analyzes in about 220 ms on the
check machine (asserted under 2 s), so Analyze is used directly for debounced as-you-type checks;
worst-case cancellation latency is one parse. There is no separate quick-analysis API.
The host runs analysis off the UI thread and rejects stale results.
Client GO repetition is supported by formatting, but analysis still reports a parse error
for GO with a repetition count. SQLCMD commands are not supported yet.

Full catalog work remains: each current rule needs its own mapping, scope, positive and
negative fixtures, severity defaults and supported fixes. Metadata-dependent rules
require explicit, permission-aware database inspection before they can be verified.
