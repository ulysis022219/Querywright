# Querywright

A free, open-source productivity extension for **SQL Server Management Studio 22**.
It helps you write, format, check and refactor T-SQL faster, right inside the SSMS query editor.

**Looking for a free alternative to Redgate SQL Prompt?** Querywright is a free SQL Prompt alternative
for SSMS 22: SQL IntelliSense and autocomplete, a T-SQL formatter / SQL beautifier, snippets,
code analysis, refactoring, tab history and server colors, with no license or subscription.

- **No AI.** Everything is plain, predictable code.
- **Never runs your SQL on its own.** Generated scripts open in a new window for you to review.
- **Private by default.** Credentials, query text and result data are never logged.
- **MIT licensed.**

> Tested automatically against SSMS 22 on every build. Found a bug? Please [report it](https://github.com/ulysis022219/Querywright/issues).

## Install

1. Download `QueryWright_v<version>.zip` from the [latest release](https://github.com/ulysis022219/Querywright/releases/latest)
   (or just the `.vsix` next to it if you prefer installing it yourself).
2. Close SSMS 22, unzip, and double-click **Install.cmd**.
3. Start SSMS 22 and open a query window.

Querywright tells you when a newer release is out (once a day; turn it off under
Tools > Options > Querywright > Updates; check now with Querywright > **Check for updates...**).
Click **Install update** in the notice: Querywright
downloads and verifies the release, then waits for you to save your work and close SSMS.
The updater installs it automatically; reopen SSMS when it reports success. Your settings are kept.
Older versions that only show **Download** need one manual update to get this feature.
To uninstall, close SSMS and double-click `Uninstall.cmd`.

## What it does

**Writing SQL**
- Suggestions as you type: tables, views, columns and joins from the connected database. Columns are grouped per table in the current query scope (innermost subquery first), including UPDATE aliases, INSERT column lists and MERGE inserts.
  Press **Ctrl+Shift+D** to reload them after schema changes. Names and types are cached on disk (hashed file names, never credentials or data) so suggestions work right after a restart; turn it off with "Cache schema on disk". Very large databases load up to 100,000 columns by default; raise or remove the cap with "Live metadata row limit" (0 = no limit).
- Snippets: type a shortcut and press **Tab**, e.g. `ssf` + Tab gives `SELECT * FROM`. Lowercase `$name$` placeholders in a template are fields: Tab selects the next one, then goes to `$CURSOR$`; Esc stops.
  56 built in; add your own `.sql` files or share a team folder.
- Typing `'` inserts `''` with the cursor inside; typing `'` again steps over the closing quote (turn off under Tools > Options > Querywright).
- Expand `*` into a column list, or pick the columns you want.
- **F12** jumps to where a variable, alias or CTE is declared; on a procedure, view, function or trigger it opens an ALTER script in a new query and on a table a CREATE TABLE script (also in scripts with syntax errors elsewhere), including three-part names in another database on the same server (`OtherDb.dbo.Proc`).
- **BEGIN/END pairs** are colored by nesting level (BEGIN/END, BEGIN TRY/END TRY, CASE/END, with their IF, WHILE or ELSE); the pair at the caret is highlighted, **Ctrl+]** jumps between them, and hovering an END shows the line that opened it. A BEGIN, CASE, END or parenthesis with no partner is underlined as you type.
- **Parameter hints**: after `EXEC dbo.Proc ` or `dbo.fn(` a tooltip lists the parameters with the current one in bold.
- **Folding**: `--region name` / `--endregion` sections and multi-line BEGIN/END blocks collapse from the margin.
- **Shift+F5** runs only the statement under the caret (only when you press it).
- Executing a **DELETE or UPDATE without WHERE** asks first ("Execute anyway" / "Don't execute"); turn it off in the prompt or under Tools > Options > Querywright.
- **Production servers**: list patterns such as `prod;live` in Tools > Options > Querywright; on a matching connection, executing DROP, ALTER, TRUNCATE or an unfiltered DELETE/UPDATE always asks first, and Run in multiple databases names matching databases in its prompt. #temp tables never trigger these prompts.

**Formatting**
- Format a selection, a whole document, or every `.sql` file in a folder. Optional **Format on save** (off by default) formats the document on Ctrl+S, and every unsaved SQL document on Save All.
- 13 style options with a live preview, saved to a file your team can share.
- Keyword casing, add/remove square brackets, qualify object names, insert semicolons.

**Checking code**
- Hovering a column shows its type, NULL/NOT NULL and default from the cached metadata.
- 46 analysis rules, shown as squiggles while you type and in the Error List. Turn rules off or change their severity in **Formatting style and rules...** > Analysis rules (saved to the shared settings file).
- Quick fixes for common issues, one at a time or all at once.
- Find unused variables and parameters, and invalid objects in the database (read-only).

**Refactoring** (always as a script you review, never executed)
- Rename a variable or table alias.
- Rename a database object together with the code that depends on it.
- Split a table, or wrap a selection as a stored procedure.

**Everyday helpers**
- Tab history: every SQL window keeps timestamped versions (a few seconds after each edit, and on execute), grouped per tab with a preview; search covers every saved version; reopen any version, even after a restart (toolbar button next to New Query). Keeps the newest 100 versions per tab, 200 tabs and 256 MB in all (favorites aside); PASSWORD and SECRET literals, password variables and the arguments of password procedures (sp_addlogin, sp_password, sp_addlinkedsrvlogin and similar) are saved as `***`.
- Select several cells in a results grid to see count, sum, average, min and max in the status bar.
- A colored strip and a `server · database` label on every query window, so you always know
  where you are connected. Right-click a server in Object Explorer > **Querywright: server color...**
  to pick its color, or write rules like `prod=Red;test=Orange` in Tools > Options.
- Highlight an object name, right-click > **Querywright: compare object with database...** to check it against
  another database on the same server: a message if identical, otherwise a side-by-side diff.
- Results grid right-click: copy as `IN (...)`, script rows as `INSERT`, open in Excel, save as CSV.

All commands are in the **Tools** menu under "Querywright". Settings are under
**Tools > Options > Querywright**; every feature above can be switched off there. Full list: [features](docs/features.md) and [analysis rules](docs/analysis-rules.md).

## Build from source

Requires the .NET 10 SDK on Windows.

```powershell
dotnet run --project tests/Querywright.Checks                  # core checks
dotnet build src/Querywright.Ssms/Querywright.Ssms.csproj      # builds the .vsix
powershell -NoProfile -File scripts/Test-Package.ps1           # checks the package contents
powershell -NoProfile -File scripts/Test-Update.ps1            # update validation, no installation
```

The package is written to `src/Querywright.Ssms/bin/Debug/net472/Querywright.Ssms.vsix`.

- `src/Querywright.Core`: parsing, formatting, analysis and refactoring (.NET Standard 2.0, no SSMS dependency).
- `src/Querywright.Ssms`: the SSMS extension (.NET Framework 4.7.2).
- `tests/Querywright.Checks`: the core checks.
- `scripts/Test-Ssms.ps1`: the end-to-end tests that drive a real SSMS 22 in CI.

Every push builds the extension and runs the SSMS tests on GitHub Actions. To publish a release,
run the **Build VSIX** workflow with a tag such as `v0.4.0`; it is published only if the tests pass, with SHA-256 checksums of the zip and .vsix in the release notes.

## Contributing

Issues and pull requests are welcome. When reporting a bug, use the error line Querywright shows
(version, command and error type). Please never paste passwords, server names, real queries or data.

Ground rules for changes: no AI features, no automatic query execution or database changes,
and no logging of credentials, query text or result data.

## License

[MIT](LICENSE). Querywright is an independent project, not affiliated with or endorsed by Microsoft or Redgate.
SQL Prompt is a trademark of Redgate Software Ltd; SQL Server Management Studio is a trademark of Microsoft.
Names are used only to describe compatibility and comparison.

<sub>Keywords: free SQL Prompt alternative, SQL Prompt free, SSMS extension, SSMS 22 add-in, SSMS plugin,
SQL IntelliSense, SQL autocomplete, T-SQL formatter, SQL formatter for SSMS, SQL beautifier, SQL snippets,
SQL code analysis, T-SQL linter, SQL refactoring, SSMS tab history, SSMS server colors, open source.</sub>
