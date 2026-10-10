# Static analysis coverage

73 syntax-only rules; this is not full SQL Prompt rule parity (see sql-prompt-parity.md). Source reference:
https://documentation.red-gate.com/codeanalysis/code-analysis-for-sql-server/best-practice-rules
Related IDs and titles were checked against the rule catalog shipped with SQL Prompt 11.5.5 (names and IDs only;
rule text and behavior here are independently written).

| Our ID | Behavior | Related Redgate rule | Evidence |
|---|---|---|---|
| SW001 | Wildcard in output list | BP005 | Wildcard flagged; COUNT(*) excluded |
| SW002 | INSERT lacks explicit target columns | BP004 | Column list and DEFAULT VALUES excluded |
| SW003 | Direct or parenthesized NULL comparison | BP011 subset | IS NULL excluded; expressions inside strings/comments ignored |
| SW004 | CASE without fallback expression | BP012 | Both CASE forms; explicit ELSE excluded |
| SW005 | DELETE without WHERE | BP017 | WHERE excluded; off by default (execute prompt instead) |
| SW006 | UPDATE without WHERE | BP018 | WHERE excluded; off by default (execute prompt instead) |
| SW007 | ORDER BY constant or ordinal | BP002 | Column names excluded |
| SW008 | char/varchar/nchar/nvarchar/binary/varbinary without length | BP007, BP008 | Declarations, CAST/CONVERT, columns; (n)/(max) excluded |
| SW009 | @@IDENTITY | BP010 | SCOPE_IDENTITY excluded |
| SW010 | TEXT, NTEXT, IMAGE | Deprecated data types (ID unverified; DEP001 is SW051) | |
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
| SW024 | NOLOCK / READUNCOMMITTED table hint | Hint rules (PE004–PE007 family) | Other table hints excluded |
| SW025 | WAITFOR DELAY inside a stored procedure | MI007 | WAITFOR in ad hoc batches and WAITFOR TIME excluded |
| SW026 | SELECT TOP without ORDER BY | BP006 | ORDER BY excluded; TOP inside EXISTS excluded; UPDATE/DELETE TOP not checked |
| SW027 | EXECUTE(string) dynamic SQL | BP013 | sp_executesql excluded |
| SW028 | COUNT subquery compared with 0 | PE013 | Any comparison with literal 0, either side; other constants excluded |
| SW029 | GOTO | Style (ID unverified; not ST011) | |
| SW030 | SET ANSI_NULLS / ANSI_PADDING / CONCAT_NULL_YIELDS_NULL OFF | Deprecated SET options (ID unverified; not DEP015) | ON excluded |
| SW031 | @@ERROR | BP (unverified) | Other @@ functions excluded |
| SW032 | Function around a column in a WHERE/JOIN comparison | PE (non-sargable; unverified) | Info; only when the other side has no column; functions in the select list excluded |
| SW033 | LIKE pattern starting with % on a column | PE (unverified) | Info; trailing % excluded |
| SW034 | Index table hint | PE004 | Info; other hints excluded |
| SW035 | SET FMTONLY ON | DEP (unverified) | OFF excluded. (FASTFIRSTROW no longer parses in the 2025 grammar, so it surfaces as a syntax error.) |
| SW036 | Named constraint on a #temp table | BP (unverified) | Unnamed constraints and permanent tables excluded |
| SW037 | FLOAT / REAL | BP023 | Info |
| SW038 | MONEY / SMALLMONEY | BP022 | Info |
| SW039 | TIMESTAMP type synonym | DEP (unverified) | ROWVERSION excluded |
| SW040 | String literal as column alias (`'x' = 1`, `AS 'x'`) | DEP021 | Bare and bracketed aliases excluded; fix brackets an `AS 'x'` alias |
| SW041 | Numbered procedure (`name;n`) | DEP020 | |
| SW042 | `!<` and `!>` operators | ST (unverified) | |
| SW043 | TOP 100 PERCENT | BP (unverified) | Other percentages excluded |
| SW044 | EXISTS over an aggregate with no GROUP BY/HAVING | BP (unverified) | GROUP BY or HAVING excluded |
| SW045 | DECIMAL / NUMERIC without precision | BP (unverified) | Any explicit precision excluded |
| SW046 | xp_cmdshell | Security (unverified) | Any schema/database qualification matched |
| SW047 | EXEC of a concatenated string (`EXEC('...' + @x)`) | Security (unverified) | EXEC of one literal or one variable excluded |
| SW048 | `!=` comparison | ST013 | `<>` excluded; fix rewrites to `<>` |
| SW049 | `alias = expression` in a select list | ST002 | `@variable = expression` assignment excluded; fix rewrites to `expression AS alias` |
| SW050 | TOP without parentheses | ST006 | `TOP (n)` excluded; fix adds the parentheses |
| SW051 | Table hint without WITH | DEP001 | `WITH (hint)` excluded; fix adds WITH |
| SW052 | GROUP BY ALL | DEP003 | |
| SW053 | SETUSER | DEP006 | |
| SW054 | DBCC DBREINDEX / INDEXDEFRAG / SHOWCONTIG | DEP009, DEP011, DEP012 | Other DBCC commands excluded |
| SW055 | SQL_VARIANT | BP024 | Info |
| SW056 | SELECT ... INTO | PE003 | Info; `SELECT @v = ...` excluded |
| SW057 | Query hint in OPTION (...) | PE007 | Info; RECOMPILE and MAXRECURSION excluded |
| SW058 | Join hint (LOOP / HASH / MERGE / REMOTE) | PE005 | |
| SW059 | SET NOCOUNT OFF | PE008 | Info; ON excluded |
| SW060 | SET FORCEPLAN ON | PE014 | OFF excluded |
| SW061 | Procedure WITH RECOMPILE | PE021 | Info; statement-level OPTION (RECOMPILE) excluded |
| SW062 | ORDER BY in INSERT ... SELECT without TOP or OFFSET | PE020 | TOP and OFFSET/FETCH excluded |
| SW063 | PRINT inside a trigger | PE011 | PRINT elsewhere excluded |
| SW064 | Trigger returns a result set | BP003 | Variable assignment and INSERT ... SELECT excluded |
| SW065 | TODO / FIXME / BUG / HACK comment | SC004 | Info; whole words in comments only, strings excluded |
| SW066 | Cursor declared but never deallocated in the script | PE016 | Name match is case-insensitive; cursor variables not tracked |
| SW067 | Column reference with three or more name parts in a select list | DEP026 | `alias.column` and `table.column` excluded |
| SW068 | `DROP INDEX table.index` | DEP022 | `DROP INDEX index ON table` excluded |
| SW069 | ALL in GRANT / DENY / REVOKE | DEP018 | Named permissions excluded |
| SW070 | IN (subquery) | PE019 | Info; value lists excluded; NOT IN stays SW014 |
| SW071 | VARCHAR / NVARCHAR / VARBINARY of length 1 or 2 | BP009 | Info; fixed-length types and length 3+ excluded |
| SW072 | CREATE INDEX without CLUSTERED / NONCLUSTERED | BP001 | Info; columnstore and explicit types excluded |
| SW073 | SQL Server 2000 system table (sysobjects, syscolumns, ...) | DEP019 | Unqualified, dbo. or sys. only; other schemas excluded |

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

These are review hints, not proof of incorrect SQL. Automatic fixes (fix at caret / fix all; each result must re-parse):
SW001, SW003, SW004, SW009, SW010, SW015, SW016, SW017, SW040, SW048, SW049, SW050, SW051.
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
