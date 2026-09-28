# Static analysis coverage

Initial four rules; this is not full SQL Prompt rule parity. Source reference:
https://documentation.red-gate.com/codeanalysis/code-analysis-for-sql-server/best-practice-rules

| Our ID | Behavior | Related Redgate rule | Evidence |
|---|---|---|---|
| SW001 | Wildcard in output list | BP005 | Wildcard flagged; COUNT(*) excluded |
| SW002 | INSERT lacks explicit target columns | BP004 | Column list and DEFAULT VALUES excluded |
| SW003 | Direct or parenthesized NULL comparison | BP011 subset | IS NULL excluded; expressions inside strings/comments ignored |
| SW004 | CASE without fallback expression | BP012 | Both CASE forms; explicit ELSE excluded |

These are review hints, not proof of incorrect SQL. No automatic fixes implemented.
Per-rule severity and disabling use shared XML settings; inline suppression remains pending. Our diagnostic
messages and code are independently authored. Do not claim identical edge-case behavior.

Parsing uses SQL Server 2025 grammar, QUOTED_IDENTIFIER ON. Syntax errors are returned
separately and prevent rule evaluation for that batch; zero rule hints after a parse
failure never means clean SQL. Dynamic SQL strings are not recursively analyzed.
Cancellation is checked before/after parsing; ScriptDOM parse itself is synchronous.
The host runs analysis off the UI thread and rejects stale results.
Client GO repetition is supported by formatting, but analysis still reports a parse error
for GO with a repetition count. SQLCMD commands are not supported yet.

Full catalog work remains: each current rule needs its own mapping, scope, positive and
negative fixtures, severity defaults and supported fixes. Metadata-dependent rules
require explicit, permission-aware database inspection before they can be verified.
