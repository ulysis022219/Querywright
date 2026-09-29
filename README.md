# Querywright

Independent open-source SQL productivity extension for SSMS, in development.
Target: latest stable SSMS (22.10.1, verified September 28, 2026).
Goal: all non-AI SQL Prompt capabilities. No affiliation with Redgate.

## Current status

Snippet expansion and 27 static analysis rules implemented and checked. The VSIX has
Tools commands for snippet insertion and SQL document analysis, plus a package smoke
check. Editor wiring compiles but has not run in SSMS. No installed extension,
live autocomplete, database access, or verified SSMS integration yet. Offline completion,
formatting core and
selection/document command are implemented; runtime behavior remains unverified.
See [feature inventory](docs/features.md) and the [SQL Prompt parity roadmap](docs/sql-prompt-parity.md).

## Run checks

Requires .NET 10 SDK:

```powershell
dotnet run --project tests/Querywright.Checks
```

The core targets .NET Standard 2.0 for reuse from the Windows/.NET Framework SSMS host.
The checks use .NET 10; they do not prove SSMS runtime compatibility.

## Build the development VSIX

```powershell
dotnet build src/Querywright.Ssms/Querywright.Ssms.csproj
powershell -NoProfile -File scripts/Test-Package.ps1
```

Output: `src/Querywright.Ssms/bin/Debug/net472/Querywright.Ssms.vsix`.
Build does not install the package or start SSMS. The manifest targets SSMS 22 x64;
installer recognition and runtime loading are not yet verified. SSMS updates may
require compatibility changes. Microsoft does not officially support third-party extensions.

After the user's SSMS setup finishes, validate the VSIX with SSMS 22's installer,
then start a separate SSMS 22 session. Under Tools, run **Querywright: snippet smoke
check** and verify the preview appears. Confirm the command does not modify a query,
and uninstall the package to verify clean removal. Do not interrupt SSMS 19 sessions.

Validation on September 28, 2026:
- `dotnet run --project tests/Querywright.Checks`: 120 checks passed (0.2.0 core).
- `dotnet build src/Querywright.Ssms/Querywright.Ssms.csproj --no-restore`:
  VSIX generated, zero warnings/errors.
- Archive inspected: extension/core assemblies, pkgdef and manifest included.
- SSMS installation, menu visibility, runtime behavior and uninstall: pending.

## SQL Prompt-style editor behavior (0.2.0, unverified in SSMS)

These run inside the SQL editor itself rather than through the Tools menu. The package now
autoloads in the background so they are active as soon as a query window opens.

- **Snippet shortcuts:** type a shortcut then press Tab, e.g. `ssf` + Tab gives `SELECT * FROM `.
  The shortcut is the snippet file name (`ssf.sql`, `sst.sql`, `scf.sql`, `ii.sql`, `ups.sql`,
  `df.sql`, `be.sql` are seeded; existing files are kept). Add your own `name.sql` to the snippet folder.
  Words that are not shortcuts, and Tab while a completion list is open, behave normally.
- **Wildcard expansion:** with the caret right after `*` (or `alias.*`), Tab replaces it with
  the column list from the offline schema. Also on Tools > Querywright: expand wildcard.
- **F12 go to definition:** variables and parameters jump to their DECLARE, aliases to their
  FROM entry, CTE names to the WITH clause. Tables, views and procedures fall through to SSMS's
  own F12 for the connected database.
- **Completion popup while typing:** tables after FROM/JOIN and columns after SELECT or `alias.`,
  from the offline schema file. Soft-selected after a space so Enter still inserts a newline.
  SSMS's built-in IntelliSense may show its own list alongside; live metadata is still pending.
- **Connection and environment colors:** the bottom right of each connected query editor shows
  `server · database`. Tools > Options > Querywright > Tab color rules (e.g. `prod=Red;test=Orange`,
  matched against `server/database`) colors that label and a strip above the editor.
- **Insert semicolons:** Tools > Querywright: insert semicolons terminates every statement,
  including inside IF/ELSE, TRY/CATCH and procedure bodies. Only semicolons change; verified by tokens.

Pending runtime checks: Tab/F12 command routing in the SSMS 22 SQL editor, async completion
coexistence with native IntelliSense, and single undo for each expansion.

## Snippets and analysis prototype

Tools > Querywright: insert snippet creates starter `.sql` templates in the local
snippet folder and opens a file picker. Existing template content is preserved.
Edit the files in any editor; Tools > Options > Querywright > General changes the
folder, including to a team share. CURSOR, selection markers, DATE, TIME, MACHINE,
and PASTE are implemented. SERVER, DBNAME, and USER require connection integration;
insertion rejects templates requesting unavailable values before modifying SQL.

