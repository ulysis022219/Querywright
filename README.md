# Querywright

A free, open-source productivity extension for **SQL Server Management Studio 22**.
It helps you write, format, check and refactor T-SQL faster, right inside the SSMS query editor.

- **No AI.** Everything is plain, predictable code.
- **Never runs your SQL on its own.** Generated scripts open in a new window for you to review.
- **Private by default.** Credentials, query text and result data are never logged.
- **MIT licensed.**

> Tested automatically against SSMS 22 on every build. Found a bug? Please [report it](https://github.com/ulysis022219/SqlWorkbench/issues).

## Install

1. Download `Querywright-ssms22.zip` from the [latest release](https://github.com/ulysis022219/SqlWorkbench/releases/latest).
2. Close SSMS 22, unzip, and double-click **Install.cmd**.
3. Start SSMS 22 and open a query window.

Querywright tells you when a newer release is out (once a day; turn it off under
Tools > Options > Querywright > Updates). To update, install the new zip the same way; your
settings are kept. To uninstall, run `Install.cmd -Uninstall`. Details: [INSTALL.txt](docs/INSTALL.txt).

## What it does

**Writing SQL**
- Suggestions as you type: tables, views, columns and joins from the connected database.
  Press **Ctrl+Shift+D** to reload them after schema changes.
- Snippets: type a shortcut and press **Tab**, e.g. `ssf` + Tab gives `SELECT * FROM`.
  56 built in; add your own `.sql` files or share a team folder.
- Expand `*` into a column list, or pick the columns you want.
- **F12** jumps to where a variable, alias or CTE is declared; on a procedure, view or function it opens an ALTER script in a new query, including three-part names in another database on the same server (`OtherDb.dbo.Proc`).
- **Shift+F5** runs only the statement under the caret (only when you press it).

**Formatting**
- Format a selection, a whole document, or every `.sql` file in a folder.
- 13 style options with a live preview, saved to a file your team can share.
- Keyword casing, add/remove square brackets, qualify object names, insert semicolons.

**Checking code**
- 46 analysis rules, shown as squiggles while you type and in the Error List.
- Quick fixes for common issues, one at a time or all at once.
- Find unused variables and parameters, and invalid objects in the database (read-only).

**Refactoring** (always as a script you review, never executed)
- Rename a variable or table alias.
- Rename a database object together with the code that depends on it.
- Split a table, or wrap a selection as a stored procedure.

**Everyday helpers**
- Tab history: find and reopen queries you closed, even after a restart (toolbar button next to New Query).
- A colored strip and a `server · database` label on every query window, so you always know
  where you are connected. Right-click a server in Object Explorer > **Querywright: server color...**
  to pick its color, or write rules like `prod=Red;test=Orange` in Tools > Options.
- Highlight an object name, right-click > **Querywright: compare object with database...** to check it against
  another database on the same server: a message if identical, otherwise a side-by-side diff.
- Results grid right-click: copy as `IN (...)`, script rows as `INSERT`, open in Excel, save as CSV.

All commands are in the **Tools** menu under "Querywright". Settings are under
**Tools > Options > Querywright**. Full list: [features](docs/features.md) and [analysis rules](docs/analysis-rules.md).

## Build from source

Requires the .NET 10 SDK on Windows.

```powershell
dotnet run --project tests/Querywright.Checks                  # core checks
dotnet build src/Querywright.Ssms/Querywright.Ssms.csproj      # builds the .vsix
powershell -NoProfile -File scripts/Test-Package.ps1           # checks the package contents
```

The package is written to `src/Querywright.Ssms/bin/Debug/net472/Querywright.Ssms.vsix`.

- `src/Querywright.Core`: parsing, formatting, analysis and refactoring (.NET Standard 2.0, no SSMS dependency).
- `src/Querywright.Ssms`: the SSMS extension (.NET Framework 4.7.2).
- `tests/Querywright.Checks`: the core checks.
- `scripts/Test-Ssms.ps1`: the end-to-end tests that drive a real SSMS 22 in CI.

Every push builds the extension and runs the SSMS tests on GitHub Actions. To publish a release,
run the **Build VSIX** workflow with a tag such as `v0.4.0`; it is published only if the tests pass.

## Contributing

Issues and pull requests are welcome. When reporting a bug, use the error line Querywright shows
(version, command and error type). Please never paste passwords, server names, real queries or data.

Ground rules for changes: no AI features, no automatic query execution or database changes,
and no logging of credentials, query text or result data.

## License

[MIT](LICENSE). Querywright is an independent project, not affiliated with Microsoft.