Tools > Querywright: analyze SQL document runs 46 syntax-tree rules in the background
and publishes results to Error List. Double-click navigation rejects stale snapshots.
See [analysis coverage](docs/analysis-rules.md). No SQL is executed.

Pending runtime checks: insertion at caret and over selection, Unicode/CRLF offsets,
single undo/redo, read-only buffers, picker cancellation, invalid template rejection,
analysis navigation, edited/closed queries, and UI responsiveness.

## Formatting and team settings

Tools > Querywright: format SQL selection/document formats selected complete SQL
statements, or the full document when selection is empty. One undo transaction.
Original text remains unchanged when parsing, protected-token checks or normalized
SQL structure checks fail. These checks reduce accidental changes; they do not prove
semantic equivalence for every supported SQL construct.

Formatter handles ordinary GO separators and repetition counts; SQLCMD directives
and incomplete statement selections remain unsupported. It uses ScriptDOM with
SQL Server 2025 grammar and QUOTED_IDENTIFIER ON. Tools > Querywright: formatting style...
edits 13 options (indentation, keyword case, commas, clause line breaks, alignment, list
layout, semicolons) with a live preview and saves them to the settings file (created under
%LocalAppData%\Querywright when none is set; point teammates at a shared copy).

Tools > Querywright: format SQL files in folder... formats every .sql file under a folder
(hidden, system and linked folders skipped). It previews the count and asks before writing;
files keep their encoding and line endings; non-UTF-8/UTF-16, oversized or unparsable files are
left untouched and listed in a report opened in a new window. There is no undo, so use
source control. Per-connection dialect remains pending.

Copy [example settings](examples/team-settings.xml) to a local/shared file. Set its
path under Tools > Options > Querywright > General > Settings file. Rule values:
Disabled, Info, Warning, Error. Parse errors cannot be disabled. Settings are read
on each invocation off the UI thread; invalid settings stop the operation.

## Offline completion prototype

Set **Offline schema SQL file** in Tools > Options > Querywright to a script containing
only CREATE TABLE statements (see examples/offline-schema.sql). No SQL is executed.
At an identifier or after an alias dot, run **suggest from offline schema**. The picker
inserts the chosen table/column with brackets and one undo transaction. Metadata is
read fresh on invocation; it is a user-supplied snapshot, not current database state.

Core checks cover ordinary joins, nested/correlated query scopes, alias shadowing,
CTEs and derived columns, and suppress suggestions inside strings/comments. The
host uses dbo as default schema and ordinal case-insensitive matching for now.
Remaining: connection-aware metadata/cache, collation/default-schema discovery,
native inline popup, quoted partial identifiers, broader incomplete-SQL recovery,
APPLY correlation, temporary tables, variables, procedure parameters, cross-database
names, CTE/derived wildcard expansion and fuzzy matching. These remain in full scope.

## Local variable rename prototype

Place the caret on a locally declared variable, then run **rename local variable**.
Enter a new name, inspect Original/Proposed SQL in the preview, and Apply. The editor
change uses one undo transaction and rejects a stale document. No SQL is executed.

The core binds within one parsed batch, including table variables, and preserves
comments, strings, other batches, and named EXEC parameter labels. Duplicate declarations,
name collisions, malformed SQL and public procedure/function parameters are rejected.
Matching currently uses ordinal case-insensitive names; collation-aware binding and
broader refactorings remain pending. Dynamic SQL string contents are not rewritten.
Core checks passed; preview, apply, undo and redo still need actual SSMS tests.

## Development order

Host validation (September 29, 2026): VSIX 0.1.1 installs on SSMS 22.10.1;
six Tools commands appear and snippet smoke command runs. Document formatting,
single-step undo and redo passed with `examples/host-smoke.sql` in a disconnected
editor. Other commands and full feature compatibility remain unverified.
Run `powershell -NoProfile -File scripts/Test-HostParser.ps1` after building to catch
ScriptDOM API differences in the installed SSMS host; the host can override the
parser assembly bundled in the VSIX.

1. Inventory current commands and analysis rules; map each to acceptance checks.
2. Prove SSMS package loading, active SQL document access, undo, and clean uninstall.
3. Wire snippets; add cached metadata and context-aware completion.
4. Formatting, navigation, editor helpers, history and result tools.
5. Analysis and refactoring with reviewable edits and dependency-aware validation.
6. Complete inventory gaps and run actual SSMS regression tests.

No AI features. No automatic query execution or database mutation.
Database credentials and query text must never enter logs by default.
New project code is MIT licensed; dependencies require their own license review.
