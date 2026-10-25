using System.Text;
using System.Text.RegularExpressions;
using Querywright.Core;

var now = new DateTimeOffset(2026, 9, 28, 13, 45, 12, TimeSpan.FromHours(8));
var context = new Dictionary<string, string>
{
    ["DBNAME"] = "TestDb", ["SERVER"] = "localhost",
    ["USER"] = "tester", ["MACHINE"] = "dev", ["PASTE"] = "$CURSOR$"
};
int checks = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new Exception("FAIL: " + name);
    checks++;
}
void Reject(string template)
{
    try { Snippets.Expand(template, context, now); }
    catch (FormatException) { checks++; return; }
    throw new Exception("Expected FormatException for " + template);
}

var expanded = Snippets.Expand("SELECT $SELECTIONSTART$name$SELECTIONEND$ FROM $DBNAME$;$CURSOR$", context, now);
Check(expanded.Text == "SELECT name FROM TestDb;", "SQL expansion");
Check(expanded.Caret == expanded.Text.Length, "caret position");
Check(expanded.SelectionStart == 7 && expanded.SelectionLength == 4, "selection range");
Check(Snippets.Expand("$DATE$ $TIME(HH:mm)$", context, now).Text == "2026-09-28 13:45", "deterministic dates");
Check(Snippets.Expand("$PASTE$", context, now).Text == "$CURSOR$", "nonrecursive context insertion");
Check(Snippets.Expand("$UNKNOWN$ N'日本語'\r\n", context, now).Text == "$UNKNOWN$ N'日本語'\r\n", "literal preservation");
Check(Snippets.Expand("$CURSOR$😀", context, now).Caret == 0, "leading caret");
Check(Snippets.Expand("", context, now).Text == "", "empty snippet");
Reject("$CURSOR$$CURSOR$");
Reject("$SELECTIONEND$$SELECTIONSTART$");
Reject("$SELECTIONSTART$x");
Reject("$DBNAME(x)$");
Reject("$DATE()$");
context.Remove("SERVER");
Reject("$SERVER$");
context["SELECTEDTEXT"] = "$PASTE$ $CURSOR$";
var wrapped = Snippets.Expand("BEGIN $SELECTEDTEXT$$CURSOR$ END", context, now);
Check(wrapped.Text == "BEGIN $PASTE$ $CURSOR$ END" && wrapped.Caret == 22, "nonrecursive selected text");
context["SELECTEDTEXT"] = "";
Check(Snippets.Expand("[$SELECTEDTEXT$]", context, now).Text == "[]", "empty selected text");
Reject("$SELECTEDTEXT(x)$");
context.Remove("SELECTEDTEXT");
Reject("$SELECTEDTEXT$");
Console.WriteLine($"PASS: {checks} snippet checks. SSMS integration not tested.");

var folder = Path.Combine(Path.GetTempPath(), "UlysiSQL-check-" + Guid.NewGuid().ToString("N"));
try
{
    SnippetFiles.Initialize(folder);
    var path = Path.Combine(folder, "Select.sql");
    Check(SnippetFiles.Read(path).Contains("$SELECTIONSTART$"), "default template");
    File.WriteAllText(path, "SELECT N'custom 日本語';");
    SnippetFiles.Initialize(folder);
    Check(SnippetFiles.Read(path) == "SELECT N'custom 日本語';", "preserve user template");
    File.WriteAllText(path, new string('x', 1_000_001));
    try { SnippetFiles.Read(path); throw new Exception("Expected size rejection"); }
    catch (IOException) { checks++; }
}
finally { Directory.Delete(folder, true); }
Console.WriteLine($"PASS: {checks} total checks including snippet files. SSMS integration not tested.");

var library = Path.Combine(Path.GetTempPath(), "UlysiSQL-library-" + Guid.NewGuid().ToString("N"));
try
{
    SnippetFiles.Initialize(library);
    var shortcuts = ("ssf sst ss0 st100 scf sd smf ii df ij lj rj fj cj j loj roj foj gb ob be bt " +
        "ctr rt tc cte ct ctt cv cp csf ctf citf at ata atd ac ap af dt dp dv dfn di " +
        "inn lk isns isnn rnum cw ifs today trim sph spt w2").Split(' ');
    Check(shortcuts.All(s => File.Exists(Path.Combine(library, s + ".sql"))), "seeded SQL Prompt shortcuts");
    File.WriteAllText(Path.Combine(library, "ssf.sql"), "SELECT 'mine';");
    SnippetFiles.Initialize(library);
    Check(File.ReadAllText(Path.Combine(library, "ssf.sql")) == "SELECT 'mine';", "seed does not overwrite shortcut");
    File.Delete(Path.Combine(library, "ssf.sql"));
    SnippetFiles.Initialize(library);

    var snippetContext = new Dictionary<string, string>(context) { ["SERVER"] = "localhost", ["SELECTEDTEXT"] = "SELECT 1;" };
    var fragments = new Dictionary<string, string>
    {
        ["ssf"] = "dbo.T;", ["sst"] = "dbo.T;", ["ss0"] = "dbo.T", ["st100"] = "dbo.T;", ["scf"] = "dbo.T;",
        ["df"] = "dbo.T WHERE Id = 1;",
        ["cj"] = "dbo.B b", ["gb"] = "x", ["ob"] = "x", ["cte"] = "SELECT 1 AS x", ["ct"] = "Name nvarchar(50) NULL", ["ctt"] = "Name nvarchar(50) NULL",
        ["cv"] = "SELECT 1 AS x;", ["csf"] = "@param + 1", ["af"] = "@param + 1", ["ctf"] = "INSERT @result VALUES (@param);",
        ["citf"] = "SELECT @param AS Id", ["at"] = "dbo.T ADD x int NULL", ["ata"] = "x int NULL", ["atd"] = "x", ["ac"] = "x bigint NOT NULL",
        ["dt"] = "dbo.X", ["dp"] = "dbo.X", ["dv"] = "dbo.X", ["dfn"] = "dbo.X", ["lk"] = "abc",
        ["isns"] = "x", ["isnn"] = "x", ["rnum"] = "x", ["cw"] = "x = 1", ["trim"] = "x", ["ifs"] = "dbo.T", ["sph"] = "dbo.T", ["spt"] = "dbo.T",
    };
    var wraps = new Dictionary<string, string> { ["cj"] = "SELECT * FROM dbo.A a {0};", ["gb"] = "SELECT x, COUNT(*) FROM dbo.T {0};",
        ["ob"] = "SELECT x FROM dbo.T {0};", ["inn"] = "SELECT * FROM dbo.T WHERE x {0};", ["lk"] = "SELECT * FROM dbo.T WHERE x {0};" };
    foreach (var join in new[] { "ij", "lj", "rj", "fj", "j", "loj", "roj", "foj" })
    {
        fragments[join] = "dbo.B b";
        wraps[join] = "SELECT * FROM dbo.A a {0}a.Id = b.Id;";
    }
    foreach (var expression in new[] { "isns", "isnn", "rnum", "cw", "today", "trim" }) wraps[expression] = "SELECT {0} FROM dbo.T;";
    foreach (var path in Directory.GetFiles(library, "*.sql"))
    {
        var name = Path.GetFileNameWithoutExtension(path);
        var expansion = Snippets.Expand(SnippetFiles.Read(path), snippetContext, now);
        Check(!expansion.Text.Contains('$'), "expand seeded " + name);
        var sql = expansion.Text.Insert(expansion.Caret, fragments.GetValueOrDefault(name, ""));
        new Microsoft.SqlServer.TransactSql.ScriptDom.TSql170Parser(true).Parse(new StringReader(wraps.GetValueOrDefault(name, "{0}").Replace("{0}", sql)), out var errors);
        Check(errors.Count == 0, "parse seeded " + name);
    }

    var fielded = Snippets.Expand("SELECT $a$ FROM $tbl$;$CURSOR$", snippetContext, now);
    Check(fielded.Text == "SELECT a FROM tbl;" && fielded.Fields.SequenceEqual(new[] { (7, 1), (14, 3) }) && fielded.Caret == 18, "snippet fields keep names as default text");

    var listed = SnippetFiles.List(library);
    Check(listed.Select(p => p.Key).SequenceEqual(shortcuts.Append("Select").Order(StringComparer.Ordinal)), "list valid seeded shortcuts in ordinal order");
    Check(listed.Single(p => p.Key == "ssf").Value == "SELECT * FROM $CURSOR$", "list first line");
    Check(listed.Single(p => p.Key == "tc").Value == "BEGIN TRY", "list multiline first line");
}
finally { Directory.Delete(library, true); }

var listFolder = Path.Combine(Path.GetTempPath(), "UlysiSQL-list-" + Guid.NewGuid().ToString("N"));
try
{
    Check(SnippetFiles.List(listFolder).Count == 0, "list missing folder");
    Directory.CreateDirectory(listFolder);
    File.WriteAllText(Path.Combine(listFolder, "b_1.sql"), "\r\n   \r\n  first line  \r\nsecond");
    File.WriteAllText(Path.Combine(listFolder, "long.sql"), new string('x', 200));
    File.WriteAllText(Path.Combine(listFolder, "emoji.sql"), new string('a', 79) + "😀tail");
    File.WriteAllText(Path.Combine(listFolder, "empty.sql"), "");
    File.WriteAllText(Path.Combine(listFolder, "bad-name.sql"), "x");
    File.WriteAllText(Path.Combine(listFolder, "a b.sql"), "x");
    File.WriteAllText(Path.Combine(listFolder, "notes.sqlx"), "x");
    File.WriteAllText(Path.Combine(listFolder, "huge.sql"), new string('x', 4_000_001));
    File.WriteAllBytes(Path.Combine(listFolder, "invalid.sql"), new byte[] { 0xC3, 0x28 });
    var small = SnippetFiles.List(listFolder).ToDictionary(p => p.Key, p => p.Value);
    Check(small.Keys.Order(StringComparer.Ordinal).SequenceEqual(new[] { "b_1", "emoji", "empty", "long" }), "list filters names, size and encoding");
    Check(small["b_1"] == "first line" && small["empty"] == "", "list skips blank lines");
    Check(small["long"].Length == 80 && small["emoji"] == new string('a', 79), "list truncates to 80 without splitting surrogates");
    for (int i = 0; i < 510; i++) File.WriteAllText(Path.Combine(listFolder, $"s{i:D3}.sql"), "x");
    var capped = SnippetFiles.List(listFolder);
    Check(capped.Count == 500 && capped[0].Key == "b_1" && capped.Select(p => p.Key).SequenceEqual(capped.Select(p => p.Key).Order(StringComparer.Ordinal)), "list cap and order");
}
finally { Directory.Delete(listFolder, true); }
Console.WriteLine($"PASS: {checks} total checks including snippet library. SSMS integration not tested.");

var issues = SqlAnalysis.Analyze("SELECT * FROM dbo.T; INSERT dbo.T VALUES (1); SELECT CASE WHEN x = NULL THEN 1 END FROM dbo.T;");
Check(issues.Parsed, "parse valid batch");
Check(issues.Diagnostics.Select(d => d.Rule).Order().SequenceEqual(new[] { "SW001", "SW002", "SW003", "SW004" }), "four independent rules");
var cleanSql = "SELECT COUNT(*) FROM dbo.T WHERE x IS NULL; INSERT dbo.T (x) VALUES (1); INSERT dbo.T DEFAULT VALUES; SELECT CASE x WHEN 1 THEN 2 ELSE 3 END FROM dbo.T;";
Check(SqlAnalysis.Analyze(cleanSql).Diagnostics.Count == 0, "negative fixtures");
Check(SqlAnalysis.Analyze("SELECT N'SELECT * WHERE x = NULL'; -- SELECT *\n").Diagnostics.Count == 0, "comments and strings ignored");
Check(!SqlAnalysis.Analyze("SELECT FROM").Parsed, "parse failure distinguished");
var positioned = SqlAnalysis.Analyze("-- line\r\nSELECT t.* FROM dbo.T t;").Diagnostics.Single();
Check(positioned.Line == 2 && positioned.Column == 8 && positioned.Length == 3, "source location");
Check(SqlAnalysis.Analyze("SELECT 1 WHERE x = (NULL)").Diagnostics.Any(d => d.Rule == "SW003"), "parenthesized NULL");
using var canceled = new CancellationTokenSource();
canceled.Cancel();
try { SqlAnalysis.Analyze("SELECT *", canceled.Token); throw new Exception("Expected cancellation"); }
catch (OperationCanceledException) { checks++; }
var peopleCatalog = new[] { new SchemaTable("dbo", "People", new[] { "Id", "FullName" }) };
var peopleEdit = SqlCompletion.ExpandWildcard("SELECT *\r\nFROM dbo.People;", 7, peopleCatalog);
Check(peopleEdit.Text.Contains("FullName"), "wildcard before CRLF FROM: " + peopleEdit.Text);
Console.WriteLine($"PASS: {checks} total checks. SSMS integration not tested.");

var renameSql = "DECLARE @id int = 1; SELECT @id; -- @id\r\nEXEC dbo.P @id = @id; SELECT N'@id';\r\nGO\r\nDECLARE @id int; SELECT @id;";
var renamed = SqlRefactoring.RenameLocalVariable(renameSql, renameSql.IndexOf("@id"), "@personId");
Check(renamed.Changes == 3, "rename bound occurrences only");
Check(renamed.Text.Contains("EXEC dbo.P @id = @personId"), "preserve formal EXEC parameter");
Check(renamed.Text.Contains("-- @id\r\n") && renamed.Text.Contains("N'@id'") && renamed.Text.EndsWith("DECLARE @id int; SELECT @id;"), "rename comments strings and batch isolation");
Check(SqlRefactoring.RenameLocalVariable("DECLARE @t TABLE (id int); SELECT id FROM @t;", 10, "@rows").Changes == 2, "table variable rename");
try { SqlRefactoring.RenameLocalVariable("DECLARE @a int, @b int; SELECT @a;", 10, "@b"); throw new Exception("Expected collision rejection"); }
catch (InvalidOperationException) { checks++; }
try { SqlRefactoring.RenameLocalVariable("DECLARE @a int;", 10, "@a; DROP TABLE dbo.T; --"); throw new Exception("Expected invalid name rejection"); }
catch (ArgumentException) { checks++; }
try { SqlRefactoring.RenameLocalVariable("CREATE PROCEDURE dbo.P @a int AS SELECT @a;", 40, "@b"); throw new Exception("Expected public parameter rejection"); }
catch (InvalidOperationException) { checks++; }
Console.WriteLine($"PASS: {checks} total checks including local variable rename. SSMS integration not tested.");

string formatInput = "-- keep\r\nselect Name, N'日本語' as label from dbo.T where id=42;\r\nGO\r\n";
string formatted = SqlFormatting.Format(formatInput);
Check(formatted.Contains("-- keep") && formatted.Contains("N'日本語'") && formatted.Contains("42"), "format protected content");
Check(SqlFormatting.Format(formatted) == formatted, "format idempotence");
Check(SqlFormatting.Format("select 1;", new FormattingStyle { LowercaseKeywords = true }).Contains("select"), "format style");
try { SqlFormatting.Format("SELECT FROM"); throw new Exception("Expected parse rejection"); }
catch (FormatException) { checks++; }
Check(SqlFormatting.Format(" \r\n") == " \r\n", "whitespace document preserved");
var settingsFile = Path.GetTempFileName();
try
{
    File.WriteAllText(settingsFile, "<WorkbenchSettings><SW001>Disabled</SW001><Formatting><IndentSize>2</IndentSize></Formatting></WorkbenchSettings>");
    var settings = WorkbenchSettings.Load(settingsFile);
    Check(settings.Severity("SW001") == RuleSeverity.Disabled && settings.Formatting.IndentSize == 2, "shared settings");
    Check(settings.Severity("PARSE46010") == RuleSeverity.Error, "parse errors mandatory");
    Check(SqlAnalysis.Analyze("SELECT * FROM dbo.T", settings: settings).Diagnostics.Count == 0, "disabled rule not emitted");
    File.WriteAllText(settingsFile, "<!DOCTYPE x [<!ENTITY e SYSTEM 'file:///no-such-file'>]><WorkbenchSettings>&e;</WorkbenchSettings>");
    try { WorkbenchSettings.Load(settingsFile); throw new Exception("Expected DTD rejection"); }
    catch (InvalidOperationException) { checks++; }
}
finally { File.Delete(settingsFile); }
Console.WriteLine($"PASS: {checks} total checks including formatting and settings. SSMS integration not tested.");

foreach (string sample in new[]
{
    "CREATE PROCEDURE dbo.P @id int AS BEGIN SET NOCOUNT ON; SELECT @id; END;",
    "SELECT N'a''b', [select] FROM [odd name];",
    "WITH c AS (SELECT id FROM dbo.T) SELECT c.id FROM c;",
    "IF 1 = 1 BEGIN SELECT 2; END ELSE BEGIN SELECT 3; END;",
    "SELECT 1;\nGO 3\nSELECT 2;\nGO\n",
    "SELECT N'line1\nline2';",
    "SELECT a.id FROM dbo.A a INNER JOIN dbo.B b ON a.id = b.id;",
    "-- header\nSELECT /* keep */ 1; -- tail\n"
})
{
    string output;
    try { output = SqlFormatting.Format(sample); }
    catch (Exception e) { throw new Exception("Formatting fixture failed: " + sample, e); }
    Check(sample.Contains("GO 3") ? output.Contains("GO 3") : SqlAnalysis.Analyze(output).Parsed, "format corpus and batch repeat");
    Check(SqlFormatting.Format(output) == output, "format corpus stable");
}
Console.WriteLine($"PASS: {checks} total checks including formatting corpus. SSMS integration not tested.");

var catalog = new[] { new SchemaTable("dbo", "People", "Id", "Name", "odd]column"), new SchemaTable("dbo", "Orders", "OrderId", "PersonId") };
CompletionResult Complete(string text)
{
    int position = text.IndexOf('|');
    return SqlCompletion.Complete(text.Remove(position, 1), position, catalog);
}
Check(Complete("SELECT p.| FROM dbo.People p;").Items.Count == 3, "alias columns");
var otherCatalog = new[] { new SchemaTable("sales", "Orders", "OrderId", "Total"), new SchemaTable("hr", "Staff", "Id") };
CompletionResult CompleteCross(string text, params string[] databases)
{
    int at = text.IndexOf('|');
    return SqlCompletion.Complete(text.Remove(at, 1), at, catalog, databases: databases, otherDatabase: name => name == "Other" ? otherCatalog : null);
}
Check(CompleteCross("SELECT * FROM Other.|", "Other").Items.Select(i => i.Name).OrderBy(n => n).SequenceEqual(new[] { "hr", "sales" }), "cross-db schemas");
Check(CompleteCross("SELECT * FROM Other.sales.|", "Other").Items.Select(i => i.Name).SequenceEqual(new[] { "Orders" }), "cross-db tables");
Check(CompleteCross("SELECT * FROM Other.sales.Orders.|", "Other").Items.Count == 2, "cross-db columns");
Check(CompleteCross("SELECT * FROM dbo.People Other JOIN x ON Other.|", "Other").Items.Select(i => i.Name).Contains("Name"), "alias wins over database");
string blockSql = "WHILE @i < 10\nBEGIN\n  IF EXISTS(SELECT 1 FROM t) BEGIN SELECT CASE WHEN a=1 THEN 2 ELSE 3 END END\n  ELSE BEGIN PRINT 'end' END\nEND";
var blocks = SqlNavigation.Blocks(blockSql);
Check(blocks.Select(b => (blockSql.Substring(b.CloseStart, b.CloseLength), b.Depth, b.HeaderStart < 0 ? "" : blockSql.Substring(b.HeaderStart, b.HeaderLength)))
    .SequenceEqual(new[] { ("END", 0, "WHILE"), ("END", 1, "IF"), ("END", 2, ""), ("END", 1, "ELSE") }), "block pairs, depth and headers");
Check(blocks[0].CloseStart == blockSql.Length - 3, "outer END matched past strings and nested blocks");
string tryCatch = "BEGIN TRAN\nBEGIN TRY\n SELECT 1\nEND TRY\nBEGIN CATCH\n ROLLBACK\nEND CATCH\nCOMMIT";
Check(SqlNavigation.Blocks(tryCatch).Select(b => tryCatch.Substring(b.OpenStart, b.OpenLength) + "/" + tryCatch.Substring(b.CloseStart, b.CloseLength))
    .SequenceEqual(new[] { "BEGIN TRY/END TRY", "BEGIN CATCH/END CATCH" }), "TRY/CATCH blocks, BEGIN TRAN skipped");
Check(SqlNavigation.Blocks("IF 1=1 SELECT 1\nBEGIN\nEND\nGO\nEND\nBEGIN").Single().HeaderStart == -1, "IF without BEGIN; GO resets");
Check(SqlNavigation.Blocks("BEGIN -- END\n SELECT 'END', [end] FROM x\n/* END */ END").Single().CloseStart == 51, "END in comments, strings and names ignored");
var twin = new[] { new SchemaTable("dbo", "People", "Id", "Name"), new SchemaTable("dbo", "Orders", "Id", "Name") };
CompletionResult CompleteTwin(string text, bool qualify = true) { int at = text.IndexOf('|'); return SqlCompletion.Complete(text.Remove(at, 1), at, twin, qualifySingleTable: qualify); }
Check(CompleteTwin("SELECT * FROM dbo.People\nSELECT * FROM dbo.Orders WHERE Na|").Items[0].InsertText == "Orders.Name", "own statement's columns first");
Check(CompleteTwin("SELECT * FROM dbo.People p WHERE p.Id = 1\nSELECT * FROM dbo.Orders o WHERE Na| = 1 AND").Items[0].InsertText == "o.Name", "own statement's columns first without parse");
Check(CompleteTwin("SELECT * FROM dbo.People WHERE Na|", false).Items[0].InsertText == "Name", "single table column unqualified");
Check(CompleteTwin("SELECT * FROM dbo.People WHERE Na| = 1 AND", false).Items[0].InsertText == "Name", "single table column unqualified without parse");
Check(CompleteTwin("SELECT * FROM dbo.People p JOIN dbo.Orders o ON o.Id = p.Id WHERE Na|", false).Items.Select(i => i.InsertText).SequenceEqual(new[] { "p.Name", "o.Name" }), "join columns keep aliases");
Check(Complete("SELECT p.Na|me FROM dbo.People p;").Items.Single().InsertText == "Name", "partial identifier replacement");
Check(Complete("SELECT FROM WHERE;\nGO\nSELECT p.| FROM dbo.People p;\nGO\nSELECT 1").Items.Count == 3 &&
    Complete("SELECT 1\nGO\nSELECT p.| FROM dbo.People p;\nGO\nSELECT FROM WHERE;").Items.Count == 3, "a syntax error in another batch keeps column resolution");
Check(Complete("SELECT p.| FROM dbo.People p;").Items.Any(i => i.InsertText == "[odd]]column]"), "escaped insertion");
Check(Complete("SELECT 1 FROM dbo.Pe|;").Items.Single().InsertText == "People", "schema objects");
Check(Complete("SELECT 1 FROM Pe|;").Items.Single().InsertText == "dbo.People", "qualified object insertion");
Check(SqlCompletion.Complete("SELECT * FROM No", 16, catalog, databases: new[] { "NOAH_BALCOR", "master" }).Items.Single().InsertText == "NOAH_BALCOR" &&
    SqlCompletion.Complete("SELECT * FROM dbo.No", 20, catalog, databases: new[] { "NOAH_BALCOR" }).Items.Count == 0 &&
    SqlCompletion.Complete("SELECT No", 9, catalog, databases: new[] { "NOAH_BALCOR" }).Items.All(i => i.InsertText != "NOAH_BALCOR"), "FROM offers databases before a schema");
Check(Complete("SELECT p.| FROM dbo.People p INNER JOIN dbo.Orders o ON p.Id=o.PersonId;").Items.Count == 3, "join scope");
Check(Complete("SELECT 1 FROM dbo.People p WHERE EXISTS (SELECT p.| FROM dbo.Orders p);").Items.All(i => i.Name != "Name"), "inner alias shadows outer");
Check(Complete("SELECT 1 FROM dbo.People p WHERE EXISTS (SELECT p.| FROM dbo.Orders o);").Items.Count == 3, "correlated scope");
Check(Complete("SELECT N'p.|';").Items.Count == 0, "literal suppression");
Check(Complete("-- p.|\nSELECT 1;").Items.Count == 0, "comment suppression");
Check(Complete("SELECT x.| FROM dbo.Missing x;").Items.Count == 0, "unknown metadata");
Check(Complete("SELECT p.| FROM dbo.People p; SELECT 1 FROM dbo.Orders p;").Items.Count == 3, "statement isolation");
Console.WriteLine($"PASS: {checks} total checks including completion. SSMS integration not tested.");
Check(Complete("WITH People AS (SELECT OrderId FROM dbo.Orders) SELECT p.| FROM People p;").Items.Single().Name == "OrderId", "CTE shadows database object");
Check(Complete("WITH c (Renamed) AS (SELECT OrderId FROM dbo.Orders) SELECT c.| FROM c;").Items.Single().Name == "Renamed", "explicit CTE columns");
Check(Complete("SELECT d.| FROM (SELECT Id AS PersonId FROM dbo.People) d;").Items.Single().Name == "PersonId", "derived aliases");
Check(Complete("SELECT 1 FROM dbo.People p WHERE EXISTS (SELECT p.| FROM (SELECT OrderId FROM dbo.Orders) p);").Items.Single().Name == "OrderId", "derived alias shadowing");
Check(Complete("SELECT 1 FROM dbo.People p, (SELECT p.| FROM dbo.Orders o) d;").Items.Count == 0, "noncorrelated derived isolation");
Console.WriteLine($"PASS: {checks} total checks including CTE and derived scopes. SSMS integration not tested.");
var imported = SchemaCatalog.FromDdl("CREATE TABLE dbo.T (Id int, [odd]]column] nvarchar(20));");
Check(imported.Single().Columns.SequenceEqual(new[] { "Id", "odd]column" }), "offline DDL catalog");
try { SchemaCatalog.FromDdl("CREATE TABLE dbo.T(id int); CREATE TABLE dbo.T(x int);"); throw new Exception("Expected duplicate rejection"); }
catch (FormatException) { checks++; }
Console.WriteLine($"PASS: {checks} total checks including schema import. SSMS integration not tested.");
try { SchemaCatalog.FromDdl("CREATE TABLE dbo.T(id int); ALTER TABLE dbo.T ADD x int;"); throw new Exception("Expected unsupported schema rejection"); }
catch (FormatException) { checks++; }
Console.WriteLine($"PASS: {checks} total checks. SSMS integration not tested.");

TextEdit Expand(string text)
{
    int position = text.IndexOf('|');
    return SqlCompletion.ExpandWildcard(text.Remove(position, 1), position, catalog);
}
void RejectExpand(string text)
{
    try { Expand(text); }
    catch (InvalidOperationException) { checks++; return; }
    throw new Exception("Expected wildcard rejection for " + text);
}
Check(Expand("SELECT *| FROM dbo.People;").Text == "Id,\r\n       Name,\r\n       [odd]]column]", "single-table wildcard vertical");
Check(Expand("SELECT o.*|, 1 FROM dbo.People p JOIN dbo.Orders o ON p.Id = o.PersonId;") is var q && q.Text == "o.OrderId,\r\n       o.PersonId" && q.Start == 7 && q.Length == 3, "qualified wildcard span");
Check(Expand("SELECT |* FROM People p, dbo.Orders o;").Text.StartsWith("p.Id,\r\n       p.Name,\r\n       p.[odd]]column],\r\n       o.OrderId"), "multi-table wildcard order");
Check(Expand("WITH c AS (SELECT OrderId FROM dbo.Orders) SELECT *| FROM c;").Text == "OrderId", "CTE wildcard");
Check(Expand("SELECT 1 FROM dbo.People p WHERE EXISTS (SELECT *| FROM dbo.Orders);").Text == "OrderId,\r\n                                                PersonId", "inner scope wildcard");
Check(SqlCompletion.ColumnList("SELECT a\n\tFROM x;\n\tSELECT ", 26, new[] { "a", "b" }) == "a,\n\t       b", "column list keeps LF and tabs");
RejectExpand("SELECT *| FROM dbo.Missing;");
RejectExpand("SELECT *| FROM dbo.People p CROSS APPLY OPENJSON(p.Name) j;");
RejectExpand("SELECT x.*| FROM dbo.People p;");
RejectExpand("SELECT 1| FROM dbo.People;");
Console.WriteLine($"PASS: {checks} total checks including wildcard expansion. SSMS integration not tested.");

Check(SqlRefactoring.AddSemicolons("SELECT 1\nSELECT 2 -- c\n") == "SELECT 1;\nSELECT 2; -- c\n", "statement semicolons");
Check(SqlRefactoring.AddSemicolons("SELECT 1;\nGO\nSELECT 2;") == "SELECT 1;\nGO\nSELECT 2;", "semicolons idempotent");
Check(SqlRefactoring.AddSemicolons("IF 1 = 1 SELECT 1 ELSE SELECT 2") == "IF 1 = 1 SELECT 1; ELSE SELECT 2;", "IF/ELSE semicolons");
Check(SqlRefactoring.AddSemicolons("BEGIN TRY\n SELECT 1\nEND TRY\nBEGIN CATCH\n THROW\nEND CATCH") ==
    "BEGIN TRY\n SELECT 1;\nEND TRY\nBEGIN CATCH\n THROW;\nEND CATCH;", "TRY/CATCH semicolons");
Check(SqlRefactoring.AddSemicolons("CREATE PROCEDURE p AS\nBEGIN\n SELECT N'a;b'\nEND") == "CREATE PROCEDURE p AS\nBEGIN\n SELECT N'a;b';\nEND;", "procedure semicolons");
Check(SqlRefactoring.AddSemicolons("SELECT 1\nWITH c AS (SELECT 1 AS x) SELECT x FROM c") == "SELECT 1;\nWITH c AS (SELECT 1 AS x) SELECT x FROM c;", "CTE terminator");
try { SqlRefactoring.AddSemicolons("SELECT FROM"); throw new Exception("Expected syntax rejection"); }
catch (FormatException) { checks++; }
Console.WriteLine($"PASS: {checks} total checks including semicolons. SSMS integration not tested.");

DefinitionTarget? Go(string text)
{
    int position = text.IndexOf('|');
    return SqlNavigation.FindDefinition(text.Remove(position, 1), position);
}
Check(Go("DECLARE @id int;\nSELECT @i|d;") is { Offset: 8, Length: 3 }, "variable definition");
Check(Go("DECLARE @id int;\nGO\nSELECT @i|d;") == null, "variable batch isolation");
Check(Go("CREATE PROCEDURE p @x int AS SELECT @|x;") is { Offset: 19 }, "parameter definition");
Check(Go("SELECT p|.Name FROM dbo.People p;") is { Offset: 30, Length: 1 }, "alias definition");
Check(Go("SELECT 1 FROM dbo.Pe|ople p;") is { Offset: -1, Schema: "dbo", Name: "People" }, "object definition");
Check(Go("EXEC dbo.Get|People;") is { Name: "GetPeople" }, "procedure definition");
Check(Go("WITH c AS (SELECT 1 AS x) SELECT x FROM |c;") is { Offset: 5, Length: 1 }, "CTE definition");
Check(Go("UPDATE p SET Name = N'' FROM dbo.People p WHERE p|.Id = 1;") is { Offset: 40 }, "UPDATE alias definition");
Check(Go("SELECT Pe|ople.Name FROM dbo.People;") is { Schema: "dbo", Name: "People" }, "unaliased table qualifier");
Check(Go("SELECT dbo.fn_Ta|x(1);") is { Offset: -1, Schema: "dbo", Name: "fn_Tax" }, "scalar function definition");
Check(Go("SELECT Other.dbo.fn_Ta|x(1);") is { Database: "Other", Schema: "dbo", Name: "fn_Tax" }, "cross-database scalar function");
Check(Go("SELECT FROM\nEXEC dbo.usp_Lo|ad;") is { Offset: -1, Schema: "dbo", Name: "usp_Load" }, "object name in a script with syntax errors");
Check(Go("SELECT FROM\nEXEC [Other]..[usp Lo|ad];") is { Database: "Other", Schema: null, Name: "usp Load" }, "db..name in a script with syntax errors");
Check(Go("SELECT FROM\nEXEC srv.db.dbo.p|;") == null && Go("SELECT FROM @x|") == null, "linked server and variables skipped on syntax errors");
Console.WriteLine($"PASS: {checks} total checks including navigation. SSMS integration not tested.");

Check(SnippetFiles.ShortcutBefore("SELECT 1;\nssf", 13) == "ssf", "shortcut word");
Check(SnippetFiles.ShortcutBefore("@ssf", 4) == null && SnippetFiles.ShortcutBefore("x.ssf", 5) == null && SnippetFiles.ShortcutBefore("ssf ", 4) == null, "shortcut boundaries");
var shortcutFolder = Path.Combine(Path.GetTempPath(), "UlysiSQL-shortcut-" + Guid.NewGuid().ToString("N"));
try
{
    Check(SnippetFiles.List("").Count == 0 && SnippetFiles.List(Path.Combine(shortcutFolder, "missing")).Count == 0, "empty or missing snippet folder lists nothing");
    SnippetFiles.Initialize(shortcutFolder);
    var listed = SnippetFiles.List(shortcutFolder);
    Check(listed.Any(p => p.Key == "ssf") && Snippets.Expand(SnippetFiles.Read(Path.Combine(shortcutFolder, "ssf.sql")), context, now).Text == "SELECT * FROM ", "ssf snippet");
    Check(!listed.Any(p => p.Key == "nope") && listed.All(p => p.Key.All(c => c < 128 && (char.IsLetterOrDigit(c) || c == '_'))), "unknown/unsafe shortcut");
}
finally { Directory.Delete(shortcutFolder, true); }
Console.WriteLine($"PASS: {checks} total checks including snippet shortcuts. SSMS integration not tested.");

string[] Rules(string sql) => SqlAnalysis.Analyze(sql).Diagnostics.Select(d => d.Rule).Distinct().Order().ToArray();
Check(Rules("DELETE FROM dbo.T;").SequenceEqual(new[] { "SW005" }) && Rules("DELETE FROM dbo.T WHERE Id = 1;").Length == 0, "DELETE without WHERE");
Check(Rules("UPDATE dbo.T SET x = 1;").SequenceEqual(new[] { "SW006" }) && Rules("UPDATE dbo.T SET x = 1 WHERE Id = 1;").Length == 0, "UPDATE without WHERE");
Check(new WorkbenchSettings().Severity("SW005") == RuleSeverity.Disabled && new WorkbenchSettings().Severity("SW006") == RuleSeverity.Disabled, "no-WHERE squiggles off by default");
Check(SqlAnalysis.UnfilteredChanges("DELETE FROM LC.StatusHdr").SequenceEqual(new[] { "DELETE LC.StatusHdr" })
    && SqlAnalysis.UnfilteredChanges("SELECT 1; UPDATE t SET x = 1 FROM dbo.T t JOIN dbo.U u ON u.Id = t.Id;").SequenceEqual(new[] { "UPDATE t" })
    && SqlAnalysis.UnfilteredChanges("DELETE FROM dbo.T WHERE Id = 1; UPDATE dbo.T SET x = 1 WHERE Id = 2;").Count == 0
    && SqlAnalysis.UnfilteredChanges("DELETE FROM").Count == 0, "unfiltered changes for execute prompt");
Check(Rules("SELECT a FROM dbo.T ORDER BY 1;").SequenceEqual(new[] { "SW007" }) && Rules("SELECT a FROM dbo.T ORDER BY a;").Length == 0, "ORDER BY constant");
Check(Rules("DECLARE @s varchar = 'x'; SELECT @s, CAST(1 AS nvarchar);").SequenceEqual(new[] { "SW008" }) &&
    Rules("DECLARE @s varchar(10) = 'x'; SELECT @s, CAST(1 AS nvarchar(max));").Length == 0, "string length");
Check(Rules("SELECT @@IDENTITY;").SequenceEqual(new[] { "SW009" }) && Rules("SELECT SCOPE_IDENTITY();").Length == 0, "@@IDENTITY");
Check(Rules("CREATE TABLE dbo.T (a ntext NULL);").SequenceEqual(new[] { "SW010" }), "deprecated types");
Check(Rules("SELECT a.x FROM dbo.A a, dbo.B b WHERE a.x = b.x;").SequenceEqual(new[] { "SW011" }) &&
    Rules("SELECT a.x FROM dbo.A a JOIN dbo.B b ON a.x = b.x;").Length == 0, "old-style join");
Check(Rules("CREATE PROCEDURE dbo.sp_x AS SET NOCOUNT ON; SELECT 1;").SequenceEqual(new[] { "SW012" }), "sp_ prefix");
Check(Rules("CREATE PROCEDURE dbo.p AS SELECT 1;").SequenceEqual(new[] { "SW015" }) &&
    Rules("CREATE PROCEDURE dbo.p AS BEGIN SET NOCOUNT ON; SELECT 1; END").Length == 0, "SET NOCOUNT ON");
Check(Rules("SELECT ISNUMERIC('1');").SequenceEqual(new[] { "SW013" }), "ISNUMERIC");
Check(Rules("SELECT a FROM dbo.A WHERE a NOT IN (SELECT b FROM dbo.B);").SequenceEqual(new[] { "SW014" }) &&
    Rules("SELECT a FROM dbo.A WHERE a NOT IN (1, 2);").Length == 0, "NOT IN subquery");
Check(Rules("DECLARE @unused int; DECLARE @t TABLE (x int NULL); SELECT x FROM @t;").SequenceEqual(new[] { "SW016" }) &&
    Rules("DECLARE @x int = 1;\nGO\nSELECT 1;").SequenceEqual(new[] { "SW016" }) &&
    Rules("CREATE PROCEDURE dbo.p @a int AS SET NOCOUNT ON; SELECT 1;").Length == 0 &&
    Rules("DECLARE @x int; EXEC dbo.p @x = 1;").SequenceEqual(new[] { "SW016" }) && Rules("DECLARE @x int = 1; EXEC dbo.p @a = @x;").Length == 0 &&
    !Rules("CREATE FUNCTION dbo.f() RETURNS @t TABLE (a int) AS BEGIN RETURN; END").Contains("SW016"), "unused variables");
Check(Rules("EXEC GetPeople;").SequenceEqual(new[] { "SW017" }) && Rules("EXEC dbo.GetPeople; EXEC sp_who; EXEC #tmp;").Length == 0, "unqualified EXEC");
var strict = new WorkbenchSettings { SW005 = RuleSeverity.Disabled };
Check(SqlAnalysis.Analyze("DELETE FROM dbo.T;", settings: strict).Diagnostics.Count == 0 && strict.Severity("PARSE1") == RuleSeverity.Error, "new rules configurable");
Console.WriteLine($"PASS: {checks} total checks including analysis batch 2. SSMS integration not tested.");

Check(Rules("DECLARE c CURSOR FOR SELECT 1; OPEN c; DEALLOCATE c;").SequenceEqual(new[] { "SW018" }) && Rules("DECLARE @c CURSOR; SET @c = CURSOR FOR SELECT 1; OPEN @c;").SequenceEqual(new[] { "SW018" }) &&
    Rules("DECLARE c CURSOR LOCAL FAST_FORWARD FOR SELECT 1; DECLARE g CURSOR GLOBAL FOR SELECT 1; DEALLOCATE c; DEALLOCATE g;").Length == 0, "cursor scope");
Check(Rules("CREATE PROCEDURE dbo.p AS BEGIN SET NOCOUNT ON; IF 1 = 1 RETURN; SELECT 1; END").SequenceEqual(new[] { "SW019" }) &&
    Rules("CREATE PROCEDURE dbo.p AS BEGIN SET NOCOUNT ON; RETURN 0; END").Length == 0 && Rules("RETURN;").Length == 0, "bare RETURN in procedure");
Check(Rules("CREATE TABLE #t (a int, b int NOT NULL);").SequenceEqual(new[] { "SW020" }) && Rules("DECLARE @t TABLE (a int); SELECT a FROM @t;").SequenceEqual(new[] { "SW020" }) &&
    Rules("CREATE TABLE dbo.T (id int IDENTITY, k int PRIMARY KEY, a int NULL, b AS a + 1); CREATE TABLE dbo.U (x int, y int NULL, CONSTRAINT PK_U PRIMARY KEY (x));").Length == 0, "column nullability");
var sw020 = SqlAnalysis.Analyze("CREATE TABLE dbo.T (a int NULL, b int);").Diagnostics.Single();
Check(sw020.Rule == "SW020" && sw020.Column == 33, "nullability location on column");
Check(Rules("DECLARE @p varbinary(16); READTEXT dbo.T.c @p 0 10;").SequenceEqual(new[] { "SW021" }) &&
    Rules("DECLARE @p varbinary(16); WRITETEXT dbo.T.c @p 'x';").SequenceEqual(new[] { "SW021" }) &&
    Rules("DECLARE @p varbinary(16); UPDATETEXT dbo.T.c @p 0 NULL 'x';").SequenceEqual(new[] { "SW021" }) &&
    Rules("UPDATE dbo.T SET c.WRITE(N'x', 0, 1) WHERE Id = 1;").Length == 0, "text pointer statements");
Check(Rules("ALTER TABLE dbo.T ADD c int NOT NULL;").SequenceEqual(new[] { "SW022" }) &&
    Rules("ALTER TABLE dbo.T ADD c int NOT NULL DEFAULT 0, d int NULL, e AS 1;").Length == 0, "ALTER ADD NOT NULL without DEFAULT");
Check(Rules("SET ROWCOUNT 10;").SequenceEqual(new[] { "SW023" }) && Rules("SET NOCOUNT ON;").Length == 0, "SET ROWCOUNT");
Check(Rules("SELECT a FROM dbo.T WITH (NOLOCK) JOIN dbo.U u WITH (READUNCOMMITTED) ON u.a = T.a;").SequenceEqual(new[] { "SW024" }) &&
    Rules("SELECT a FROM dbo.T WITH (READCOMMITTEDLOCK, INDEX(ix));").SequenceEqual(new[] { "SW034" }), "NOLOCK hint");
Check(Rules("CREATE PROCEDURE dbo.p AS BEGIN SET NOCOUNT ON; WAITFOR DELAY '00:00:01'; RETURN 0; END").SequenceEqual(new[] { "SW025" }) &&
    Rules("WAITFOR DELAY '00:00:01';").Length == 0, "WAITFOR DELAY in procedure");
Check(Rules("SELECT TOP (5) a FROM dbo.T;").SequenceEqual(new[] { "SW026" }) &&
    Rules("SELECT TOP (5) a FROM dbo.T ORDER BY a; IF EXISTS (SELECT TOP (1) 1 FROM dbo.T) SELECT 1;").Length == 0, "TOP without ORDER BY");
Check(Rules("DECLARE @s nvarchar(100) = N'SELECT 1'; EXEC (@s);").SequenceEqual(new[] { "SW027" }) &&
    Rules("DECLARE @s nvarchar(100) = N'SELECT 1'; EXEC sys.sp_executesql @s;").Length == 0, "EXECUTE(string)");
Console.WriteLine($"PASS: {checks} total checks including analysis batch 3. SSMS integration not tested.");
Check(Rules("IF (SELECT COUNT(*) FROM dbo.T) > 0 SELECT 1;").SequenceEqual(new[] { "SW028" }) &&
    Rules("IF 0 = (SELECT COUNT(*) FROM dbo.T) SELECT 1;").SequenceEqual(new[] { "SW028" }) &&
    Rules("IF (SELECT COUNT(*) FROM dbo.T) > 5 SELECT 1; IF EXISTS (SELECT 1 FROM dbo.T) SELECT 1;").Length == 0, "COUNT compared with 0");
Check(Rules("GOTO done; done: SELECT 1;").SequenceEqual(new[] { "SW029" }), "GOTO");
Check(Rules("SET ANSI_NULLS OFF;").SequenceEqual(new[] { "SW030" }) && Rules("SET CONCAT_NULL_YIELDS_NULL OFF;").SequenceEqual(new[] { "SW030" }) &&
    Rules("SET ANSI_NULLS ON; SET ANSI_PADDING ON; SET QUOTED_IDENTIFIER ON;").Length == 0, "ANSI settings OFF");
Check(Rules("SELECT @@ERROR;").SequenceEqual(new[] { "SW031" }) && Rules("SELECT @@ROWCOUNT;").Length == 0, "@@ERROR");
Check(Rules("SELECT a FROM dbo.T WHERE YEAR(d) = 2020;").SequenceEqual(new[] { "SW032" }) &&
    Rules("SELECT a FROM dbo.T t JOIN dbo.U u ON UPPER(t.a) = N'X';").SequenceEqual(new[] { "SW032" }) &&
    Rules("SELECT a FROM dbo.T t JOIN dbo.U u ON UPPER(t.a) = u.a;").Length == 0 &&
    Rules("SELECT a FROM dbo.T WHERE d >= DATEFROMPARTS(2020, 1, 1) AND a = @x;").Length == 0 &&
    Rules("SELECT UPPER(a) FROM dbo.T;").Length == 0, "non-sargable function");
Check(Rules("SELECT a FROM dbo.T WHERE a LIKE '%x';").SequenceEqual(new[] { "SW033" }) &&
    Rules("SELECT a FROM dbo.T WHERE a LIKE 'x%';").Length == 0, "leading wildcard LIKE");
Check(Rules("SET FMTONLY ON;").SequenceEqual(new[] { "SW035" }) && Rules("SET FMTONLY OFF;").Length == 0, "SET FMTONLY");
Check(Rules("CREATE TABLE #t (a int NOT NULL, CONSTRAINT PK_t PRIMARY KEY (a));").SequenceEqual(new[] { "SW036" }) &&
    Rules("CREATE TABLE #t (a int NOT NULL PRIMARY KEY); CREATE TABLE dbo.T (a int NOT NULL, CONSTRAINT PK_T PRIMARY KEY (a));").Length == 0, "named temp constraint");
Check(Rules("DECLARE @f float, @r real; SELECT @f, @r;").SequenceEqual(new[] { "SW037" }) && Rules("DECLARE @m money; SELECT @m;").SequenceEqual(new[] { "SW038" }) &&
    Rules("CREATE TABLE dbo.T (v timestamp NULL);").SequenceEqual(new[] { "SW039" }) && Rules("CREATE TABLE dbo.T (v rowversion NULL);").Length == 0 &&
    Rules("DECLARE @d decimal; SELECT @d;").SequenceEqual(new[] { "SW045" }) && Rules("DECLARE @d decimal(9, 2), @n numeric(5); SELECT @d, @n;").Length == 0, "data type rules");
Check(Rules("SELECT 'x' = 1;").SequenceEqual(new[] { "SW040", "SW049" }) && Rules("SELECT 1 AS 'x';").SequenceEqual(new[] { "SW040" }) &&
    Rules("SELECT 1 AS x, 2 AS [y], 3 z;").Length == 0, "string alias");
Check(Rules("CREATE PROCEDURE dbo.p;2 AS SET NOCOUNT ON;").SequenceEqual(new[] { "SW041" }) && Rules("CREATE PROCEDURE dbo.p AS SET NOCOUNT ON;").Length == 0, "numbered procedure");
Check(Rules("SELECT a FROM dbo.T WHERE a !< 1;").SequenceEqual(new[] { "SW042" }) && Rules("SELECT a FROM dbo.T WHERE a !> 1 OR a <> 2;").SequenceEqual(new[] { "SW042" }), "!< !>");
Check(Rules("SELECT TOP (100) PERCENT a FROM dbo.T ORDER BY a;").SequenceEqual(new[] { "SW043" }) &&
    Rules("SELECT TOP (50) PERCENT a FROM dbo.T ORDER BY a;").Length == 0, "TOP 100 PERCENT");
Check(Rules("DECLARE @t sysname = N'x'; EXEC('SELECT 1 FROM ' + @t);").Contains("SW047") && !Rules("EXEC('SELECT 1');").Contains("SW047"), "concatenated EXEC");
Check(SqlRefactoring.UnwrapDynamicSql(SqlRefactoring.WrapAsDynamicSql("SELECT 'a';")).Trim() == "SELECT 'a';" && SqlRefactoring.WrapAsDynamicSql("SELECT 'a';").Contains("N'SELECT ''a'';'"), "wrap and unwrap dynamic SQL");
Check(new[] { "N'abc", "'", "EXEC('x" }.All(s => { try { SqlRefactoring.UnwrapDynamicSql(s); return false; } catch (InvalidOperationException) { return true; } }), "unwrap unterminated literal warns");
Throws<InvalidOperationException>(() => SqlRefactoring.UnwrapDynamicSql("DECLARE @sql nvarchar(max) = N'SELECT ' + QUOTENAME(@c) + N' FROM t';"), "unwrap refuses concatenated dynamic SQL");
Check(SqlAnalysis.DatabaseSwitchChanges("USE Other; DELETE FROM dbo.T WHERE Id = 1;").SequenceEqual(new[] { "USE Other" }) && SqlAnalysis.DatabaseSwitchChanges("USE Other; SELECT 1;").Count == 0 && SqlAnalysis.DatabaseSwitchChanges("DELETE FROM dbo.T WHERE Id = 1;").Count == 0, "USE with data changes");
Check(Rules("IF EXISTS (SELECT COUNT(*) FROM dbo.T WHERE a = 1) SELECT 1;").SequenceEqual(new[] { "SW044" }) &&
    Rules("IF EXISTS (SELECT COUNT(*) FROM dbo.T GROUP BY a) SELECT 1; IF EXISTS (SELECT MAX(a) FROM dbo.T HAVING MAX(a) > 1) SELECT 1;").Length == 0, "EXISTS aggregate");
Check(Rules("EXEC master.dbo.xp_cmdshell 'dir';").SequenceEqual(new[] { "SW046" }) && Rules("EXEC xp_cmdshell 'dir';").SequenceEqual(new[] { "SW017", "SW046" }) &&
    Rules("EXEC dbo.xp_other;").Length == 0, "xp_cmdshell");
Check(Rules("-- querywright-disable SW029\nGOTO done; done: SELECT 1;").Length == 0, "new rule suppression");
Check(Rules("-- ulysisql-disable SW029\nGOTO done; done: SELECT 1;").Length == 0 && Rules("/* ULYSISQL-disable-next-line */\nSELECT * FROM dbo.T;").Length == 0
    && Rules("-- ulysisql-disabled\nSELECT * FROM dbo.T;").Length == 1, "suppression under the UlysiSQL name");
Console.WriteLine($"PASS: {checks} total checks including analysis batch 4. SSMS integration not tested.");
var styled = SqlFormatting.Format("select a, b from dbo.T t where a = 1 and b = 2 group by a, b order by a", new FormattingStyle
{ NewLineBeforeWhere = false, NewLineBeforeJoin = false, NewLineBeforeGrouping = false, MultilinePredicates = false, MultilineColumns = false, NewLineBeforeFrom = false, IncludeSemicolons = true });
Check(!styled.Trim().Contains('\n') && styled.TrimEnd().EndsWith(";"), "style options single line + semicolon");
var defaultStyled = SqlFormatting.Format("select a from dbo.T where a = 1 and b = 2");
Check(defaultStyled.Contains("\nWHERE") && Regex.IsMatch(defaultStyled, @"\n\s+AND b = 2"), "default style unchanged");
var styleDir = Path.Combine(Path.GetTempPath(), "qw-style-" + Guid.NewGuid().ToString("N"));
try
{
    var saved = new WorkbenchSettings { SW005 = RuleSeverity.Error };
    saved.Formatting.LeadingCommas = true; saved.Formatting.IndentSize = 2;
    string settingsPath = Path.Combine(styleDir, "sub", "settings.xml");
    saved.Save(settingsPath); saved.Formatting.IndentSize = 3; saved.Save(settingsPath);
    var loaded = WorkbenchSettings.Load(settingsPath);
    Check(loaded.Formatting.LeadingCommas && loaded.Formatting.IndentSize == 3 && loaded.SW005 == RuleSeverity.Error && loaded.Formatting.NewLineBeforeJoin
        && Directory.GetFiles(Path.GetDirectoryName(settingsPath)!).Length == 1, "settings save round trip");
    File.WriteAllText(settingsPath, "<WorkbenchSettings><Formatting><IndentSize>4</IndentSize></Formatting></WorkbenchSettings>");
    Check(WorkbenchSettings.Load(settingsPath).Formatting.AlignClauseBodies, "old settings file keeps new defaults");

    string F(string name, byte[] bytes) { string p = Path.Combine(styleDir, name); File.WriteAllBytes(p, bytes); return p; }
    var utf8 = new UTF8Encoding(false);
    var changed = F("a.sql", utf8.GetBytes("select a from dbo.T\r\nwhere a = 1\r\n"));
    var bom16 = F("b.sql", Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("select N'é' from dbo.T")).ToArray());
    var clean = F("c.sql", utf8.GetBytes(SqlFormatting.Format("SELECT a FROM dbo.T;").Replace("\r\n", "\n")));
    var broken = F("d.sql", utf8.GetBytes("select from where"));
    var ansi = F("e.sql", new byte[] { (byte)'s', (byte)'e', (byte)'l', (byte)'e', (byte)'c', (byte)'t', (byte)' ', (byte)'\'', 0xE9, (byte)'\'' });
    var empty = F("f.sql", new byte[0]);
    var all = new[] { changed, bom16, clean, broken, ansi, empty };
    var before = all.Select(File.ReadAllBytes).ToArray();
    var preview = SqlFormatting.FormatFiles(all, null, write: false);
    Check(preview.Select(r => r.Status).SequenceEqual(new[] { SqlFormatting.FileStatus.Changed, SqlFormatting.FileStatus.Changed, SqlFormatting.FileStatus.Unchanged,
        SqlFormatting.FileStatus.Failed, SqlFormatting.FileStatus.Failed, SqlFormatting.FileStatus.Unchanged })
        && all.Select(File.ReadAllBytes).Zip(before, (a, b) => a.SequenceEqual(b)).All(x => x), "bulk preview writes nothing");
    var written = SqlFormatting.FormatFiles(all, null, write: true);
    var changedText = File.ReadAllText(changed);
    var bomBytes = File.ReadAllBytes(bom16);
    Check(written.Select(r => r.Status).SequenceEqual(preview.Select(r => r.Status)) && changedText.Contains("SELECT a") && changedText.Contains("\r\n") && !Regex.IsMatch(changedText, "[^\r]\n")
        && bomBytes[0] == 0xFF && bomBytes[1] == 0xFE && Encoding.Unicode.GetString(bomBytes, 2, bomBytes.Length - 2).Contains("N'é'")
        && File.ReadAllBytes(broken).SequenceEqual(before[3]) && File.ReadAllBytes(ansi).SequenceEqual(before[4]) && File.ReadAllBytes(clean).SequenceEqual(before[2])
        && !Directory.GetFiles(styleDir, "*.qwtmp").Any(), "bulk write keeps encoding, newlines, and skips failures");
    Check(SqlFormatting.FormatFiles(all, null, write: false).Take(2).All(r => r.Status == SqlFormatting.FileStatus.Unchanged), "bulk format idempotent");
}
finally { if (Directory.Exists(styleDir)) Directory.Delete(styleDir, true); }
Console.WriteLine($"PASS: {checks} total checks including style options and bulk formatting. SSMS integration not tested.");

Check(Rules("-- querywright-disable SW005\nDELETE FROM dbo.T;\nGO\nDELETE FROM dbo.T;").Length == 0 && Rules("-- querywright-disable SW005, SW006\nDELETE FROM dbo.T;\nUPDATE dbo.T SET x = 1;\n-- querywright-enable SW005\nDELETE FROM dbo.T;\nUPDATE dbo.T SET x = 1;")
    .SequenceEqual(new[] { "SW005" }), "disable until enable");
Check(Rules("/* querywright-disable */ SELECT * FROM dbo.T; DELETE FROM dbo.T;").Length == 0, "block comment disables all to end");
Check(Rules("-- querywright-disable\nSELECT * FROM dbo.T;\n-- querywright-enable SW001\nSELECT * FROM dbo.T; DELETE FROM dbo.T;").SequenceEqual(new[] { "SW001" }), "re-enable one after disable all");
Check(Rules("-- querywright-disable-next-line SW005\nDELETE FROM dbo.T;\nDELETE FROM dbo.T;").Length == 1 &&
    SqlAnalysis.Analyze("-- querywright-disable-next-line SW005\nDELETE FROM dbo.T;\nDELETE FROM dbo.T;").Diagnostics.Single().Line == 3, "disable next line only");
Check(Rules("/* querywright-disable-next-line\n   multi-line comment */\nSELECT * FROM dbo.T;").Length == 0 &&
    Rules("-- querywright-disable-next-line SW006\nDELETE FROM dbo.T;").SequenceEqual(new[] { "SW005" }), "next-line after block comment; other IDs untouched");
Check(Rules("SELECT N'-- querywright-disable'; SELECT * FROM dbo.T; -- querywright-disabled\nSELECT * FROM dbo.U;").SequenceEqual(new[] { "SW001" }) &&
    SqlAnalysis.Analyze("SELECT N'-- querywright-disable'; SELECT * FROM dbo.T; -- querywright-disabled\nSELECT * FROM dbo.U;").Diagnostics.Count == 2, "strings and lookalike comments are not directives");
Check(!SqlAnalysis.Analyze("-- querywright-disable\nSELECT FROM").Parsed && SqlAnalysis.Analyze("-- querywright-disable\nSELECT FROM").Diagnostics.Count > 0, "parse errors never suppressed");
Check(Rules("-- QueryWright-Disable-Next-Line sw001 -- reason SW005\nSELECT * FROM dbo.T; DELETE FROM dbo.T;").SequenceEqual(new[] { "SW005" }), "case-insensitive directive and trailing reason");
Console.WriteLine($"PASS: {checks} total checks including inline suppression. SSMS integration not tested.");

var big = new System.Text.StringBuilder();
for (int i = 0; i < 1000; i++)
    big.Append("SELECT a, b FROM dbo.T").Append(i).Append(" t WITH (NOLOCK)\n  WHERE t.a = @x\n    AND t.b IN (SELECT b FROM dbo.U);\n")
       .Append("-- note ").Append(i).Append("\nUPDATE dbo.T SET a = CASE WHEN b = 1 THEN 2 END WHERE a = 1;\n");
var bigSql = "DECLARE @x int = 1;\n" + big;
Check(bigSql.Count(c => c == '\n') >= 5000, "5,000-line fixture");
SqlAnalysis.Analyze("SELECT 1;"); // warm up JIT
var watch = System.Diagnostics.Stopwatch.StartNew();
var bigResult = SqlAnalysis.Analyze(bigSql);
watch.Stop();
Check(bigResult.Parsed && bigResult.Diagnostics.Count(d => d.Rule == "SW024") == 1000, "5,000-line analysis results");
Check(watch.ElapsedMilliseconds < 2000, "5,000-line analysis under 2 s (" + watch.ElapsedMilliseconds + " ms)");
using var midway = new CancellationTokenSource();
midway.CancelAfter(1);
try { for (int i = 0; i < 50; i++) SqlAnalysis.Analyze(bigSql, midway.Token); throw new Exception("Expected cancellation of large analysis"); }
catch (OperationCanceledException) { checks++; }
Console.WriteLine($"PASS: {checks} total checks; 5,000-line analysis took {watch.ElapsedMilliseconds} ms. SSMS integration not tested.");
void Throws<T>(Action action, string name) where T : Exception
{
    try { action(); }
    catch (T) { checks++; return; }
    throw new Exception("Expected " + typeof(T).Name + ": " + name);
}
Check(SqlRefactoring.ApplyCasing("select count(*), cast(a as nvarchar(max)), dateadd(day, 1, getdate()) from dbo.t where x is not null") ==
    "SELECT COUNT(*), CAST(a AS NVARCHAR(MAX)), DATEADD(DAY, 1, GETDATE()) FROM dbo.t WHERE x IS NOT NULL", "keyword, type and function casing");
Check(SqlRefactoring.ApplyCasing("select N'select from', [select], \"from\", @select -- select\n/* from */ from t") ==
    "SELECT N'select from', [select], \"from\", @select -- select\n/* from */ FROM t", "casing skips strings comments quoted and variables");
Check(SqlRefactoring.ApplyCasing("SELECT Count, Name, MyFunc FROM dbo.Orders o JOIN Sales.Count c ON o.Id = c.Id") ==
    "SELECT Count, Name, MyFunc FROM dbo.Orders o JOIN Sales.Count c ON o.Id = c.Id", "casing leaves identifiers named like functions");
Check(SqlRefactoring.ApplyCasing("begin try set nocount on; throw; end try begin catch end catch\ngo\ndeclare @x Int; exec p @a = abc;") ==
    "BEGIN TRY SET NOCOUNT ON; THROW; END TRY BEGIN CATCH END CATCH\nGO\nDECLARE @x INT; EXEC p @a = abc;", "casing non-reserved keywords, GO and identifier literals");
Check(SqlRefactoring.ApplyCasing("SELECT COUNT(*) FROM T WHERE X IS NULL", false) == "select count(*) from T where X is null", "lowercase keywords");
Check(SqlRefactoring.ApplyCasing("SELECT dbo.Max(1), s.Len FROM dbo.fn() s") == "SELECT dbo.Max(1), s.Len FROM dbo.fn() s", "schema-qualified function kept");
Throws<FormatException>(() => SqlRefactoring.ApplyCasing("select from"), "casing syntax error");

Check(SqlRefactoring.AddBrackets("SELECT o.Id, Name AS n, COUNT(*) c FROM dbo.Orders o JOIN #t t ON t.Id = o.Id WHERE o.Note = N'x.y'") ==
    "SELECT [o].[Id], [Name] AS [n], COUNT(*) [c] FROM [dbo].[Orders] [o] JOIN #t [t] ON [t].[Id] = [o].[Id] WHERE [o].[Note] = N'x.y'", "add brackets");
Check(SqlRefactoring.AddBrackets("WITH c (x) AS (SELECT 1 AS y) SELECT \"q\", [z] FROM c; DECLARE @v int; SELECT @v; EXEC dbo.P;") ==
    "WITH [c] ([x]) AS (SELECT 1 AS [y]) SELECT \"q\", [z] FROM [c]; DECLARE @v int; SELECT @v; EXEC [dbo].[P];", "add brackets CTE, quoted, variables, EXEC");
Check(SqlRefactoring.AddBrackets("SELECT DATEADD(day, 1, x), CAST(y AS int) FROM t -- t\n") ==
    "SELECT DATEADD(day, 1, [x]), CAST([y] AS int) FROM [t] -- t\n", "add brackets skips dateparts and types");
Check(SqlRefactoring.RemoveBrackets("SELECT [o].[Id], [order], [my col], [1x], \"Name\", [window] FROM [dbo].[Orders] [o] WHERE [o].[a$b] = N'[x]'") ==
    "SELECT o.Id, [order], [my col], [1x], \"Name\", [window] FROM dbo.Orders o WHERE o.a$b = N'[x]'", "remove brackets keeps reserved and irregular names");
Check(SqlRefactoring.RemoveBrackets("SELECT [@x], [#t], [go], [key], [Name] FROM [#t]") == "SELECT [@x], #t, [go], [key], Name FROM #t", "remove brackets special names");
var bracketed = "CREATE TABLE [dbo].[T] ([Id] [int], [select] int); SELECT [x]]y] FROM [T];";
Check(SqlRefactoring.RemoveBrackets(SqlRefactoring.AddBrackets(bracketed)) == "CREATE TABLE dbo.T (Id int, [select] int); SELECT [x]]y] FROM T;", "bracket round trip");
Throws<FormatException>(() => SqlRefactoring.AddBrackets("SELECT FROM"), "add brackets syntax error");
Throws<FormatException>(() => SqlRefactoring.RemoveBrackets("SELECT [a FROM t"), "remove brackets syntax error");

Check(SqlRefactoring.QualifyObjectNames("SELECT * FROM Orders o JOIN Sales.People p ON 1 = 1; UPDATE Orders SET x = 1; INSERT INTO Log (a) VALUES (1); DELETE FROM Log; EXEC GetPeople;") ==
    "SELECT * FROM dbo.Orders o JOIN Sales.People p ON 1 = 1; UPDATE dbo.Orders SET x = 1; INSERT INTO dbo.Log (a) VALUES (1); DELETE FROM dbo.Log; EXEC dbo.GetPeople;", "qualify DML and EXEC");
Check(SqlRefactoring.QualifyObjectNames("WITH c AS (SELECT id FROM T) SELECT * FROM c JOIN #tmp ON 1 = 1 JOIN @tv v ON 1 = 1 CROSS APPLY STRING_SPLIT(N'a', N',') s; EXEC sp_who; SELECT * FROM c;") ==
    "WITH c AS (SELECT id FROM dbo.T) SELECT * FROM c JOIN #tmp ON 1 = 1 JOIN @tv v ON 1 = 1 CROSS APPLY STRING_SPLIT(N'a', N',') s; EXEC sp_who; SELECT * FROM dbo.c;", "qualify excludes CTE temp variable built-in and sp_");
Check(SqlRefactoring.QualifyObjectNames("UPDATE p SET Name = N'' FROM People p; DELETE o FROM Orders AS o; MERGE Target t USING Source s ON t.Id = s.Id WHEN MATCHED THEN DELETE;", "Sales") ==
    "UPDATE p SET Name = N'' FROM Sales.People p; DELETE o FROM Sales.Orders AS o; MERGE Sales.Target t USING Sales.Source s ON t.Id = s.Id WHEN MATCHED THEN DELETE;", "qualify alias targets and MERGE");
Check(SqlRefactoring.QualifyObjectNames("SELECT * FROM db..T, [T2], x.dbo.T3 -- FROM T4\n", "my schema") == "SELECT * FROM db..T, [my schema].[T2], x.dbo.T3 -- FROM T4\n", "qualify bracketed schema, skip multipart");
Check(SqlRefactoring.QualifyObjectNames("SELECT * FROM dbo.fnRows(1); SELECT * FROM fnRows(1);") == "SELECT * FROM dbo.fnRows(1); SELECT * FROM dbo.fnRows(1);", "qualify table functions");
Throws<FormatException>(() => SqlRefactoring.QualifyObjectNames("SELECT * FROM"), "qualify syntax error");

RenameResult Alias(string text, string newName)
{
    int position = text.IndexOf('|');
    return SqlRefactoring.RenameAlias(text.Remove(position, 1), position, newName);
}
var aliasRename = Alias("SELECT p.Name, p.* FROM dbo.People p| WHERE p.Id = 1 ORDER BY p.Name; SELECT p.Id FROM dbo.Other p;", "person");
Check(aliasRename.Text == "SELECT person.Name, person.* FROM dbo.People person WHERE person.Id = 1 ORDER BY person.Name; SELECT p.Id FROM dbo.Other p;" &&
    aliasRename.OldName == "p" && aliasRename.Changes == 5, "alias rename from definition, statement scope");
Check(Alias("SELECT |a.x FROM dbo.A a WHERE EXISTS (SELECT 1 FROM dbo.B a WHERE a.y = 1) AND EXISTS (SELECT 1 FROM dbo.C c WHERE c.z = a.x);", "ao").Text ==
    "SELECT ao.x FROM dbo.A ao WHERE EXISTS (SELECT 1 FROM dbo.B a WHERE a.y = 1) AND EXISTS (SELECT 1 FROM dbo.C c WHERE c.z = ao.x);", "alias rename nested shadowing and correlation");
Check(Alias("SELECT a.x FROM dbo.A a WHERE EXISTS (SELECT 1 FROM dbo.B a WHERE |a.y = 1);", "[inner b]").Text ==
    "SELECT a.x FROM dbo.A a WHERE EXISTS (SELECT 1 FROM dbo.B [inner b] WHERE [inner b].y = 1);", "alias rename inner scope from qualifier");
Check(Alias("UPDATE p SET p.Name = N'p.x' FROM dbo.People p| WHERE p.Id = 1;", "pe").Text == "UPDATE pe SET pe.Name = N'p.x' FROM dbo.People pe WHERE pe.Id = 1;", "alias rename UPDATE FROM");
Check(Alias("DELETE |o FROM dbo.Orders o JOIN dbo.People p ON p.Id = o.PersonId; -- o.Id\n", "ord").Text ==
    "DELETE ord FROM dbo.Orders ord JOIN dbo.People p ON p.Id = ord.PersonId; -- o.Id\n", "alias rename DELETE target");
Check(Alias("SELECT d.n FROM (SELECT x.Name AS n FROM dbo.X x) |d;", "derived").Text == "SELECT derived.n FROM (SELECT x.Name AS n FROM dbo.X x) derived;", "derived table alias");
Throws<InvalidOperationException>(() => Alias("SELECT |a.x FROM dbo.A a JOIN dbo.B b ON a.x = b.x;", "b"), "alias conflict in scope");
Throws<InvalidOperationException>(() => Alias("SELECT |a.x FROM dbo.A a JOIN dbo.B ON 1 = 1;", "B"), "alias conflict with unaliased table");
Throws<InvalidOperationException>(() => Alias("SELECT 1 FROM dbo.A |a WHERE EXISTS (SELECT 1 FROM dbo.B b WHERE b.x = a.x);", "b"), "alias capture by inner scope");
Throws<InvalidOperationException>(() => Alias("SELECT 1 FROM dbo.A a WHERE EXISTS (SELECT 1 FROM dbo.B |b WHERE b.x = a.x);", "a"), "alias shadows outer reference");
Throws<InvalidOperationException>(() => Alias("SELECT |People.Name FROM dbo.People;", "p"), "table qualifier is not an alias");
Throws<InvalidOperationException>(() => Alias("SELECT |1 FROM dbo.People p;", "q"), "caret not on alias");
Throws<ArgumentException>(() => Alias("SELECT 1 FROM dbo.People |p;", "q; DROP TABLE t; --"), "invalid alias name");
Throws<ArgumentException>(() => Alias("SELECT 1 FROM dbo.People |p;", "order"), "reserved alias name");
Throws<FormatException>(() => Alias("SELECT 1 FROM dbo.People |p WHERE", "q"), "alias rename syntax error");

StatementSpan? At(string text, out string statement)
{
    int position = text.IndexOf('|');
    string sql = text.Remove(position, 1);
    var span = SqlNavigation.StatementAt(sql, position);
    statement = span == null ? "" : sql.Substring(span.Start, span.Length);
    return span;
}
Check(At("SELECT 1;\nSELECT |2 FROM t;\nSELECT 3", out var current) != null && current == "SELECT 2 FROM t;", "statement at caret with semicolon");
Check(At("SELECT 1;\n\n|\nSELECT 2;", out current) != null && current == "SELECT 1;", "caret between statements");
Check(At("SELECT 1;|SELECT 2;", out current) != null && current == "SELECT 2;", "caret at statement start");
Check(At("IF 1 = 1\nBEGIN\n  SELECT |1;\n  SELECT 2;\nEND\nSELECT 3;", out current) != null && current == "IF 1 = 1\nBEGIN\n  SELECT 1;\n  SELECT 2;\nEND", "compound statement");
Check(At("CREATE PROCEDURE p AS\nSELECT 1;\nSELECT |2;\nGO\nSELECT 3;", out current) != null && current == "CREATE PROCEDURE p AS\nSELECT 1;\nSELECT 2;", "procedure is one statement");
Check(At("SELECT 1;\nGO\n|SELECT 2;", out current) != null && current == "SELECT 2;", "GO-aware region");
Check(At("SELECT 1;\nGO\n-- |only a comment\nGO\nSELECT 2;", out _) == null, "empty batch region");
Check(At("SELECT 1;\nGO\n  SELECT * FRM |t WHERE\n\nGO\nSELECT 2;", out current) != null && current == "SELECT * FRM t WHERE", "syntax error falls back to trimmed batch");
Check(At("SELECT * FROM;\nGO\nSELECT |2;", out current) != null && current == "SELECT 2;", "syntax error in another batch ignored");
Check(At("SELECT N'\nGO\n' AS |x;", out current) != null && current == "SELECT N'\nGO\n' AS x;", "GO inside string is not a separator");
Check(At("|", out _) == null, "empty script");

var outline = SqlRefactoring.Summarize("CREATE PROCEDURE dbo.GetPeople AS SELECT 1;\nGO\nUPDATE p SET Name = N'' FROM dbo.People p;\nSELECT * FROM dbo.Orders o JOIN dbo.People p ON 1 = 1;\nDECLARE @x int;\nIF 1 = 1 BEGIN SELECT 1; END\nINSERT @t (a) VALUES (1);\nEXEC dbo.P;\nSELECT 1;");
Check(outline.Select(o => o.Kind).SequenceEqual(new[] { "CREATE PROCEDURE", "UPDATE", "SELECT", "DECLARE", "IF", "INSERT", "EXEC", "SELECT" }), "outline kinds");
Check(outline.Select(o => o.Target).SequenceEqual(new[] { "dbo.GetPeople", "dbo.People", "dbo.Orders", "", "", "@t", "dbo.P", "" }), "outline targets");
Check(outline[1] is { Line: 3, Offset: 47, Length: 42 } && outline[0].Length == 43, "outline positions");
Check(SqlRefactoring.Summarize("").Count == 0, "empty outline");
Throws<FormatException>(() => SqlRefactoring.Summarize("SELECT FROM"), "outline syntax error");

Check(SqlRefactoring.UnusedDeclarations("DECLARE @used int = 1, @unused int; DECLARE @t TABLE (x int); SELECT @used; -- @unused\nEXEC dbo.P @unused = 1;").SequenceEqual(new[] { "@unused", "@t" }), "unused locals");
Check(SqlRefactoring.UnusedDeclarations("CREATE PROCEDURE dbo.P @a int, @b int, @c dbo.Tvp READONLY AS SELECT @a FROM @c;\nGO\nDECLARE @b int;").SequenceEqual(new[] { "@b", "@b" }), "unused parameters and batch isolation");
Check(SqlRefactoring.UnusedDeclarations("CREATE FUNCTION dbo.F (@a int, @b int) RETURNS @r TABLE (x int) AS BEGIN INSERT @r VALUES (@a); RETURN; END").SequenceEqual(new[] { "@b" }), "unused function parameter");
Check(SqlRefactoring.UnusedDeclarations("CREATE FUNCTION dbo.G (@a int) RETURNS TABLE AS RETURN SELECT @a AS x").Count == 0, "inline function parameter used");
Throws<FormatException>(() => SqlRefactoring.UnusedDeclarations("DECLARE @x"), "unused syntax error");
Console.WriteLine($"PASS: {checks} total checks including casing, brackets, qualification, alias rename, statement lookup, outline and unused declarations. SSMS integration not tested.");
// SQL Prompt-style completion: no schema, incomplete SQL, foreign keys and column types.
CompletionResult CompleteAt(string text, IReadOnlyList<SchemaTable>? schema)
{
    int position = text.IndexOf('|');
    return SqlCompletion.Complete(text.Remove(position, 1), position, schema);
}
string[] Names(CompletionResult r) => r.Items.Select(i => i.Name).ToArray();
var sel = CompleteAt("SEL|", null);
Check(sel.Items[0].Name == "SELECT" && sel.Items[0].InsertText == "SELECT" && sel.Items[0].Description == "keyword" && sel.Start == 0 && sel.Length == 3, "no-schema keyword");
Check(CompleteAt("sel|", Array.Empty<SchemaTable>()).Items[0].Name == "SELECT", "empty schema, case-insensitive prefix");
var count = CompleteAt("SELECT CO|", null).Items.Single(i => i.Name == "COUNT");
Check(count.InsertText == "COUNT(" && count.Description == "function", "function inserts parenthesis");
Check(CompleteAt("SELECT IS|", null).Items[0].Name == "IS" && CompleteAt("SELECT IS|", null).Items[1].Name == "ISNULL", "prefix-exact before context order");
Check(CompleteAt("|", null).Items.Count is > 50 and <= 200, "empty input offers keywords and functions within the cap");
Check(Names(CompleteAt("DECLARE @id int = 1, @name nvarchar(10);\nDECLARE @t TABLE (x int);\nSELECT @|", null)).SequenceEqual(new[] { "@id", "@name", "@t" }), "declared variables");
Check(CompleteAt("DECLARE @old int;\nGO\nSELECT @|; DECLARE @later int;", null).Items.Count == 0, "variable batch and order isolation");
Check(Names(CompleteAt("CREATE PROCEDURE dbo.P @pid int, @flag bit AS SELECT @p|", null)).SequenceEqual(new[] { "@pid" }), "procedure parameters");
Check(Names(CompleteAt("DECLARE @t TABLE (x int); DECLARE @n int; SELECT * FROM @|", null)).SequenceEqual(new[] { "@t" }), "table variables after FROM");
Check(Names(CompleteAt("DECLARE @a int = (SELECT MAX(x) FROM dbo.T WHERE y IN (1, 2)), @b int; SELECT @| FROM (", null)).SequenceEqual(new[] { "@a", "@b" }), "variables in unparsable SQL");
Check(CompleteAt("SELECT N'abc|", null).Items.Count == 0 && CompleteAt("/* SEL|", null).Items.Count == 0, "unterminated literal and comment");
Console.WriteLine($"PASS: {checks} total checks including schema-free completion. SSMS integration not tested.");

var fkCatalog = SchemaCatalog.FromDdl(@"
CREATE TABLE dbo.People (Id int PRIMARY KEY, Name nvarchar(100) NOT NULL, Born date);
CREATE TABLE dbo.Orders (OrderId int PRIMARY KEY, PersonId int NULL REFERENCES dbo.People, Total decimal(18, 2), Total2 AS Total * 2);
CREATE TABLE sales.Lines (OrderId int, LineNumber int, [LineNo] int, Qty int,
    CONSTRAINT PK_Lines PRIMARY KEY (OrderId, LineNumber),
    CONSTRAINT FK_Lines_Orders FOREIGN KEY (OrderId) REFERENCES dbo.Orders (OrderId));
CREATE TABLE sales.Notes (OrderId int, LineNumber int, Body nvarchar(max));
GO
ALTER TABLE sales.Notes WITH CHECK ADD CONSTRAINT FK_Notes_Lines FOREIGN KEY (OrderId, LineNumber) REFERENCES sales.Lines (OrderId, LineNumber);
");
SchemaTable T(string name) => fkCatalog.Single(t => t.Name == name);
var ordersKey = T("Orders").ForeignKeys.Single();
Check(ordersKey.Columns.SequenceEqual(new[] { "PersonId" }) && ordersKey.ReferencedSchema == "dbo" && ordersKey.ReferencedTable == "People" &&
    ordersKey.ReferencedColumns.SequenceEqual(new[] { "Id" }), "column-level FK resolves primary key");
var linesKey = T("Lines").ForeignKeys.Single();
Check(linesKey.Columns.SequenceEqual(new[] { "OrderId" }) && linesKey.ReferencedTable == "Orders" && linesKey.ReferencedColumns.SequenceEqual(new[] { "OrderId" }), "table-level FK");
var notesKey = T("Notes").ForeignKeys.Single();
Check(notesKey.Columns.SequenceEqual(new[] { "OrderId", "LineNumber" }) && notesKey.ReferencedSchema == "sales" && notesKey.ReferencedTable == "Lines" &&
    notesKey.ReferencedColumns.SequenceEqual(new[] { "OrderId", "LineNumber" }), "ALTER TABLE ADD FOREIGN KEY");
Check(T("People").ForeignKeys.Count == 0, "no foreign keys");
Check(T("People").ColumnTypes!.SequenceEqual(new[] { "int", "nvarchar(100)", "date" }) && T("Orders").ColumnTypes!.SequenceEqual(new[] { "int", "int", "decimal(18,2)", null }) &&
    T("Notes").ColumnTypes![2] == "nvarchar(max)", "DDL column types");
Check(SchemaCatalog.FromDdl("CREATE TABLE dbo.C (Id int, Code dbo.Code);").Single().ColumnTypes![1] == "dbo.Code", "user-defined type");
try { SchemaCatalog.FromDdl("CREATE TABLE dbo.T (id int); ALTER TABLE dbo.Missing ADD CONSTRAINT F FOREIGN KEY (id) REFERENCES dbo.T (id);"); throw new Exception("Expected unknown ALTER target rejection"); }
catch (FormatException) { checks++; }
try { SchemaCatalog.FromDdl("CREATE TABLE dbo.T (id int); ALTER TABLE dbo.T ADD CONSTRAINT U UNIQUE (id);"); throw new Exception("Expected non-FK ALTER rejection"); }
catch (FormatException) { checks++; }
try { SchemaCatalog.FromDdl("CREATE TABLE dbo.T (id int); ALTER TABLE dbo.T DROP COLUMN id;"); throw new Exception("Expected ALTER DROP rejection"); }
catch (FormatException) { checks++; }
var plain = new SchemaTable("dbo", "X", "a");
Check(plain.ColumnTypes == null && plain.ForeignKeys.Count == 0, "legacy constructor defaults");
var live = new SchemaTable("dbo", "X", new[] { "a", "b" }, new string?[] { "int", null }, new[] { new SchemaForeignKey(new[] { "a" }, "dbo", "Y", new[] { "id" }) });
Check(live.ColumnTypes![1] == null && live.ForeignKeys.Single().ReferencedTable == "Y" &&
    new SchemaTable("dbo", "X", new[] { "a" }, new[] { new SchemaForeignKey(new[] { "a" }, "dbo", "Y", new[] { "id" }) }).ForeignKeys.Count == 1, "full constructors");
try { new SchemaTable("dbo", "X", new[] { "a" }, new string?[] { "int", "int" }, null); throw new Exception("Expected type count rejection"); }
catch (ArgumentException) { checks++; }
try { new SchemaForeignKey(new[] { "a", "b" }, "dbo", "Y", new[] { "id" }); throw new Exception("Expected FK column count rejection"); }
catch (ArgumentException) { checks++; }
Console.WriteLine($"PASS: {checks} total checks including foreign keys and column types. SSMS integration not tested.");

var nameItem = CompleteAt("SELECT p.| FROM dbo.People p;", fkCatalog).Items.Single(i => i.Name == "Name");
Check(nameItem.Description == "column nvarchar(100) NOT NULL p.Name" && nameItem.InsertText == "Name", "typed column description");
Check(CompleteAt("SELECT Na|me FROM dbo.People;", fkCatalog).Items[0].Description == "column nvarchar(100) NOT NULL People.Name", "typed unqualified column");
Check(CompleteAt("SELECT * FROM |", fkCatalog).Items.Single(i => i.Name == "Orders").Description == "table dbo.Orders", "table description");
var noFrom = CompleteAt("SELECT Na|", fkCatalog);
Check(noFrom.Items[0].Name == "Name" && noFrom.Items[0].InsertText == "Name" && noFrom.Start == 7 && noFrom.Length == 2, "SELECT column without FROM");
Check(Names(CompleteAt("SELECT * FROM dbo.|", fkCatalog)).SequenceEqual(new[] { "Orders", "People" }) && CompleteAt("SELECT * FROM dbo.|", fkCatalog).Items[0].InsertText == "Orders", "schema-dot tables");
Check(Names(CompleteAt("SELECT * FROM sales.| WHERE", fkCatalog)).SequenceEqual(new[] { "Lines", "Notes" }), "schema-dot tables in unparsable SQL");
Check(Names(CompleteAt("SELECT * FROM sales.L|", fkCatalog)).SequenceEqual(new[] { "Lines" }), "schema-dot prefix");
Check(CompleteAt("SELECT p.| FROM dbo.People p WHERE", fkCatalog).Items.Count == 3, "alias-dot columns in unparsable SQL");
Check(Names(CompleteAt("SELECT p.Na| FROM dbo.People AS p JOIN", fkCatalog)).SequenceEqual(new[] { "Name" }), "alias-dot prefix with AS");
Check(Names(CompleteAt("SELECT * FROM dbo.People p WHERE p.| AND", fkCatalog)).SequenceEqual(new[] { "Born", "Id", "Name" }), "alias-dot in WHERE");
Check(CompleteAt("SELECT dbo.People.| FROM dbo.People", fkCatalog).Items.Count == 3, "schema.table-dot columns");
Check(CompleteAt("SELECT People.| FROM", fkCatalog).Items.Count == 3, "table-dot columns without FROM source");
Check(CompleteAt("SELECT x.| FROM dbo.Missing x WHERE", fkCatalog).Items.Count == 0, "unknown alias metadata in unparsable SQL");
Check(Names(CompleteAt("SELECT d.| FROM (SELECT Id AS Pid, Name FROM dbo.People) d WHERE", fkCatalog)).SequenceEqual(new[] { "Name", "Pid" }), "derived columns in unparsable SQL");
Check(Names(CompleteAt("WITH recent (Oid) AS (SELECT OrderId FROM dbo.Orders), big AS (SELECT Total FROM dbo.Orders) SELECT r.| FROM recent r, big b WHERE", fkCatalog))
    .SequenceEqual(new[] { "Oid" }), "CTE columns in unparsable SQL");
var cte = CompleteAt("WITH recent AS (SELECT OrderId FROM dbo.Orders) SELECT * FROM re|", fkCatalog).Items.Single();
Check(cte.Name == "recent" && cte.Description == "cte", "CTE names in scope");
var where = CompleteAt("SELECT * FROM dbo.People p WHERE |", fkCatalog);
Check(where.Items[0].Name == "p" && where.Items[0].Description == "alias dbo.People" &&
    where.Items.Skip(1).TakeWhile(i => i.Description.StartsWith("column")).Count() == 3, "alias then columns after WHERE");
Check(CompleteAt("SELECT * FROM dbo.People p |", fkCatalog).Items[0].Description == "keyword" && Names(CompleteAt("SELECT * FROM dbo.People p WH|", fkCatalog)).SequenceEqual(new[] { "WHEN", "WHERE", "WHILE" }), "keywords first after a source");
var fromItems = CompleteAt("SELECT * FROM |", fkCatalog).Items;
Check(fromItems.Count == 4 && fromItems.All(i => i.Description.StartsWith("table")), "only tables after FROM");
Check(CompleteAt("UPDATE |", fkCatalog).Items.Count == 4 && CompleteAt("INSERT INTO sales.|", fkCatalog).Items.Count == 2 && CompleteAt("SELECT * FROM dbo.People p, |", fkCatalog).Items.Count == 4, "table positions");
var wide = Enumerable.Range(0, 300).Select(i => new SchemaTable("dbo", "T" + i, "c")).ToArray();
Check(CompleteAt("SELECT * FROM T|", wide).Items.Count == 200, "cap applies to tables");
Console.WriteLine($"PASS: {checks} total checks including incomplete-SQL completion. SSMS integration not tested.");

Check(CompleteAt("SELECT * FROM dbo.People p JOIN dbo.Orders o ON |", fkCatalog).Items[0].InsertText == "o.PersonId = p.Id", "FK join child to parent");
var reverse = CompleteAt("SELECT * FROM dbo.Orders AS o INNER JOIN dbo.People AS p ON |", fkCatalog).Items[0];
Check(reverse.Name == "p.Id = o.PersonId" && reverse.Description == "foreign key dbo.Orders -> dbo.People", "FK join parent to child");
Check(CompleteAt("SELECT * FROM dbo.People JOIN dbo.Orders ON |", fkCatalog).Items[0].Name == "Orders.PersonId = People.Id", "FK join without aliases");
Check(CompleteAt("SELECT * FROM People p LEFT JOIN Orders ON |", fkCatalog).Items[0].Name == "Orders.PersonId = p.Id", "FK join default schema");
Check(CompleteAt("SELECT * FROM sales.Lines l JOIN sales.Notes n ON |", fkCatalog).Items[0].Name == "n.OrderId = l.OrderId AND n.LineNumber = l.LineNumber", "composite FK join");
Check(CompleteAt("SELECT * FROM sales.Notes n JOIN sales.Lines l ON |", fkCatalog).Items[0].Name == "l.OrderId = n.OrderId AND l.LineNumber = n.LineNumber", "composite FK join reverse");
var chain = CompleteAt("SELECT * FROM dbo.People p JOIN dbo.Orders o ON o.PersonId = p.Id\nJOIN sales.Lines l ON |\nWHERE p.Id = 1;", fkCatalog);
Check(chain.Items[0].Name == "l.OrderId = o.OrderId" && chain.Items.Skip(1).Take(3).All(i => i.Description.StartsWith("alias")) &&
    chain.Items[4].Description.StartsWith("column"), "FK join to earlier source, then aliases, then columns");
var typed = CompleteAt("SELECT * FROM dbo.People p JOIN dbo.Orders o ON o|", fkCatalog);
Check(typed.Items[0].Name == "o" && typed.Items[1].Name == "o.PersonId = p.Id" && typed.Start == 48 && typed.Length == 1, "exact alias leads the FK join with typed prefix");
Check(CompleteAt("SELECT * FROM dbo.People p JOIN sales.Notes n ON |", fkCatalog).Items.All(i => !i.Name.Contains(" = ")), "no FK, no join condition");
Check(CompleteAt("SELECT * FROM dbo.People p WHERE |", fkCatalog).Items.All(i => !i.Name.Contains(" = ")), "join conditions only after ON");
Console.WriteLine($"PASS: {checks} total checks including FK join completion. SSMS integration not tested.");

var unusedItems = SqlRefactoring.UnusedDeclarationItems("DECLARE @a int;\nDECLARE @b int = 1;\nSELECT @a;");
Check(unusedItems.Count == 1 && unusedItems[0].Target == "@b" && unusedItems[0].Line == 2 && unusedItems[0].Offset == 24 && unusedItems[0].Length == 2, "unused declaration spans");
Console.WriteLine($"PASS: {checks} total checks including unused declaration spans. SSMS integration not tested.");

Check(SqlRefactoring.CreateToAlter("-- CREATE note\r\nCREATE   PROCEDURE dbo.p AS SELECT 'CREATE';") == "-- CREATE note\r\nALTER   PROCEDURE dbo.p AS SELECT 'CREATE';", "create to alter skips comments and strings");
Check(SqlRefactoring.CreateToAlter("/* x */ create or alter view v as select 1 a") == "/* x */ ALTER view v as select 1 a", "create or alter to alter");
Check(SqlRefactoring.CreateToAlter("CREATE OR /*c*/ ALTER FUNCTION f() RETURNS int AS BEGIN RETURN 1 END") == "ALTER FUNCTION f() RETURNS int AS BEGIN RETURN 1 END", "create or alter with inner comment");
Check(SqlRefactoring.CreateToAlter("SELECT 1") == "SELECT 1" && SqlRefactoring.CreateToAlter("") == "", "no create unchanged");
Check(SqlRefactoring.CreateToAlter("CREATE TRIGGER t ON dbo.x AFTER INSERT AS BEGIN CREATE TABLE #t(i int) END").StartsWith("ALTER TRIGGER") , "only first create changes");
Console.WriteLine($"PASS: {checks} total checks including CREATE to ALTER. SSMS integration not tested.");
Check(SqlNavigation.FindDefinition("EXEC otherdb.dbo.p;", 16) is { Offset: -1, Database: "otherdb", Schema: "dbo", Name: "p" }, "cross-database F12 names the database");
Check(SqlNavigation.FindDefinition("SELECT * FROM otherdb..People;", 23) is { Database: "otherdb", Schema: null, Name: "People" }, "cross-database default schema");
Check(SqlNavigation.FindDefinition("EXEC srv.otherdb.dbo.p;", 20) == null, "linked-server F12 left to host");
var procTarget = SqlNavigation.FindDefinition("EXEC dbo.usp_Load @x = 1;", 10);
Check(procTarget != null && procTarget.Offset < 0 && procTarget.Schema == "dbo" && procTarget.Name == "usp_Load", "F12 on procedure names object");
Console.WriteLine($"PASS: {checks} total checks including object F12 targets. SSMS integration not tested.");

// INSERT/EXEC fill, quick info, auto-fixes, object refactors, column picker.
var assistTables = SchemaCatalog.FromDdl("CREATE TABLE dbo.People (Id int IDENTITY PRIMARY KEY, FullName nvarchar(100) NOT NULL, Born date NULL, Code char(3), Stamp rowversion, Twice AS Id * 2);\n" +
    "CREATE TABLE sales.Orders (OrderId int NOT NULL, Total decimal(9,2), Ref uniqueidentifier, At datetimeoffset, Blob varbinary(max), Doc xml, Odd sql_variant);\n" +
    "CREATE TABLE dbo.OnlyId (Id int IDENTITY);");
var procs = SqlAssist.ProceduresFromScript("CREATE PROCEDURE dbo.usp_Add @Name nvarchar(50), @Age int = 18, @NewId int OUTPUT AS SELECT 1;\nGO\nCREATE PROC NoArgs AS SELECT 1;\nGO\nCREATE PROC broken AS SELEC");
Check(procs.Count == 2 && procs[0].Parameters.Count == 3 && procs[0].Parameters[1].HasDefault && procs[0].Parameters[2].IsOutput &&
    procs[0].Parameters[0].Type == "nvarchar(50)" && procs[1].Schema == "dbo", "procedures from script skip broken batches");
Check(assistTables[0].Generated!.SequenceEqual(new[] { true, false, false, false, true, true }), "identity, rowversion and computed are generated");
TextEdit? Fill(string text, IReadOnlyList<SchemaProcedure>? p = null)
{
    int position = text.IndexOf('|');
    return SqlAssist.FillStatement(text.Remove(position, 1), position, assistTables, p ?? procs);
}
var insertFill = Fill("INSERT INTO dbo.People|");
Check(insertFill != null && insertFill.Start == 22 && insertFill.Length == 0 && insertFill.Text ==
    "\n(\n    FullName,\n    Born,\n    Code\n)\nVALUES\n(\n    N'',       -- FullName - nvarchar(100)\n    GETDATE(), -- Born - date\n    ''         -- Code - char(3)\n)", "INSERT fill skips generated columns");
var indented = Fill("BEGIN\r\n\tINSERT People|\r\nEND");
Check(indented != null && indented.Text.StartsWith("\r\n\t(\r\n\t    FullName,") && indented.Text.EndsWith("\r\n\t)"), "INSERT fill keeps indent and CRLF");
var orders = Fill("insert sales.[Orders]|;");
Check(orders != null && orders.Text.Contains("    0,                   -- OrderId - int") && orders.Text.Contains("NEWID(),") && orders.Text.Contains("SYSDATETIMEOFFSET(),") &&
    orders.Text.Contains("0x,") && orders.Text.Contains("N'',") && orders.Text.Contains("    NULL                 -- Odd - sql_variant"), "INSERT fill placeholders by type");
Check(Fill("INSERT OnlyId|")!.Text == " DEFAULT VALUES", "all generated uses DEFAULT VALUES");
var spaced = Fill("EXEC dbo.usp_Add \t|");
Check(spaced is { Start: 16, Length: 2 } && spaced.Text.StartsWith(" @Name = "), "fill after trailing blanks replaces them");
Check(Fill("EXEC dbo.usp_Add\n|") == null, "no fill on the next line");
var dated = SqlAssist.ProceduresFromScript("CREATE PROC dbo.p @At datetime, @Id uniqueidentifier, @Off datetimeoffset AS SELECT 1;");
var execDates = SqlAssist.FillStatement("EXEC dbo.p", 10, null, dated, now: new DateTimeOffset(2026, 9, 29, 5, 10, 6, TimeSpan.FromHours(8)))?.Text ?? "";
Check(execDates.Contains("@At = '2026-09-29T05:10:06',") && execDates.Contains("@Off = '2026-09-29 05:10:06 +08:00'") && !execDates.Contains("("),
    "EXEC fill uses the current time as literals");
string[] ProcNames(string text) { int at = text.IndexOf('|'); return SqlCompletion.Complete(text.Remove(at, 1), at, null, procedures: procs).Items.Select(i => i.InsertText).ToArray(); }
Check(ProcNames("EXEC usp|").SequenceEqual(new[] { "dbo.usp_Add" }) && ProcNames("EXEC @rc = N|").SequenceEqual(new[] { "dbo.NoArgs" }) &&
    ProcNames("EXECUTE dbo.|").SequenceEqual(new[] { "NoArgs", "usp_Add" }) && ProcNames("EXEC sales.|").Length == 0, "procedure suggestions after EXEC");
Check(!ProcNames("SELECT usp|").Contains("dbo.usp_Add"), "procedures only after EXEC");
Check(Fill("INSERT INTO dbo.People| (Id) VALUES (1)") == null && Fill("INSERT INTO dbo.People|\nSELECT 1") == null && Fill("INSERT INTO dbo.People| x") == null,
    "INSERT fill skips continued statements");
Check(Fill("INSERT INTO dbo.People|\nSELECT 2;") == null && Fill("INSERT INTO dbo.Peo|ple") == null && Fill("INSERT INTO dbo.Missing|") == null &&
    Fill("SELECT * FROM dbo.People|") == null && Fill("-- INSERT INTO dbo.People|") == null && Fill("SELECT 'INSERT INTO dbo.People|") == null, "INSERT fill only after INSERT target");
Check(Fill("INSERT INTO dbo.People|\nGO") != null && Fill("INSERT INTO dbo.People|\n\nUPDATE x SET y = 1") != null && Fill("INSERT INTO dbo.People |") == null, "INSERT fill before later statements");
Check(Fill("INSERT INTO [dbo].[People]|") != null && Fill("INSERT INTO otherdb.dbo.People|") != null && Fill("INSERT INTO x.dbo.People|") != null, "bracketed and three-part names");
var exec = Fill("EXEC dbo.usp_Add|");
Check(exec != null && exec.Text == " @Name = N'',            -- nvarchar(50)\n                 @Age = DEFAULT,         -- int\n                 @NewId = @NewId OUTPUT  -- int", "EXEC fill aligned: " + exec?.Text);
var execRc = Fill("\tEXECUTE @rc = usp_Add|;");
Check(execRc != null && execRc.Text.Split('\n')[1].StartsWith("\t                      @Age"), "EXEC @rc fill aligns with tabs");
Check(Fill("EXEC NoArgs|") == null && Fill("EXEC dbo.usp_Add| @Name = N'x'") == null && Fill("EXEC dbo.usp_Add| 'x'") == null && Fill("EXEC @sql|") == null, "EXEC fill skips");
Check(Fill("EXEC dbo.usp_Add|\n@Name = 1") == null, "EXEC fill skips arguments on next line");

string? Info(string text)
{
    int position = text.IndexOf('|');
    return SqlAssist.Describe(text.Remove(position, 1), position, assistTables, procs);
}
Check(Info("DECLARE @x nvarchar(20) = N'';\nSELECT @x|;") == "@x: variable nvarchar(20)", "quick info variable type");
Check(Info("DECLARE @t TABLE (a int);\nSELECT * FROM @|t") == "@t: table variable", "quick info table variable");
Check(Info("CREATE PROC p @p int AS SELECT @p|;") == "@p: parameter int", "quick info parameter");
Check(Info("DECLARE @x int;\nSELECT @x| FROM") == "@x: variable int", "quick info variable in unfinished SQL");
Check(Info("SELECT @@IDENT|ITY") == null && Info("SELECT | 1") == null, "quick info nothing");
var tableInfo = Info("SELECT * FROM dbo.Peo|ple");
Check(tableInfo != null && tableInfo.StartsWith("table dbo.People") && tableInfo.Contains("  FullName nvarchar(100)") && tableInfo.Contains("  Twice"), "quick info table columns");
Check(Info("SELECT p.Full|Name FROM dbo.People p") == "FullName: column nvarchar(100) NOT NULL p.FullName", "quick info column");
Check(Info("SELECT p|.FullName FROM dbo.People p")!.StartsWith("p: alias"), "quick info alias");
var procInfo = Info("EXEC dbo.usp_A|dd");
Check(procInfo != null && procInfo.StartsWith("procedure dbo.usp_Add") && procInfo.Contains("@NewId int OUTPUT") && procInfo.Contains("@Age int = default"), "quick info procedure");
var wideTable = new SchemaTable("dbo", "Wide", Enumerable.Range(0, 60).Select(i => "c" + i).ToArray());
Check(SqlAssist.Describe("SELECT * FROM Wide", 16, new[] { wideTable }, null)!.EndsWith("... 10 more"), "quick info caps columns");

string FixOne(string sql, string rule, IReadOnlyList<SchemaTable>? t = null)
{
    var d = SqlAnalysis.Analyze(sql).Diagnostics.First(x => x.Rule == rule);
    var e = SqlAnalysis.Fix(sql, d, t);
    return e == null ? "<null>" : sql.Substring(0, e.Start) + e.Text + sql.Substring(e.Start + e.Length);
}
Check(FixOne("SELECT @@IDENTITY;", "SW009") == "SELECT SCOPE_IDENTITY();", "fix @@IDENTITY");
Check(FixOne("EXEC usp_Load;", "SW017") == "EXEC dbo.usp_Load;", "fix unqualified procedure");
Check(FixOne("SELECT 1 WHERE @a = NULL;", "SW003") == "SELECT 1 WHERE @a IS NULL;" && FixOne("SELECT 1 WHERE NULL <> (@a + 1);", "SW003") == "SELECT 1 WHERE (@a + 1) IS NOT NULL;" &&
    FixOne("SELECT 1 WHERE @a != NULL;", "SW003") == "SELECT 1 WHERE @a IS NOT NULL;" && FixOne("SELECT 1 WHERE @a > NULL;", "SW003") == "<null>", "fix NULL comparison");
Check(FixOne("CREATE TABLE t (a TEXT NOT NULL, b ntext NULL);", "SW010") == "CREATE TABLE t (a VARCHAR(MAX) NOT NULL, b ntext NULL);" &&
    FixOne("DECLARE @i [image];SELECT @i;", "SW010") == "DECLARE @i varbinary(max);SELECT @i;", "fix deprecated types");
Check(FixOne("CREATE PROCEDURE dbo.p\nAS\nBEGIN\n    SELECT 1;\nEND", "SW015") == "CREATE PROCEDURE dbo.p\nAS\nBEGIN\n    SET NOCOUNT ON;\n    SELECT 1;\nEND" &&
    FixOne("CREATE PROC dbo.p AS SELECT 1;", "SW015") == "CREATE PROC dbo.p AS SET NOCOUNT ON; SELECT 1;", "fix missing NOCOUNT");
Check(FixOne("DECLARE @a int;\nDECLARE @b int = 1;\nSELECT @a;", "SW016") == "DECLARE @a int;\nSELECT @a;" &&
    FixOne("DECLARE @a int, @b int;\nSELECT @b;", "SW016") == "DECLARE @b int;\nSELECT @b;" && FixOne("DECLARE @a int, @b int;\nSELECT @a;", "SW016") == "DECLARE @a int;\nSELECT @a;" &&
    FixOne("SELECT 1; DECLARE @t TABLE (a int);", "SW016") == "SELECT 1; ", "fix unused declaration");
Check(SqlCompletion.Complete("USE Q", 5, null, databases: new[] { "master", "QwTest", "Other DB" }).Items.Select(i => i.InsertText).SequenceEqual(new[] { "QwTest" }) &&
    SqlCompletion.Complete("USE ", 4, null, databases: new[] { "Other DB" }).Items.Single().InsertText == "[Other DB]", "USE suggests databases only");
Check(ObjectScript.SameScript("CREATE VIEW v AS\r\nSELECT 1  \r\n", "CREATE VIEW v AS\nSELECT 1") && !ObjectScript.SameScript("SELECT 1", "SELECT  1") && !ObjectScript.SameScript("a", null), "same script ignores line endings only");
Check(FixOne("SELECT * FROM dbo.People;", "SW001", assistTables) == "SELECT Id,\r\n       FullName,\r\n       Born,\r\n       Code,\r\n       Stamp,\r\n       Twice FROM dbo.People;" &&
    FixOne("SELECT * FROM dbo.Missing;", "SW001", assistTables) == "<null>" && FixOne("SELECT * FROM dbo.People;", "SW001") == "<null>", "fix wildcard needs metadata");
var fixedAll = SqlAnalysis.FixAll("DECLARE @unused int, @x int = 1;\nSELECT @@IDENTITY WHERE @x = NULL;\nEXEC usp_Load;\n-- querywright-disable-next-line SW009\nSELECT @@IDENTITY;");
Check(fixedAll.Fixed == 4 && fixedAll.Text == "DECLARE @x int = 1;\nSELECT SCOPE_IDENTITY() WHERE @x IS NULL;\nEXEC dbo.usp_Load;\n-- querywright-disable-next-line SW009\nSELECT @@IDENTITY;", "fix all respects suppression: " + fixedAll.Text);
Check(SqlAnalysis.FixAll("SELECT @@IDENTITY FROM").Fixed == 0 && SqlAnalysis.FixAll("SELEC 1").Text == "dbo.SELEC 1", "fix all needs parsable SQL");

var renameScript = SqlRefactoring.RenameObjectScript("dbo", "People", "Person", new[]
{
    ("dbo", "vPeople", "CREATE VIEW dbo.vPeople AS SELECT People.Id, dbo.People.FullName FROM People JOIN sales.People sp ON 1 = 1 -- People\n"),
    ("dbo", "usp_P", "CREATE PROCEDURE dbo.usp_P AS SELECT p.Id FROM dbo.People AS p WHERE dbo.People_Count() > 0 AND 'People' <> ''"),
    ("sales", "vS", "CREATE VIEW sales.vS AS SELECT 1 AS a FROM People"),
    ("dbo", "bad", "CREATE VIEW dbo.bad AS SELEC"),
}, "\n");
Check(renameScript.Contains("EXEC sys.sp_rename N'[dbo].[People]', N'Person';\nGO") &&
    renameScript.Contains("ALTER VIEW dbo.vPeople AS SELECT Person.Id, dbo.Person.FullName FROM Person JOIN sales.People sp ON 1 = 1 -- People\nGO") &&
    renameScript.Contains("ALTER PROCEDURE dbo.usp_P AS SELECT p.Id FROM dbo.Person AS p WHERE dbo.People_Count() > 0 AND 'People' <> ''") &&
    renameScript.Contains("-- sales.vS: no direct reference") && renameScript.Contains("-- dbo.bad: definition could not be parsed"), "rename object script: " + renameScript);
Check(SqlRefactoring.RenameObjectScript("dbo", "fn", "fn2", new[] { ("dbo", "v", "CREATE VIEW v AS SELECT dbo.fn(1) AS x") }, "\n").Contains("ALTER VIEW v AS SELECT dbo.fn2(1) AS x"), "rename scalar function callers");
var splitSource = new SchemaTable("dbo", "People", new[] { "Id", "FullName", "Bio", "Stamp" }, new string?[] { "int", "nvarchar(100)", "nvarchar(max)", "timestamp" }, null, new[] { true, false, false, true });
var split = SqlRefactoring.SplitTableScript(splitSource, new[] { "Id" }, new[] { "Bio" }, "dbo", "PeopleBio", "\n");
Check(split.Contains("CREATE TABLE [dbo].[PeopleBio]\n(\n    [Id] int NOT NULL,\n    [Bio] nvarchar(max) NULL,\n    CONSTRAINT [PK_PeopleBio] PRIMARY KEY ([Id]),"), "split table creates keyed table");
Check(split.Contains("FOREIGN KEY ([Id]) REFERENCES [dbo].[People] ([Id])") && split.Contains("INSERT INTO [dbo].[PeopleBio] ([Id], [Bio])\nSELECT [Id], [Bio]\nFROM [dbo].[People];")
    && split.Contains("ALTER TABLE [dbo].[People] DROP COLUMN [Bio];") && split.Contains("BEGIN TRANSACTION;") && split.Trim().EndsWith("COMMIT TRANSACTION;"), "split table copies then drops");
Throws<ArgumentException>(() => SqlRefactoring.SplitTableScript(splitSource, new[] { "Id" }, new[] { "Id" }, "dbo", "X"), "split table rejects key column");
Throws<ArgumentException>(() => SqlRefactoring.SplitTableScript(splitSource, new[] { "Id" }, new[] { "Stamp" }, "dbo", "X"), "split table rejects generated column");
Throws<ArgumentException>(() => SqlRefactoring.SplitTableScript(splitSource, new[] { "Id" }, new[] { "Bio" }, "dbo", "people"), "split table rejects same name");
Throws<ArgumentException>(() => SqlRefactoring.SplitTableScript(splitSource, new[] { "Id" }, new[] { "Nope" }, "dbo", "X"), "split table rejects unknown column");
Throws<InvalidOperationException>(() => SqlRefactoring.SplitTableScript(splitSource, Array.Empty<string>(), new[] { "Bio" }, "dbo", "X"), "split table needs a key");
Throws<InvalidOperationException>(() => SqlRefactoring.SplitTableScript(new SchemaTable("dbo", "T", "K", "V"), new[] { "K" }, new[] { "V" }, "dbo", "T2"), "split table needs known types");
Check(SqlRefactoring.SplitTableScript(new SchemaTable("s]x", "T", new[] { "K", "V" }, new string?[] { "int", "int" }, null), new[] { "K" }, new[] { "V" }, "s]x", "T2", "\n").Contains("[s]]x].[T2]"), "split table escapes brackets");
var invalidReport = SqlRefactoring.InvalidObjectsReport("Db", new[] { ("dbo", "vB", "Invalid column name 'x'.\r\nmore"), ("dbo", "pA", "gone"), ("dbo", "pA", "gone") }, "\n");
Check(invalidReport.Contains("(2 issues)") && invalidReport.IndexOf("dbo.pA: gone") < invalidReport.IndexOf("dbo.vB: Invalid column name 'x'. more"), "invalid objects report sorted, distinct, one line each");
Check(SqlRefactoring.InvalidObjectsReport(null, Array.Empty<(string, string, string)>(), "\n").Contains("No invalid objects found"), "invalid objects report empty");
Check(invalidReport.Split('\n').All(l => l.Length == 0 || l.StartsWith("--")), "invalid objects report is comments only");
Check(SqlRefactoring.RenameObjectScript("dbo", "t", "a b", null, "\n").Contains("N'a b'") && SqlRefactoring.RenameObjectScript("dbo", "t", "it's", null).Contains("N'it''s'"), "rename quotes new name");
foreach (var bad in new[] { "", "t", new string('x', 129), "a\nb" })
{
    try { SqlRefactoring.RenameObjectScript("dbo", "t", bad, null); throw new Exception("Expected rename rejection"); }
    catch (ArgumentException) { checks++; }
}
Check(SqlRefactoring.RenameObjectScript("dbo", "People", "P", new[] { ("dbo", "v", "CREATE VIEW v AS SELECT People.Id FROM dbo.Orders AS People") }, "\n").Contains("-- dbo.v: no direct reference"), "alias spelled like object is left alone");

string doc = "DECLARE @id int = 1, @name nvarchar(50);\nDECLARE @t TABLE (a int);\nSELECT @name = FullName FROM dbo.People WHERE Id = @id;\nPRINT @name;";
int selStart = doc.IndexOf("SELECT"), selLen = doc.IndexOf("\nPRINT") - selStart;
var encapsulated = SqlRefactoring.EncapsulateAsProcedure(doc, selStart, selLen, "dbo", "usp_GetName", "\n");
Check(encapsulated == "CREATE PROCEDURE dbo.usp_GetName\n    @name nvarchar(50) OUTPUT,\n    @id int\nAS\nBEGIN\n    SET NOCOUNT ON;\nSELECT @name = FullName FROM dbo.People WHERE Id = @id;\nEND;\nGO\n-- EXEC dbo.usp_GetName @name = @name OUTPUT, @id = @id;\n", "encapsulate: " + encapsulated);
Check(SqlRefactoring.EncapsulateAsProcedure("SELECT @q;", 0, 10, "dbo", "p", "\n").Contains("@q sql_variant"), "encapsulate unknown type");
Check(SqlRefactoring.EncapsulateAsProcedure("SELECT 1;", 0, 9, "my schema", "select", "\n").StartsWith("CREATE PROCEDURE [my schema].[select]\nAS"), "encapsulate quotes names");
Check(SqlRefactoring.EncapsulateAsProcedure("DECLARE @t TABLE (a int); SELECT * FROM @t;", 0, 43, "dbo", "p", "\n").Contains("DECLARE @t TABLE"), "encapsulate local table variable");
foreach (var (text, s, l) in new[] { (doc, doc.IndexOf("SELECT * FROM", StringComparison.Ordinal) < 0 ? doc.IndexOf("@t") : 0, 0), ("SELECT * FROM @t;", 0, 17), ("SELECT 1;\nGO\nSELECT 2;", 0, 20), ("SELEC 1", 0, 7), ("CREATE VIEW v AS SELECT 1 a", 0, 27) })
{
    try { SqlRefactoring.EncapsulateAsProcedure(text, s, l, "dbo", "p"); throw new Exception("Expected encapsulate rejection for " + text); }
    catch (Exception e) when (e is ArgumentException || e is InvalidOperationException || e is FormatException) { checks++; }
}

var picker = SqlCompletion.WildcardColumns("SELECT p.* FROM dbo.People p", 9, assistTables);
Check(picker.Wildcard.Start == 7 && picker.Wildcard.Text == "p.*" && picker.Columns.SequenceEqual(new[] { "p.Id", "p.FullName", "p.Born", "p.Code", "p.Stamp", "p.Twice" }), "column picker columns");

var fileTime = new DateTime(2026, 9, 29, 14, 5, 9);
Check(ResultGrid.FileName("SELECT * FROM lc.getRefDatabase WHERE descr LIKE '%x%'", fileTime) == "lc.getRefDatabase_20260929_140509", "result file name from FROM table");
Check(ResultGrid.FileName("select a from Orders o join dbo.Lines l on l.id = o.id", fileTime) == "dbo.Orders_20260929_140509", "result file name defaults schema to dbo");
Check(ResultGrid.FileName("SELECT 1", fileTime) == "Results_20260929_140509", "result file name without a table");
Check(ResultGrid.IsNumber("12") && !ResultGrid.IsNumber("12\n") && !ResultGrid.IsNumber("1\n;DROP TABLE t"), "IsNumber rejects a trailing newline");
// Deep nesting would overflow ScriptDom's recursive parser and end SSMS; it must surface as a parse error instead.
string deepCase = "SELECT " + string.Concat(Enumerable.Repeat("CASE WHEN 1=1 THEN ", 1000)) + "1" + string.Concat(Enumerable.Repeat(" END", 1000));
Throws<FormatException>(() => SqlFormatting.Format(deepCase), "deep nesting is a format error, not a crash");
Check(!SqlAnalysis.Analyze("SELECT " + new string('(', 5000)).Parsed, "deep unclosed parentheses analyze without crashing");
Throws<FormatException>(() => SqlFormatting.Format("SELECT 1 WHERE " + string.Join(" AND ", Enumerable.Range(0, 5000).Select(i => "1 = " + i))), "long operator chain is a format error");
Check(SqlFormatting.Format(string.Join(" UNION ALL ", Enumerable.Range(0, 1500).Select(i => "SELECT " + i))).Contains("SELECT 1499"), "1,500 UNION ALL branches still format");
Check(SqlFormatting.Format("BEGIN TRAN; " + string.Concat(Enumerable.Repeat("SELECT 1; ", 300)) + "COMMIT").Contains("COMMIT"), "BEGIN TRAN does not count as nesting");
Check(SqlAnalysis.Analyze(string.Concat(Enumerable.Repeat("IF @a = 1 PRINT 1 IF @b = 1 RAISERROR('x', 16, 1) WHILE @c = 1 BREAK ", 1000))).Parsed, "sequential IF and WHILE statements do not count as nesting");
Check(SqlAnalysis.Analyze("SELECT - -1, ~1, 1 - -1 WHERE NOT 1 = 2").Parsed, "short prefix operator runs parse");
Check(ResultGrid.FileName(null, fileTime) == "Results_20260929_140509", "result file name without query text");
Check(ResultGrid.InClause(new[] { "3", "1", "3", null, "-2.5" }) == "(3, 1, -2.5)", "IN clause numbers, distinct, NULL dropped");
Check(ResultGrid.InClause(new[] { "7", "007", "O'Brien" }) == "(N'7', N'007', N'O''Brien')", "IN clause mixed quotes all");
Check(ResultGrid.InClause(new[] { "NULL" }) == "(N'NULL')", "IN clause literal NULL text is a string");
try { ResultGrid.InClause(new string?[] { null }); throw new Exception("Expected empty IN rejection"); } catch (InvalidOperationException) { checks++; }
Check(ResultGrid.ColumnNames(new[] { "Id", "(No column name)", "id", "", null, "a]b" }).SequenceEqual(new[] { "Id", "Column2", "id_2", "Column4", "Column5", "a]b" }), "column names unique");
var gridRows = new List<string?[]> { new[] { "1", "Ann", "2024-01-02 03:04:05.123", "0x0A", "12.50" }, new[] { "2", null, null, null, "-1" } };
string insert = ResultGrid.InsertScript(new[] { "Id", "Name", "When", "Bin", "Amt" }, new[] { "int", "nvarchar(50)", "datetime", "varbinary(max)", null }, gridRows, "\n");
Check(insert.Contains("    [Id] int NULL,\n    [Name] nvarchar(50) NULL,\n    [When] datetime NULL,\n    [Bin] varbinary(max) NULL,\n    [Amt] decimal(38, 10) NULL\n);") &&
    insert.Contains("VALUES\n    (1, N'Ann', '2024-01-02T03:04:05.123', 0x0A, 12.50),\n    (2, NULL, NULL, NULL, -1);") && insert.Contains("\nDROP TABLE IF EXISTS #Results;\nCREATE TABLE #Results") && insert.EndsWith("SELECT * FROM #Results;\n\nDROP TABLE #Results;\n"), "insert script: " + insert);
string inferred = ResultGrid.InsertScript(new[] { "a", "b", "c" }, null, new List<string?[]> { new[] { "1", "x", "007" }, new[] { "3000000000", "it's", "1" } }, "\n");
Check(inferred.Contains("[a] bigint NULL") && inferred.Contains("[b] nvarchar(4) NULL") && inferred.Contains("[c] nvarchar(3) NULL") && inferred.Contains("(1, N'x', N'007')") && inferred.Contains("(3000000000, N'it''s', N'1')"), "insert inferred types: " + inferred);
Check(ResultGrid.InsertScript(new[] { "v" }, new[] { "int; DROP TABLE x" }, new List<string?[]> { new[] { "1" } }, "\n").Contains("[v] int NULL"), "insert rejects odd type text");
Check(ResultGrid.InsertScript(new[] { "r" }, new[] { "timestamp" }, new List<string?[]> { new[] { "0x00000000000007D1" } }, "\n").Contains("[r] binary(8) NULL") , "rowversion scripted as binary(8)");
var many = Enumerable.Range(0, 2500).Select(i => new string?[] { i.ToString() }).ToList();
string batched = ResultGrid.InsertScript(new[] { "n" }, new[] { "int" }, many, "\n");
Check(Regex.Matches(batched, "INSERT INTO").Count == 3 && batched.Contains("    (999);\n\nINSERT") && batched.Contains("(2499);"), "insert batches of 1000");
Check(ResultGrid.InsertScript(new[] { "n" }, null, new List<string?[]>(), "\n").Contains("[n] nvarchar(1) NULL") , "insert with no rows");
try { ResultGrid.InsertScript(new[] { "a", "b" }, null, new List<string?[]> { new[] { "1" } }); throw new Exception("Expected ragged rejection"); } catch (ArgumentException) { checks++; }
string csv = ResultGrid.Delimited(new[] { "a", "b" }, new List<string?[]> { new[] { "=1+1", "x,y" }, new[] { "-5", "say \"hi\"\nthere" }, new[] { null, "@SUM(A1)" }, new[] { "-x", "+1" } }, ',', "\n");
Check(csv == "a,b\n'=1+1,\"x,y\"\n-5,\"say \"\"hi\"\"\nthere\"\n,'@SUM(A1)\n'-x,'+1\n", "csv quoting and injection guard: " + csv);
Check(ResultGrid.Delimited(new[] { "a" }, new List<string?[]> { new[] { "x\ty" } }, '\t', "\n") == "a\n\"x\ty\"\n", "tab-delimited quoting");
Check(ResultGrid.Delimited(new[] { "a", "b" }, new List<string?[]> { new[] { "1", "x;y" } }, ';', "\n", includeHeaders: false) == "1;\"x;y\"\n", "csv without headers, semicolon");
string markdown = ResultGrid.Markdown(new[] { "a", "b" }, new List<string?[]> { new[] { "1", "x|y\nz" }, new[] { null, "" } }, "\n");
Check(markdown == "| a | b |\n| --- | --- |\n| 1 | x\\|y<br>z |\n| NULL |  |\n", "markdown table: " + markdown);
string json = ResultGrid.Json(new[] { "Id", "Name", "Code" }, new[] { "int", "nvarchar(10)", null }, new List<string?[]> { new[] { "1", "a\"b\\\n", "007" }, new[] { "2", null, "8" } }, "\n");
Check(json == "[\n  {\"Id\": 1, \"Name\": \"a\\\"b\\\\\\n\", \"Code\": \"007\"},\n  {\"Id\": 2, \"Name\": null, \"Code\": \"8\"}\n]\n", "json: " + json);
Check(ResultGrid.Json(new[] { "a" }, null, new List<string?[]>(), "\n") == "[]\n", "json empty");
var keyed = new List<string?[]> { new[] { "1", "Ann", "0x01" }, new[] { "2", "O'Neil", "0x02" } };
string update = ResultGrid.UpdateScript(new[] { "Id", "Name", "Ver" }, new[] { "int", "nvarchar(50)", "timestamp" }, keyed, "[dbo].[People]", "\n");
Check(update.EndsWith("UPDATE [dbo].[People] SET [Name] = N'Ann' WHERE [Id] = 1;\nUPDATE [dbo].[People] SET [Name] = N'O''Neil' WHERE [Id] = 2;\n") && update.StartsWith("-- UlysiSQL"), "update script: " + update);
try { ResultGrid.UpdateScript(new[] { "Id" }, null, keyed.Select(r => new[] { r[0] }).ToList(), "t"); Check(false, "update needs a column to set"); } catch (InvalidOperationException) { checks++; }
string merge = ResultGrid.MergeScript(new[] { "Id", "Name", "Ver" }, new[] { "int", "nvarchar(50)", "rowversion" }, keyed, "[dbo].[People]", "\n");
Check(merge.Contains("MERGE INTO [dbo].[People] AS target\nUSING (VALUES\n    (1, N'Ann'),\n    (2, N'O''Neil')\n) AS source ([Id], [Name])\nON target.[Id] = source.[Id]\nWHEN MATCHED THEN UPDATE SET target.[Name] = source.[Name]\nWHEN NOT MATCHED BY TARGET THEN INSERT ([Id], [Name]) VALUES (source.[Id], source.[Name]);\n") && !merge.Contains("DELETE"), "merge script: " + merge);
string createTable = ResultGrid.CreateTableScript(new[] { "Id", "Name" }, new[] { "int", null }, new List<string?[]> { new[] { "1", "x" }, new[] { "2", null } }, "#Results", "\n");
Check(createTable.EndsWith("CREATE TABLE #Results\n(\n    [Id] int NOT NULL,\n    [Name] nvarchar(1) NULL\n);\n"), "create table script: " + createTable);
Check(ResultGrid.SourceTable("SELECT * FROM Sales.Orders o JOIN dbo.X x ON 1=1") == "[Sales].[Orders]" && ResultGrid.SourceTable("SELECT 1") == null, "source table");
Check(SqlAnalysis.UnfilteredChanges("TRUNCATE TABLE dbo.T; DROP TABLE dbo.A, #b; DELETE FROM dbo.T", unfiltered: false, dropTruncate: true).SequenceEqual(new[] { "TRUNCATE TABLE dbo.T", "DROP TABLE dbo.A" }), "drop/truncate warning targets");
Check(SqlAnalysis.UnfilteredChanges("DROP TABLE #a; TRUNCATE TABLE #a; DELETE FROM #a; UPDATE ##g SET x = 1; ALTER TABLE #a ADD c int; DROP TABLE IF EXISTS #a;", true, true, true).Count == 0, "temp tables are not flagged");
string multi = SqlRefactoring.ForDatabases("CREATE OR ALTER PROC dbo.p AS SELECT 1;\n", new[] { "Sales", "O'Brien]x" }, newline: "\n");
Check(multi == "-- UlysiSQL: script for 2 databases. Review, then execute. Nothing has been run.\n\nUSE [Sales];\nPRINT N'Sales';\nGO\nCREATE OR ALTER PROC dbo.p AS SELECT 1;\nGO\n\nUSE [O'Brien]]x];\nPRINT N'O''Brien]x';\nGO\nCREATE OR ALTER PROC dbo.p AS SELECT 1;\nGO\n", "script for databases: " + multi);
string multiStop = SqlRefactoring.ForDatabases("SELECT 1\ngo", new[] { "A" }, stopOnError: true, printName: false, newline: "\n");
Check(multiStop.Contains(":on error exit\n") && multiStop.EndsWith("USE [A];\nGO\nSELECT 1\ngo\n") && !multiStop.Contains("PRINT"), "script for databases: stop on error, trailing GO kept once: " + multiStop);
try { SqlRefactoring.ForDatabases("SELECT 1", new string[0]); Check(false, "script for databases needs a database"); }
catch (ArgumentException) { checks++; }
Check(SqlRefactoring.ContainsUse("USE Sales;\nSELECT 1") && !SqlRefactoring.ContainsUse("SELECT 'USE x' -- USE y\nSELECT [use] FROM t"), "detects USE outside strings and comments");
Check(SqlAnalysis.UnfilteredChanges("TRUNCATE TABLE dbo.T; DELETE FROM dbo.T").SequenceEqual(new[] { "DELETE dbo.T" }), "drop/truncate off by default");
string colorRules = ColorRules.Set("prod=Red; test=Orange", ColorRules.ServerPattern(@"10.0.0.1\SQL"), "#FF8800");
Check(colorRules == @"10.0.0.1\SQL/=#FF8800;prod=Red;test=Orange" && ColorRules.Get(colorRules, @"10.0.0.1\sql/") == "#FF8800", "server color rule added first: " + colorRules);
Check(ColorRules.Set(ColorRules.Set(colorRules, @"10.0.0.1\SQL/", "Blue"), @"10.0.0.1\SQL/", null) == "prod=Red;test=Orange" && ColorRules.Get("", "x/") == null, "server color rule replaced and cleared");
try { ColorRules.Set("", "a=b/", "Red"); throw new Exception("Expected odd server rejection"); } catch (ArgumentException) { checks++; }
var xlsxStream = new MemoryStream();
ResultGrid.Xlsx(xlsxStream, new[] { "When", "Code", "Amt", "Big", "Note", "" }, new[] { "datetime", "varchar(10)", "decimal(18,2)", "bigint", "nvarchar(max)", "float" },
    new List<string?[]> { new[] { "2026-09-28 13:45:12.123", "007", "12.50", "1234567890123456789", "=1+1 <b>&\u0001", "1.5E-05" }, new string?[] { null, "x", "-3.00", "42", "", "0" } });
string sheet;
using (var zip = new System.IO.Compression.ZipArchive(new MemoryStream(xlsxStream.ToArray())))
{
    Check(zip.Entries.Select(e => e.FullName).OrderBy(n => n, StringComparer.Ordinal).SequenceEqual(new[] { "[Content_Types].xml", "_rels/.rels", "xl/_rels/workbook.xml.rels", "xl/styles.xml", "xl/workbook.xml", "xl/worksheets/sheet1.xml" }), "xlsx parts");
    using (var reader = new StreamReader(zip.GetEntry("xl/worksheets/sheet1.xml")!.Open())) sheet = reader.ReadToEnd();
    string xlsxStyles;
    using (var reader = new StreamReader(zip.GetEntry("xl/styles.xml")!.Open())) xlsxStyles = reader.ReadToEnd();
    Check(xlsxStyles.Contains("formatCode=\"0.00\"") && xlsxStyles.Contains("formatCode=\"0\""), "xlsx keeps grid decimal places: " + xlsxStyles);
    foreach (var e in zip.Entries) using (var reader = new StreamReader(e.Open())) System.Xml.Linq.XDocument.Parse(reader.ReadToEnd());
    checks++;
}
Check(sheet.Contains("<c r=\"A2\" s=\"2\" t=\"inlineStr\"><is><t xml:space=\"preserve\">2026-09-28 13:45:12.123</t>"), "xlsx date kept as grid text");
Check(sheet.Contains(">007</t>") && sheet.Contains(">1234567890123456789</t>") && sheet.Contains(">1.5E-05</t>"), "xlsx codes, long numbers and exponents kept as text");
Check(sheet.Contains("<c r=\"C2\" s=\"3\"><v>12.50</v>") && sheet.Contains("<c r=\"D3\" s=\"4\"><v>42</v>") && sheet.Contains("<v>-3.00</v>"), "xlsx numeric columns as numbers: " + sheet);
Check(sheet.Contains("=1+1 &lt;b&gt;&amp;</t>") && !sheet.Contains("<f>"), "xlsx escapes text and never writes formulas");
Check(!sheet.Contains("r=\"A3\"") && sheet.Contains(">Column6</t>"), "xlsx NULL is an empty cell; unnamed header named");
Check(Updates.IsNewer("v0.4.0", "0.3.0.57") && Updates.IsNewer("v0.3.1", "0.3.0") && Updates.IsNewer("V1.0", "0.9.9.9"), "newer release detected");
Check(!Updates.IsNewer("v0.3.0", "0.3.0.57") && !Updates.IsNewer("v0.2.9", "0.3.0.1") && !Updates.IsNewer("dev-master", "0.3.0.1")
    && !Updates.IsNewer("v0.4.0", null) && !Updates.IsNewer(null, "0.3.0") && !Updates.IsNewer("v0.4.0-beta", "0.3.0"), "same, older or malformed versions ignored");
Check(Updates.ErrorLine("0.3.0.42", "SplitTableAsync", typeof(FormatException)) == "UlysiSQL 0.3.0.42 \u00B7 SplitTable \u00B7 FormatException"
    && Updates.ErrorLine(null, "Format", typeof(IOException)) == "UlysiSQL unknown \u00B7 Format \u00B7 IOException", "error report line");
Check(Updates.IssueUrl("UlysiSQL 0.3.0 \u00B7 Format \u00B7 IOException") == "https://github.com/ulysis022219/Querywright/issues/new?template=bug_report.yml&error=UlysiSQL%200.3.0%20%C2%B7%20Format%20%C2%B7%20IOException", "issue link escapes the line");
var scriptColumns = new[]
{
    new ScriptColumn("Id", "int", null, false) { Identity = "1, 1" },
    new ScriptColumn("Code", "varchar", "50", true) { Collation = "SQL_Latin1_General_CP1_CI_AS" },
    new ScriptColumn("Amt", "decimal", "18,2", false) { DefaultName = "DF_T_Amt", Default = "((0))" },
    new ScriptColumn("Twice", "int", null, true) { Computed = "([Id]*(2))", Persisted = true },
};
string create = ObjectScript.CreateTable("FC", "Acct]Hdr", scriptColumns, "PRIMARY", new[]
{
    ObjectScript.Key("PK_T", true, true, new[] { ("Id", false) }, "PRIMARY"),
    ObjectScript.ForeignKey("FK_T", new[] { "Code" }, "dbo", "Codes", new[] { "Code" }, "CASCADE", "NO_ACTION"),
    ObjectScript.Check("CK_T", "([Amt]>=(0))"),
}, "\n");
Check(create == "CREATE TABLE [FC].[Acct]]Hdr]\n(\n[Id] [int] NOT NULL IDENTITY(1, 1),\n[Code] [varchar] (50) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,\n" +
    "[Amt] [decimal] (18,2) NOT NULL CONSTRAINT [DF_T_Amt] DEFAULT ((0)),\n[Twice] AS ([Id]*(2)) PERSISTED\n) ON [PRIMARY]\nGO\n" +
    "ALTER TABLE [FC].[Acct]]Hdr] ADD CONSTRAINT [PK_T] PRIMARY KEY CLUSTERED ([Id]) ON [PRIMARY]\nGO\n" +
    "ALTER TABLE [FC].[Acct]]Hdr] ADD CONSTRAINT [FK_T] FOREIGN KEY ([Code]) REFERENCES [dbo].[Codes] ([Code]) ON DELETE CASCADE\nGO\n" +
    "ALTER TABLE [FC].[Acct]]Hdr] ADD CONSTRAINT [CK_T] CHECK ([Amt]>=(0))\nGO\n", "object script: " + create);
Check(scriptColumns[1].DataType == "varchar(50)" && scriptColumns[0].DataType == "int" && scriptColumns[3].DataType == "computed", "summary data types");
try { ObjectScript.CreateTable("dbo", "t", Array.Empty<ScriptColumn>(), null); throw new Exception("Expected empty table rejection"); } catch (ArgumentException) { checks++; }
Check(ObjectScript.Header("NOAH_BALCOR", "P", "FC", "nsp_BalanceSheet", true, true, "9/29/2026 7:10:36 PM", "\n") ==
    "USE [NOAH_BALCOR]\nGO\n/****** Object:  StoredProcedure [FC].[nsp_BalanceSheet]    Script Date: 9/29/2026 7:10:36 PM ******/\n" +
    "SET ANSI_NULLS ON\nGO\nSET QUOTED_IDENTIFIER ON\nGO\n", "SSMS script header");
Check(ObjectScript.Header(null, "IF", "dbo", "f", false, false, "d", "\n").StartsWith("/****** Object:  UserDefinedFunction [dbo].[f]") &&
    ObjectScript.Header(null, "IF", "dbo", "f", false, false, "d", "\n").Contains("SET ANSI_NULLS OFF\nGO\nSET QUOTED_IDENTIFIER OFF"), "script header options");
// Catalog assembly edge cases: nothing may throw, bad rows are dropped one by one.
var asm_catEmpty = CatalogAssembler.Tables(new (string?, string?, string?, string?, bool, bool, string?)[0], new (int, string?, string?, string?, string?, string?, string?)[0]);
Check(asm_catEmpty.Tables.Length == 0 && asm_catEmpty.Skipped == 0, "catalog empty");
var asm_catNull = CatalogAssembler.Tables(null!, null!);
Check(asm_catNull.Tables.Length == 0, "catalog null input");
var asm_catCols = new (string?, string?, string?, string?, bool, bool, string?)[]
{
    ("dbo", "B", "Id", "int", true, false, "NOT NULL DEFAULT ((0))"), ("dbo", "B", null, "int", false, false, null), ("dbo", "B", "  ", null, false, false, null), ("dbo", "B", "Id", "int", false, false, null),
    ("dbo", "B", "Note", null, false, false, null), ("dbo", "a", "x", "int", false, true, null), (null, "Orphan", "c", "int", false, false, null), ("dbo", " ", "c", "int", false, false, null),
    ("dbo", "OnlyBlank", null, null, false, false, null), ("Sales", "C", "a", "int", false, false, null), ("Sales", "C", "A", "int", false, false, null),
    ("dbo", "we]ird'\"[name", "c\u00e9", "nvarchar(5)", false, false, null),
};
var asm_catKeys = new (int, string?, string?, string?, string?, string?, string?)[]
{
    (1, "dbo", "B", "Id", "dbo", "a", "x"),
    (2, "dbo", "B", "Missing", "dbo", "a", "x"),
    (3, "dbo", "B", "Id", null, null, null),
    (4, "dbo", "B", null, "dbo", "a", "x"),
    (5, "gone", "T", "c", "dbo", "a", "x"),
    (6, "dbo", "B", "Id", "dbo", "a", "x"), (6, "dbo", "B", "Note", "dbo", "a", "x"),
};
var asm_catBuilt = CatalogAssembler.Tables(asm_catCols, asm_catKeys);
Check(asm_catBuilt.Tables.Length == 4 && asm_catBuilt.Skipped == 1, "catalog keeps usable tables: " + asm_catBuilt.Tables.Length + "/" + asm_catBuilt.Skipped);
Check(asm_catBuilt.Tables[0].Name == "a" && asm_catBuilt.Tables[1].Name == "B" && asm_catBuilt.Tables[0].IsView, "catalog sorted case-insensitively");
var asm_catB = asm_catBuilt.Tables.First(t => t.Name == "B");
Check(asm_catB.Columns.SequenceEqual(new[] { "Id", "Note" }) && asm_catB.Generated![0], "catalog drops blank and duplicate columns");
Check(asm_catB.ForeignKeys.Count == 2 && asm_catB.ForeignKeys.Any(k => k.Columns.Count == 2), "catalog drops orphan, blank and mismatched foreign keys");
Check(asm_catBuilt.Tables.First(t => t.Name == "C").Columns.Count == 2, "catalog keeps case-distinct columns");
Check(asm_catBuilt.Tables.Any(t => t.Name.Contains("]")), "catalog keeps odd characters");
var asm_procs = CatalogAssembler.Procedures(new (string?, string?, string?, string?, bool, bool)[]
{
    ("dbo", "p2", "@a", "int", false, false), ("dbo", "p2", "bad", "int", false, false), ("dbo", "p2", null, null, false, false),
    ("dbo", "p1", null, null, false, false), (null, "x", "@a", null, false, false), ("dbo", " ", "@a", null, false, false),
});
Check(asm_procs.Length == 2 && asm_procs[0].Name == "p1" && asm_procs[0].Parameters.Count == 0 && asm_procs[1].Parameters.Count == 1, "catalog procedures tolerate bad rows");
Check(CatalogAssembler.Procedures(null!).Length == 0, "catalog procedures null input");
var ob_items = Complete("SELECT * FROM People ORD|").Items;
Check(ob_items.Any(i => i.Name == "ORDER BY" && i.InsertText == "ORDER BY"), "ORDER completes to ORDER BY");
var ph_procs = new[]
{
    new SchemaProcedure("dbo", "Load", new[] { new SchemaParameter("@id", "int", false, false), new SchemaParameter("@name", "nvarchar(50)", false, true), new SchemaParameter("@out", "int", true, false) }),
    new SchemaProcedure("dbo", "Tax", new[] { new SchemaParameter("@amount", "money", false, false), new SchemaParameter("@rate", "decimal(5,2)", false, false) }, isFunction: true),
};
ParameterHint? Hint(string text) { int at = text.IndexOf('|'); return SqlAssist.ParameterHintAt(text.Remove(at, 1), at, ph_procs); }
Check(Hint("EXEC dbo.Load |") is { Name: "dbo.Load", Current: 0 } h1 && h1.Parameters.Count == 3 && h1.Parameters[2].Contains("OUTPUT"), "parameter hint after EXEC");
Check(Hint("EXEC Load 1, |") is { Current: 1 }, "parameter hint second argument");
Check(Hint("EXEC dbo.Load @out = |") is { Current: 2 }, "parameter hint named argument");
Check(Hint("EXEC dbo.Load 1, 2, 3, |") is { Current: -1 }, "parameter hint past last parameter");
Check(Hint("EXEC dbo.Lo|") == null, "no parameter hint while typing the name");
Check(Hint("SELECT dbo.Tax(|") is { Name: "dbo.Tax", Current: 0 }, "function parameter hint");
Check(Hint("SELECT dbo.Tax(1, |") is { Current: 1 }, "function hint second argument");
Check(Hint("SELECT dbo.Tax(1); SELECT |") == null, "no hint after statement end");
Check(Hint("EXEC dbo.Tax |") == null, "EXEC hint ignores functions");
var ph_exec = SqlCompletion.Complete("EXEC dbo.", 9, catalog, procedures: ph_procs).Items;
Check(ph_exec.Any(i => i.Name == "Load") && !ph_exec.Any(i => i.Name == "Tax"), "EXEC completion excludes functions");
var ml = SqlAssist.MatchingLines("CREATE PROC p AS\nSELECT * FROM Orders;\nSELECT * FROM OrdersArchive;\n-- orders", "Orders");
Check(ml.Count == 2 && ml[0].Line == 2 && ml[1].Line == 4, "matching lines whole word: " + string.Join(",", ml.Select(m => m.Line)));
Check(SqlAssist.MatchingLines("a\nx.Orders_Id = 1", "rs_Id =").Count == 1, "matching lines substring for fragments");
var rg = SqlNavigation.Regions("--region Load\nSELECT 1;\n  -- #region inner\nSELECT 2;\n--endregion\n--endregion\n--region open");
Check(rg.Count == 2 && rg[0].Start == 0 && rg[0].Label == "--region Load" && rg[1].Label == "-- #region inner", "regions pair and nest");
Check(ResultGrid.SelectionSummary(new[] { "1", "2.5", null, "x" }) == "Count: 3    Sum: 3.5    Avg: 1.75    Min: 1    Max: 2.5", "selection totals: " + ResultGrid.SelectionSummary(new[] { "1", "2.5", null, "x" }));
Check(ResultGrid.SelectionSummary(new[] { "a", "b" }) == "Count: 2", "selection totals without numbers");
var unmatched = SqlNavigation.Unmatched("BEGIN SELECT (1\nSELECT ')' -- END (\nCASE\nGO\nBEGIN TRAN; END CONVERSATION @h; SELECT 1) END");
Check(string.Join(",", unmatched.Select(u => u.Message[0])) == "B,(,C,),E" && unmatched[1].Start == 13 && unmatched[3].Length == 1,
    "flag unmatched BEGIN, (, ), CASE per batch, skipping strings, comments and BEGIN TRAN");
Check(SqlNavigation.Unmatched("BEGIN TRY SELECT CASE WHEN (1) = 1 THEN 1 END; END TRY BEGIN CATCH END CATCH").Count == 0, "matched TRY/CATCH and CASE are not flagged");

var noted = SchemaCatalog.FromDdl("CREATE TABLE dbo.N (Id int PRIMARY KEY, Name nvarchar(20) NOT NULL DEFAULT (N'x'), Tag int NULL, Plain int);");
Check(noted.Single().ColumnNotes!.SequenceEqual(new[] { "NOT NULL", "NOT NULL DEFAULT (N'x')", "NULL", null }), "DDL column nullability and default");
Check(SqlAssist.Describe("SELECT Name FROM dbo.N;", 8, noted, null) == "Name: column nvarchar(20) NOT NULL DEFAULT (N'x') N.Name", "column hover shows type, nullability and default");
Check(asm_catB.ColumnNotes![0] == "NOT NULL DEFAULT ((0))", "catalog keeps live column notes");

// Column suggestions per table: scope, grouping, cap and INSERT/MERGE targets.
{
    var shop = new[] { new SchemaTable("dbo", "Customers", "CustId", "CustName", "City"), new SchemaTable("dbo", "Orders", "OrderId", "CustId", "Total") };
    string[] Cols(string text)
    {
        int at = text.IndexOf('|');
        return SqlCompletion.Complete(text.Remove(at, 1), at, shop).Items.Where(i => i.Description.StartsWith("column")).Select(i => i.InsertText).ToArray();
    }
    Check(string.Join(",", Cols("SELECT | FROM Customers c JOIN Orders o ON c.CustId = o.CustId")) == "c.City,c.CustId,c.CustName,o.CustId,o.OrderId,o.Total", "columns grouped per table in FROM order");
    Check(Cols("SELECT * FROM Customers c JOIN Orders o ON c.CustId = o.CustId WHERE |").Length == 6, "scanner lists every joined table");
    Check(Cols("UPDATE c SET | FROM Customers c JOIN Orders o ON o.CustId = c.CustId").Contains("c.City"), "UPDATE alias keeps its FROM table columns");
    Check(Cols("SELECT TOP 10 | FROM Orders").Length == 3 && Cols("SELECT TOP (1000) | FROM Orders o").Length == 3, "TOP n expects columns");
    Check(string.Join(",", Cols("INSERT INTO Customers (|) VALUES (1)")) == "City,CustId,CustName" && Cols("INSERT dbo.Customers (CustId, |").Length == 3, "INSERT column list");
    Check(Cols("MERGE INTO Customers AS t USING Orders AS s ON t.CustId = s.CustId WHEN NOT MATCHED THEN INSERT (|").Contains("CustName"), "MERGE INSERT column list");
    Check(string.Join(",", Cols("SELECT * FROM Orders o JOIN (SELECT CustId AS K, City FROM Customers) d ON d.K = o.CustId WHERE |")) == "o.CustId,o.OrderId,o.Total,d.City,d.K", "derived table body stays out of scope");
    Check(Cols("SELECT * FROM Customers c WHERE c.CustId IN (SELECT o.CustId FROM Orders o WHERE |)")[0] == "o.CustId", "innermost subquery columns first");
    Check(Cols("SELECT * FROM \"dbo\".\"Customers\" WHERE |").All(c => c.StartsWith("Customers.")), "double-quoted table names resolve");
    var manyTables = Enumerable.Range(0, 300).Select(n => new SchemaTable("dbo", "T" + n, "C" + n)).Concat(shop).ToArray();
    Check(SqlCompletion.Complete("SELECT * FROM Orders o WHERE o.Total > 1 AND o.CustId = 1 ORDER BY o.Total ", 75, manyTables).Items.Count(i => i.Description.StartsWith("column")) == 3, "item cap keeps in-scope columns");
}
Check(SqlAnalysis.UnfilteredChanges("DROP PROCEDURE dbo.p; ALTER TABLE dbo.T ADD c int; TRUNCATE TABLE dbo.T; SELECT 1; UPDATE dbo.T SET c = 1 WHERE c = 2", false, false, schema: true)
    .SequenceEqual(new[] { "DROP PROCEDURE dbo.p", "ALTER TABLE dbo.T ADD c int" }) && SqlAnalysis.UnfilteredChanges("ALTER TABLE dbo.T ADD c int").Count == 0, "production: DROP/ALTER only with schema");
Check(SqlAnalysis.UnfilteredChanges("ALTER PROCEDURE dbo.p AS\nSELECT 1 FROM dbo.T WHERE Id = 1", false, false, true).Single() == "ALTER PROCEDURE dbo.p AS"
    && SqlAnalysis.UnfilteredChanges("ALTER TABLE dbo." + new string('x', 100) + " ADD c int", false, false, true).Single().Length == 80, "production: first line of statement, capped");
Check(SqlAnalysis.UnfilteredChanges("CREATE PROC dbo.p AS BEGIN DELETE FROM dbo.T; UPDATE dbo.T SET c = 1; TRUNCATE TABLE dbo.T; DROP TABLE dbo.A; ALTER TABLE dbo.T ADD d int; END", true, true, true).Count == 0
    && SqlAnalysis.UnfilteredChanges("CREATE TRIGGER dbo.tr ON dbo.T AFTER INSERT AS DELETE FROM dbo.Log;", true, true, true).Count == 0
    && SqlAnalysis.UnfilteredChanges("CREATE FUNCTION dbo.f() RETURNS @r TABLE (a int) AS BEGIN INSERT @r VALUES (1); UPDATE @r SET a = 2; RETURN; END", true, true, true).Count == 0
    && SqlAnalysis.UnfilteredChanges("ALTER PROC dbo.p AS DELETE FROM dbo.T;", true, true, true).SequenceEqual(new[] { "ALTER PROC dbo.p AS DELETE FROM dbo.T" })
    && SqlAnalysis.UnfilteredChanges("CREATE PROC dbo.p AS DELETE FROM dbo.T;\nGO\nDELETE FROM dbo.T;").SequenceEqual(new[] { "DELETE dbo.T" }), "module bodies are stored, not run");
Check(SqlAnalysis.UnfilteredChanges("DECLARE @t TABLE (a int); DELETE FROM @t; UPDATE @t SET a = 1; DELETE dbo.T;").SequenceEqual(new[] { "DELETE dbo.T" }), "table variables are not flagged");
Check(SqlRefactoring.RedactSecrets("CREATE LOGIN x WITH PASSWORD = N'p''w', CHECK_POLICY = OFF;") == "CREATE LOGIN x WITH PASSWORD = '***', CHECK_POLICY = OFF;"
    && SqlRefactoring.RedactSecrets("ALTER LOGIN x WITH PASSWORD='new' OLD_PASSWORD = 'old'") == "ALTER LOGIN x WITH PASSWORD = '***' OLD_PASSWORD = '***'"
    && SqlRefactoring.RedactSecrets("CREATE LOGIN x WITH PASSWORD = 0x0200AB HASHED") == "CREATE LOGIN x WITH PASSWORD = '***' HASHED"
    && SqlRefactoring.RedactSecrets("CREATE DATABASE SCOPED CREDENTIAL c WITH IDENTITY = 'me', SECRET = 'sig'") == "CREATE DATABASE SCOPED CREDENTIAL c WITH IDENTITY = 'me', SECRET = '***'"
    && SqlRefactoring.RedactSecrets("EXEC sp_addlinkedsrvlogin 'srv', 'false', NULL, 'sa', @rmtpassword = 'pw'; DECLARE @Pwd nvarchar(50) = N'pw';") == "EXEC sp_addlinkedsrvlogin '***', '***', NULL, '***', @rmtpassword = '***'; DECLARE @Pwd nvarchar(50) = '***';"
    && SqlRefactoring.RedactSecrets("EXEC sp_addlinkedsrvlogin N'srv', 'false', NULL, 'sa', N'p;w'\nEXEC sp_password NULL, 'new', 'me' SELECT 'keep'") == "EXEC sp_addlinkedsrvlogin N'***', '***', NULL, '***', N'***'\nEXEC sp_password NULL, '***', '***' SELECT 'keep'"
    && SqlRefactoring.RedactSecrets("EXEC sp_change_users_login 'Auto_Fix', 'u', NULL, 'pw'") == "EXEC sp_change_users_login '***', '***', NULL, '***'"
    && SqlRefactoring.RedactSecrets("SELECT * FROM OPENROWSET('MSOLEDBSQL', 'Server=s;Uid=sa;Pwd=a b;', 'SELECT 1')") == "SELECT * FROM OPENROWSET('MSOLEDBSQL', 'Server=s;Uid=sa;Pwd=***;', 'SELECT 1')"
    && SqlRefactoring.RedactSecrets("CREATE LOGIN x WITH PASSWORD = @p; SELECT Password, Pwd FROM dbo.Users WHERE Secret = 1") == "CREATE LOGIN x WITH PASSWORD = @p; SELECT Password, Pwd FROM dbo.Users WHERE Secret = 1"
    && SqlRefactoring.RedactSecrets("SELECT ENCRYPTBYPASSPHRASE(N'p''w', 'data'), DecryptByPassphrase ( 'pw', @c), ENCRYPTBYPASSPHRASE(@k, 'x')") == "SELECT ENCRYPTBYPASSPHRASE(N'***', 'data'), DecryptByPassphrase ( '***', @c), ENCRYPTBYPASSPHRASE(@k, 'x')"
    && SqlRefactoring.RedactSecrets("BACKUP DATABASE d TO DISK = 'f' WITH MEDIAPASSWORD = 'm'") == "BACKUP DATABASE d TO DISK = 'f' WITH MEDIAPASSWORD = '***'"
    && SqlRefactoring.RedactSecrets("SELECT * FROM OPENROWSET('SQLNCLI', 's';'sa';N'p''w', 'SELECT 1')") == "SELECT * FROM OPENROWSET('SQLNCLI', 's';'sa';N'***', 'SELECT 1')", "tab history redacts secrets");
Check(ColorRules.Matches("dev; PROD ", @"sql-prod01\A", "Sales") && ColorRules.Matches("prod01/Sales", "prod01", "Sales") && !ColorRules.Matches("prod", "dev01", "Sales")
    && !ColorRules.Matches(";;", "x", "y") && !ColorRules.Matches("prod", null, null), "production server patterns");
Check(typeof(WorkbenchSettings).GetProperties().Where(p => p.PropertyType == typeof(RuleSeverity))
    .All(p => p.GetCustomAttributes(typeof(System.ComponentModel.DisplayNameAttribute), false).Cast<System.ComponentModel.DisplayNameAttribute>().Single().DisplayName.StartsWith(p.Name + " ")), "every rule has a display name in the settings editor");

// Stress: huge or deeply nested input on a 1 MB stack (the UI thread's size) must not overflow; a stack overflow ends this process, failing CI.
{
    var cat = new[] { new SchemaTable("dbo", "Orders", "OrderId", "CustId", "Total") };
    var shapes = new Func<int, string>[]
    {
        n => "SELECT " + new string('(', n) + "1" + new string(')', n),
        n => "SELECT " + new string('(', n),
        n => string.Concat(Enumerable.Repeat("IF 1=1 BEGIN ", n)) + "SELECT 1 " + string.Concat(Enumerable.Repeat("END ", n)),
        n => "SELECT " + string.Concat(Enumerable.Repeat("CASE WHEN 1=1 THEN ", n)) + "1" + string.Concat(Enumerable.Repeat(" END", n)),
        n => "SELECT " + string.Concat(Enumerable.Repeat("(SELECT ", n)) + "1" + new string(')', n),
        n => "IF 1=1 SELECT 1 " + string.Concat(Enumerable.Repeat("ELSE IF 1=1 SELECT 1 ", n)),
        n => "SELECT * FROM Orders o WHERE " + string.Join(" AND ", Enumerable.Range(0, n).Select(i => "o.Total <> " + i)),
        n => "SELECT " + string.Join(" + ", Enumerable.Range(0, n)),
        n => string.Join(" UNION ALL ", Enumerable.Range(0, n).Select(i => "SELECT " + i)),
        n => "DELETE FROM Orders WHERE " + string.Join(" OR ", Enumerable.Range(0, n).Select(i => "OrderId = " + i)),
        // Prefix operators and bare IF/WHILE bodies nest without parentheses or BEGIN.
        n => "SELECT 1 WHERE " + string.Concat(Enumerable.Repeat("NOT ", n)) + "1 = 1",
        n => "SELECT " + new string('~', n) + "1",
        n => "SELECT " + string.Concat(Enumerable.Repeat("- ", n)) + "1",
        n => string.Concat(Enumerable.Repeat("IF 1=1 ", n)) + "SELECT 1",
        n => string.Concat(Enumerable.Repeat("WHILE 1=0 ", n)) + "SELECT 1",
    };
    Exception? failure = null;
    var thread = new System.Threading.Thread(() =>
    {
        try
        {
            foreach (var shape in shapes)
            foreach (int n in new[] { 600, 3000 })
            {
                string sql = shape(n);
                int mid = sql.Length / 2;
                void Run(Action action) { try { action(); } catch (Exception e) when (e is FormatException || e is InvalidOperationException) { } }
                Run(() => SqlCompletion.Complete(sql, sql.Length, cat));
                Run(() => SqlAnalysis.Analyze(sql));
                Run(() => SqlAnalysis.UnfilteredChanges(sql, true, true, true));
                Run(() => SqlFormatting.Format(sql));
                Run(() => { SqlNavigation.Blocks(sql); SqlNavigation.Unmatched(sql); });
                Run(() => SqlAssist.Describe(sql, mid, cat, null));
                Run(() => SqlNavigation.FindDefinition(sql, mid));
                Run(() => SqlNavigation.StatementAt(sql, mid));
                Run(() => SqlAssist.FillStatement(sql, sql.Length, cat, null));
                Run(() => SqlAssist.ParameterHintAt(sql, mid, null));
                Run(() => SqlAnalysis.FixAll(sql, cat));
                Run(() => SqlRefactoring.AddSemicolons(sql));
                Run(() => SqlRefactoring.QualifyObjectNames(sql));
                Run(() => SqlRefactoring.UnusedDeclarationItems(sql));
            }
        }
        catch (Exception e) { failure = e; }
    }, 1024 * 1024);
    thread.Start(); thread.Join();
    Check(failure == null, "stress shapes on a 1 MB stack: " + failure);
}

// Fuzz: seeded random edits of real statements; what runs while typing must never throw.
{
    var cat = new[] { new SchemaTable("dbo", "Customers", "CustId", "CustName", "City"), new SchemaTable("dbo", "Orders", "OrderId", "CustId", "Total"), new SchemaTable("dbo", "we]ird name", "col 1", "[x]", "select") };
    var fuzzProcs = new[] { new SchemaProcedure("dbo", "GetOrders", new[] { new SchemaParameter("@id", "int", false, false) }) };
    string[] corpus =
    {
        "SELECT c.CustName, SUM(o.Total) FROM dbo.Customers c JOIN dbo.Orders o ON o.CustId = c.CustId WHERE c.City = N'x' GROUP BY c.CustName;",
        "IF @x = 1 BEGIN UPDATE dbo.Orders SET Total = 0 WHERE OrderId = @id; END ELSE BEGIN DELETE FROM dbo.Orders; END",
        "DECLARE @n int = 1; WITH cte AS (SELECT * FROM Orders) SELECT CASE WHEN Total > 1 THEN 'a' ELSE 'b' END FROM cte;",
        "CREATE PROCEDURE dbo.p @a int AS BEGIN TRY EXEC dbo.GetOrders @id = @a; END TRY BEGIN CATCH THROW; END CATCH\nGO\n--region x\nSELECT 1\n--endregion",
        "INSERT INTO dbo.Orders (OrderId, CustId) SELECT 1, 2 UNION ALL SELECT 3, 4; MERGE dbo.Orders t USING dbo.Customers s ON t.CustId = s.CustId WHEN NOT MATCHED THEN INSERT (CustId) VALUES (s.CustId);",
    };
    const string pieces = "'\"[]()*,.;@#-/\n\r\t N'x' /* */ -- GO BEGIN END CASE WHEN SELECT FROM JOIN ON WHERE ( ) \u00e9\ud83d\ude00 0x 1e5 $ ";
    var random = new Random(1);
    var failures = new List<string>();
    for (int m = 0; m < 300; m++)
    {
        string sql = corpus[random.Next(corpus.Length)];
        for (int k = random.Next(1, 4); k > 0; k--)
        {
            int at = random.Next(sql.Length);
            if (random.Next(2) == 0) sql = sql.Remove(at, Math.Min(random.Next(1, 6), sql.Length - at));
            else { int p = random.Next(pieces.Length); sql = sql.Insert(at, pieces.Substring(p, Math.Min(random.Next(1, 8), pieces.Length - p))); }
        }
        void Try(string what, Action action) { try { action(); } catch (Exception e) { failures.Add(what + " " + e.GetType().Name + ": " + sql); } }
        Try("Analyze", () => SqlAnalysis.Analyze(sql));
        Try("UnfilteredChanges", () => SqlAnalysis.UnfilteredChanges(sql, true, true, true));
        Try("DatabaseSwitchChanges", () => SqlAnalysis.DatabaseSwitchChanges(sql));
        Try("Blocks", () => { SqlNavigation.Blocks(sql); SqlNavigation.Unmatched(sql); SqlNavigation.Regions(sql); });
        for (int pos = 0; pos <= sql.Length; pos += 7)
        {
            int p = pos;
            Try("Complete", () => SqlCompletion.Complete(sql, p, cat, "dbo", false, new[] { "master" }, fuzzProcs, false));
            Try("Describe", () => SqlAssist.Describe(sql, p, cat, fuzzProcs));
            Try("ParameterHintAt", () => SqlAssist.ParameterHintAt(sql, p, fuzzProcs));
            Try("FindDefinition", () => SqlNavigation.FindDefinition(sql, p));
            Try("StatementAt", () => SqlNavigation.StatementAt(sql, p));
            Try("ShortcutBefore", () => SnippetFiles.ShortcutBefore(sql, p));
        }
    }
    Check(failures.Count == 0, "fuzzed typing paths never throw: " + string.Join("\n", failures.Distinct().Take(5)));
}
{
    var tables = new[]
    {
        new SchemaTable("dbo", "Orders", new[] { "Id", "Cust\tId", "Note%" }, new[] { "int", null, "nvarchar(10)" },
            new[] { new SchemaForeignKey(new[] { "Cust\tId" }, "sales", "Cust omers", new[] { "Id\n" }) }, new[] { true, false, false }, false, new[] { "NOT NULL", null, "NULL DEFAULT ('')" }),
        new SchemaTable("sales", "Cust omers", new[] { "Id\n" }, null, null, null, true),
    };
    var text = new System.IO.StringWriter();
    var cachedProcs = new[] { new SchemaProcedure("dbo", "p\tx", new[] { new SchemaParameter("@a", "int", true, false), new SchemaParameter("@b", null, false, true) }, false), new SchemaProcedure("dbo", "f", new SchemaParameter[0], true) };
    SchemaDiskCache.Write(text, tables, cachedProcs);
    var read = SchemaDiskCache.Read(new System.IO.StringReader(text.ToString()));
    var back = read?.Tables;
    Check(back != null && back.Length == 2 && text.ToString().Split('\n').Length == 13, "schema disk cache round-trips");
    var bp = read!.Value.Procedures;
    Check(bp.Length == 2 && bp[0].Name == "p\tx" && !bp[0].IsFunction && bp[1].IsFunction && bp[1].Parameters.Count == 0
        && bp[0].Parameters[0].IsOutput && bp[0].Parameters[0].Type == "int" && bp[0].Parameters[1].HasDefault && bp[0].Parameters[1].Type == null, "schema disk cache keeps procedures");
    Check(SchemaDiskCache.Read(new System.IO.StringReader("querywright-schema 2\nA\t@a\tint\t0\t0")) == null, "parameter before procedure yields null");
    var o = back!.Single(t => t.Name == "Orders"); var c = back!.Single(t => t.Name == "Cust omers");
    Check(o.Columns.SequenceEqual(tables[0].Columns) && o.ColumnTypes!.SequenceEqual(tables[0].ColumnTypes!) && o.Generated!.SequenceEqual(tables[0].Generated!)
        && o.ColumnNotes!.SequenceEqual(tables[0].ColumnNotes!) && !o.IsView && c.IsView && c.Columns.Single() == "Id\n", "schema disk cache keeps columns, types, flags and notes");
    Check(o.ForeignKeys.Single().ReferencedTable == "Cust omers" && o.ForeignKeys.Single().Columns.Single() == "Cust\tId" && o.ForeignKeys.Single().ReferencedColumns.Single() == "Id\n", "schema disk cache keeps foreign keys");
    Check(SchemaDiskCache.Read(new System.IO.StringReader("junk")) == null && SchemaDiskCache.Read(new System.IO.StringReader("querywright-schema 2\nC")) == null
        && SchemaDiskCache.Read(new System.IO.StringReader("querywright-schema 2\nX\ty")) == null && SchemaDiskCache.Read(new System.IO.StringReader("")) == null, "unreadable schema cache yields null");
}
void Same(string actual, string expected, string name)
{
    if (actual != expected) Console.WriteLine("  got: " + actual.Replace("\r", "\\r").Replace("\n", "\\n"));
    Check(actual == expected, name);
}
bool Raises<T>(Action action) where T : Exception
{
    try { action(); }
    catch (T) { return true; }
    return false;
}
{
    Same(SqlTextTools.AddCommas("a\r\nb\r\n\r\nc\r\n"), "a,\r\nb,\r\n\r\nc\r\n", "add commas");
    Same(SqlTextTools.AddCommas("  x,\n  o'k\n  'q'\n  N'r'", quote: true), "  'x',\n  'o''k',\n  'q',\n  N'r'", "add quotes and commas");
    Check(Raises<InvalidOperationException>(() => SqlTextTools.AddCommas(" \r\n ")), "add commas needs lines");
    Same(SqlTextTools.Comment("  a\n    b\n\n  c"), "  --a\n  --  b\n\n  --c", "comment lines");
    Same(SqlTextTools.Uncomment("  --a\n  --  b\n\n  --c"), "  a\n    b\n\n  c", "uncomment lines");
    Same(SqlTextTools.Uncomment("/* x */"), " x ", "uncomment block");
    Check(Raises<InvalidOperationException>(() => SqlTextTools.Uncomment("SELECT 1;")) && Raises<InvalidOperationException>(() => SqlTextTools.Comment("\n")), "comment commands need lines");
    Same(SqlTextTools.RemoveComments("SELECT 1 -- one\r\n-- whole\r\nFROM t /* b */ WHERE a = 1;"), "SELECT 1\r\nFROM t WHERE a = 1;", "remove comments");
    Same(SqlTextTools.RemoveComments("SELECT '--x'/* c */FROM t;"), "SELECT '--x' FROM t;", "remove comment between tokens");
    Check(Raises<InvalidOperationException>(() => SqlTextTools.RemoveComments("SELECT '/* x */';")), "remove comments ignores strings");
    Same(SqlTextTools.Unformat("SELECT a,\r\n       b -- note\r\nFROM t\r\nGO\r\nSELECT 2;"), "SELECT a, b -- note\r\nFROM t\r\nGO\r\nSELECT 2;", "unformat");
    Same(SqlTextTools.Unformat("SELECT 'a  b',\n\t[c  d]\nFROM   t;"), "SELECT 'a  b', [c  d] FROM t;", "unformat keeps literals");

    string dynamic = "EXEC sp_executesql N'SELECT * FROM dbo.T WHERE id = @id AND n = @n', N'@id int, @n nvarchar(10)', @id = 5, @n = N'x';";
    Same(SqlTextTools.ExecuteSqlToPlain(dynamic, 3), "DECLARE @id int = 5;\r\nDECLARE @n nvarchar(10) = N'x';\r\nSELECT * FROM dbo.T WHERE id = @id AND n = @n;", "sp_executesql to SQL");
    Same(SqlTextTools.ExecuteSqlToPlain("SELECT 0;\nEXEC sys.sp_executesql @stmt = N'SELECT ''a'';';\nSELECT 9;", 12), "SELECT 0;\nSELECT 'a';\nSELECT 9;", "sp_executesql without parameters");
    Check(Raises<InvalidOperationException>(() => SqlTextTools.ExecuteSqlToPlain("EXEC sp_executesql @sql;", 0)) &&
        Raises<InvalidOperationException>(() => SqlTextTools.ExecuteSqlToPlain("EXEC dbo.P;", 0)) &&
        Raises<FormatException>(() => SqlTextTools.ExecuteSqlToPlain("EXEC sp_executesql N'x' N'y';", 0)), "sp_executesql conversion rejects what it cannot read");

    string proc = "CREATE PROCEDURE dbo.P @a int, @b int = 2, @c int = 3 AS BEGIN SELECT @a + @b + @c; END";
    Same(SqlTextTools.InlineExec("EXEC dbo.P @a = 1, @b = @x;", 2, proc), "DECLARE @a int = 1;\r\nDECLARE @b int = @x;\r\nDECLARE @c int = 3;\r\nSELECT @a + @b + @c;", "inline EXEC");
    Same(SqlTextTools.InlineExec("EXEC dbo.P 7, @b;", 2, proc, "\n"), "DECLARE @a int = 7;\nDECLARE @c int = 3;\nSELECT @a + @b + @c;", "inline EXEC reuses a same-named variable");
    Check(SqlTextTools.ExecTarget("SELECT 1; EXEC [dbo].[P] 1;", 12) == "dbo.P", "EXEC target");
    Check(Raises<InvalidOperationException>(() => SqlTextTools.InlineExec("EXEC dbo.P 1;", 0, "CREATE PROCEDURE dbo.P @a int AS IF @a = 1 RETURN; SELECT 1;")) &&
        Raises<InvalidOperationException>(() => SqlTextTools.InlineExec("EXEC @r = dbo.P 1;", 0, proc)) &&
        Raises<InvalidOperationException>(() => SqlTextTools.InlineExec("EXEC dbo.P 1, 2, 3, 4;", 0, proc)) &&
        Raises<InvalidOperationException>(() => SqlTextTools.InlineExec("EXEC dbo.P @z = 1;", 0, proc)) &&
        Raises<InvalidOperationException>(() => SqlTextTools.InlineExec("EXEC dbo.P 1;", 0, "SELECT 1;")), "inline EXEC rejects unsafe cases");

    Same(SqlTextTools.InsertToUpdate("INSERT INTO dbo.T (a, [b c]) VALUES (1, 'x');", 0, "\n"),
        "UPDATE dbo.T\nSET a = 1,\n    [b c] = 'x'\nWHERE 1 = 0 /* replace with the key predicate */;", "INSERT to UPDATE");
    Check(Raises<InvalidOperationException>(() => SqlTextTools.InsertToUpdate("INSERT INTO dbo.T VALUES (1);", 0)) &&
        Raises<InvalidOperationException>(() => SqlTextTools.InsertToUpdate("INSERT INTO dbo.T (a) VALUES (1), (2);", 0)) &&
        Raises<InvalidOperationException>(() => SqlTextTools.InsertToUpdate("INSERT INTO dbo.T (a) SELECT 1;", 0)) &&
        Raises<InvalidOperationException>(() => SqlTextTools.InsertToUpdate("SELECT 1;", 0)), "INSERT to UPDATE rejects other shapes");

    string batches = "SELECT 1;\r\nGO\r\nSELECT 2;\r\nSELECT 3;\r\nGO\r\nSELECT 'GO';\r\n";
    string Text(StatementSpan? s) => s == null ? "<null>" : batches.Substring(s.Start, s.Length);
    Same(Text(SqlNavigation.BatchAt(batches, batches.IndexOf("SELECT 3"))), "SELECT 2;\r\nSELECT 3;", "current batch");
    Same(Text(SqlNavigation.BatchAt(batches, 0)), "SELECT 1;", "first batch");
    Same(Text(SqlNavigation.BatchAt(batches, batches.Length)), "SELECT 'GO';", "last batch");
    Same(Text(SqlNavigation.ToCursor(batches, batches.IndexOf("SELECT 2") + 3)), "SELECT 1;\r\nGO\r\nSELECT 2;", "script to cursor");
    Check(SqlNavigation.BatchAt("GO\r\n\r\nGO", 3) == null, "empty batch");

    Check(Rules("SELECT a FROM dbo.T WHERE a != 1;").SequenceEqual(new[] { "SW048" }) && Rules("SELECT a FROM dbo.T WHERE a <> 1;").Length == 0, "!= operator");
    Same(FixOne("SELECT a FROM dbo.T WHERE a ! = 1 AND b != 2;", "SW048"), "SELECT a FROM dbo.T WHERE a <> 1 AND b != 2;", "fix !=");
    Check(Rules("SELECT x = a FROM dbo.T;").SequenceEqual(new[] { "SW049" }) && Rules("SELECT @x = a FROM dbo.T;").All(r => r != "SW049"), "alias = expression");
    Same(FixOne("SELECT x = a + 1, b FROM dbo.T;", "SW049"), "SELECT a + 1 AS x, b FROM dbo.T;", "fix alias = expression");
    Check(Rules("SELECT TOP 5 a FROM dbo.T ORDER BY a;").SequenceEqual(new[] { "SW050" }), "TOP without parentheses");
    Same(FixOne("SELECT TOP 5 PERCENT a FROM dbo.T ORDER BY a;", "SW050"), "SELECT TOP (5) PERCENT a FROM dbo.T ORDER BY a;", "fix TOP");
    Check(Rules("SELECT a FROM dbo.T (NOLOCK);").SequenceEqual(new[] { "SW024", "SW051" }) && Rules("SELECT a FROM dbo.T WITH (NOLOCK);").SequenceEqual(new[] { "SW024" }), "table hint without WITH");
    Same(FixOne("SELECT a FROM dbo.T AS t (NOLOCK);", "SW051"), "SELECT a FROM dbo.T AS t WITH (NOLOCK);", "fix table hint");
    Same(FixOne("SELECT CASE WHEN a = 1 THEN 2 END FROM dbo.T;", "SW004"), "SELECT CASE WHEN a = 1 THEN 2 ELSE NULL END FROM dbo.T;", "fix CASE without ELSE");
    Same(FixOne("SELECT 1 AS 'x y', 'it''s' = 2;", "SW040"), "SELECT 1 AS [x y], 'it''s' = 2;", "fix string alias");
    Check(Rules("SELECT a, COUNT(*) FROM dbo.T WHERE a > 1 GROUP BY ALL a;").SequenceEqual(new[] { "SW052" }), "GROUP BY ALL");
    Check(Rules("SETUSER 'x';").SequenceEqual(new[] { "SW053" }), "SETUSER");
    Check(Rules("DBCC DBREINDEX ('dbo.T');").SequenceEqual(new[] { "SW054" }) && Rules("DBCC CHECKDB;").Length == 0, "deprecated DBCC");
    Check(Rules("DECLARE @v sql_variant; SET @v = 1;").SequenceEqual(new[] { "SW055" }), "SQL_VARIANT");
    Check(Rules("SELECT a INTO #t FROM dbo.T;").SequenceEqual(new[] { "SW056" }), "SELECT INTO");
    Check(Rules("SELECT a FROM dbo.T OPTION (MAXDOP 1);").SequenceEqual(new[] { "SW057" }) && Rules("SELECT a FROM dbo.T OPTION (RECOMPILE);").Length == 0, "query hint");
    Check(Rules("SELECT t.a FROM dbo.T AS t INNER HASH JOIN dbo.U AS u ON u.a = t.a;").SequenceEqual(new[] { "SW058" }), "join hint");
    Check(Rules("SET NOCOUNT OFF;").SequenceEqual(new[] { "SW059" }) && Rules("SET NOCOUNT ON;").Length == 0, "SET NOCOUNT OFF");
    Check(Rules("SET FORCEPLAN ON;").SequenceEqual(new[] { "SW060" }) && Rules("SET FORCEPLAN OFF;").Length == 0, "SET FORCEPLAN");
    Check(Rules("CREATE PROCEDURE dbo.P WITH RECOMPILE AS BEGIN SET NOCOUNT ON; SELECT 1; END").SequenceEqual(new[] { "SW061" }), "WITH RECOMPILE");
    Check(Rules("INSERT INTO dbo.T (a) SELECT a FROM dbo.U ORDER BY a;").SequenceEqual(new[] { "SW062" }) &&
        Rules("INSERT INTO dbo.T (a) SELECT TOP (5) a FROM dbo.U ORDER BY a;").Length == 0, "ORDER BY in INSERT SELECT");
    Check(Rules("CREATE TRIGGER dbo.Tr ON dbo.T AFTER INSERT AS BEGIN PRINT 'x'; SELECT a FROM inserted; DECLARE @n int; SELECT @n = COUNT(*) FROM inserted; INSERT INTO dbo.L (n) SELECT @n; END")
        .SequenceEqual(new[] { "SW063", "SW064" }), "trigger output");
    Check(Rules("SELECT 1; -- TODO: later").SequenceEqual(new[] { "SW065" }) && Rules("SELECT 1; /* FIXME */").SequenceEqual(new[] { "SW065" }) &&
        Rules("SELECT 'TODO'; -- debugger notes").Length == 0, "unfinished-work comment");
    Check(Rules("DECLARE c CURSOR LOCAL FOR SELECT 1; OPEN c; CLOSE c;").SequenceEqual(new[] { "SW066" }) &&
        Rules("DECLARE c CURSOR LOCAL FOR SELECT 1; OPEN c; CLOSE c; DEALLOCATE C;").Length == 0, "cursor never deallocated");
    Check(Rules("SELECT dbo.T.a FROM dbo.T;").SequenceEqual(new[] { "SW067" }) && Rules("SELECT T.a FROM dbo.T;").Length == 0, "schema-qualified column");
    Check(Rules("DROP INDEX T.IX_a;").SequenceEqual(new[] { "SW068" }) && Rules("DROP INDEX IX_a ON dbo.T;").Length == 0, "old DROP INDEX");
    Check(Rules("GRANT ALL ON dbo.T TO r;").SequenceEqual(new[] { "SW069" }) && Rules("GRANT SELECT ON dbo.T TO r;").Length == 0, "GRANT ALL");
    Check(Rules("SELECT a FROM dbo.T WHERE a IN (SELECT a FROM dbo.U);").SequenceEqual(new[] { "SW070" }) && Rules("SELECT a FROM dbo.T WHERE a IN (1, 2);").Length == 0, "IN subquery");
    Check(Rules("DECLARE @s varchar(2) = 'x'; SELECT @s;").SequenceEqual(new[] { "SW071" }) && Rules("DECLARE @s varchar(3) = 'x', @c char(1) = 'y'; SELECT @s, @c;").Length == 0, "short variable-length type");
    Check(Rules("CREATE INDEX IX_a ON dbo.T (a);").SequenceEqual(new[] { "SW072" }) && Rules("CREATE NONCLUSTERED INDEX IX_a ON dbo.T (a);").Length == 0, "index type");
    Check(Rules("SELECT name FROM sysobjects;").SequenceEqual(new[] { "SW073" }) && Rules("SELECT name FROM dbo.sysobjects AS o;").SequenceEqual(new[] { "SW073" }) &&
        Rules("SELECT name FROM sys.objects;").Length == 0 && Rules("SELECT name FROM app.sysobjects;").Length == 0, "SQL Server 2000 system table");
    Check(new WorkbenchSettings().Severity("SW048") == RuleSeverity.Info && new WorkbenchSettings().Severity("SW073") == RuleSeverity.Warning, "new rule severities");
    Check(Enumerable.Range(1, 73).All(i => typeof(WorkbenchSettings).GetProperty("SW" + i.ToString("000")) != null), "every rule has a setting");
    Check(Rules("-- querywright-disable SW065\r\nSELECT 1; -- TODO").Length == 0, "TODO rule can be suppressed");

    string region = "select 1\r\n-- querywright:format off\r\nselect   2 ,  3\r\n-- querywright:format on\r\nselect 4";
    string regionFormatted = SqlFormatting.Format(region);
    Check(regionFormatted.StartsWith("SELECT 1") && regionFormatted.Contains("-- querywright:format off\r\nselect   2 ,  3\r\n-- querywright:format on\r\n") && regionFormatted.TrimEnd().EndsWith("SELECT 4;"), "format off region kept verbatim");
    Check(SqlFormatting.Format("select 1\r\n-- querywright:format off\r\nselect   2 ,  3").EndsWith("-- querywright:format off\r\nselect   2 ,  3"), "format off without on runs to the end");
    Check(SqlFormatting.Format(regionFormatted) == regionFormatted, "format region is stable");
    Check(!SqlFormatting.Format("select 1\r\n-- querywright:format on\r\nselect   2 ,  3").Contains("select   2"), "format on alone changes nothing");
    Check(SqlFormatting.Format("select 1\r\n-- ulysisql:format off\r\nselect   2 ,  3").EndsWith("-- ulysisql:format off\r\nselect   2 ,  3"), "format off under the UlysiSQL name");
}
CompletionResult Sug(string text, CompletionOptions? o = null)
{
    int at = text.IndexOf('|');
    return SqlCompletion.Complete(text.Remove(at, 1), at, fkCatalog, o ?? new CompletionOptions());
}
Check(SqlCompletion.Match("OrderId", "oi") == MatchKind.Camel && SqlCompletion.Match("order_id", "OI") == MatchKind.Camel &&
    SqlCompletion.Match("OrderId", "ordi") == MatchKind.Camel && SqlCompletion.Match("CustomerOrders", "CusOrd") == MatchKind.Camel, "word-start match");
Check(SqlCompletion.Match("HTTPServer", "hs") == MatchKind.None && SqlCompletion.Match("HTTPServer", "hts") == MatchKind.None &&
    SqlCompletion.Match("SalesOrderId", "oi") == MatchKind.Camel && SqlCompletion.Match("OrderOrigin", "oo") == MatchKind.Camel, "upper-case run is one word; match may start at a later word");
Check(SqlCompletion.Match("OrderId", "ord") == MatchKind.Prefix && SqlCompletion.Match("OrderId", "orderid") == MatchKind.Exact &&
    SqlCompletion.Match("OrderId", "") == MatchKind.Prefix, "prefix and exact match");
Check(SqlCompletion.Match("SalesOrder", "ord") == MatchKind.CamelSubstring && SqlCompletion.Match("Border", "ord") == MatchKind.Substring &&
    SqlCompletion.Match("OrderId", "xz") == MatchKind.None, "word-start substring, substring and no match");
Check(SqlCompletion.Match("OrderId", "oi", camelHumps: false) == MatchKind.None && SqlCompletion.Match("Border", "ord", anywhere: false) == MatchKind.None, "match kinds can be turned off");
Check(MatchKind.Exact > MatchKind.Prefix && MatchKind.Prefix > MatchKind.CamelSubstring &&
    MatchKind.CamelSubstring > MatchKind.Camel && MatchKind.Camel > MatchKind.Substring, "match kinds rank");
Check(SqlCompletion.Highlights("OrderId", "oi").SequenceEqual(new[] { 0, 5 }) && SqlCompletion.Highlights("SalesOrder", "ord").SequenceEqual(new[] { 5, 6, 7 }) &&
    SqlCompletion.Highlights("OrderId", "or").SequenceEqual(new[] { 0, 1 }) && SqlCompletion.Highlights("OrderId", "xz").Count == 0 &&
    SqlCompletion.Highlights("OrderOrigin", "ori").SequenceEqual(new[] { 5, 6, 7 }) && SqlCompletion.Highlights("Border", "ord").SequenceEqual(new[] { 1, 2, 3 }), "match highlights");
Check(SqlCompletion.Alias("OrderDetails") == "od" && SqlCompletion.Alias("People") == "p" && SqlCompletion.Alias("order_details") == "od", "alias from word initials");
Check(SqlCompletion.Alias("tblOrderDetails", new CompletionOptions { IgnoredPrefixes = new[] { "tbl" } }) == "od" &&
    SqlCompletion.Alias("tbl", new CompletionOptions { IgnoredPrefixes = new[] { "tbl" } }) == "t", "alias ignores a prefix but not the whole name");
Check(SqlCompletion.Alias("Orders", null, new[] { "o" }) == "o2" && SqlCompletion.Alias("Orders", null, new[] { "o", "o2" }) == "o3", "alias avoids taken names");
Check(SqlCompletion.Alias("InventoryStock") == "ist" && SqlCompletion.Alias("GlobalOrders") == "gor" && SqlCompletion.Alias("123") == "a", "reserved alias takes one more letter; fallback");
Check(SqlCompletion.Alias("HTTPServer") == "hs" && SqlCompletion.Alias("Order2Line") == "ol" && SqlCompletion.Alias("order2line") == "o", "alias word starts");
Check(SqlCompletion.Alias("tblOrderLines", new CompletionOptions { IgnoredPrefixes = new[] { "t", "tbl" } }) == "ol", "longest ignored prefix wins");
Check(SqlCompletion.Alias("I_S") == "[is]" && SqlCompletion.Alias("People", new CompletionOptions { Aliases = new Dictionary<string, string> { ["People"] = "my people" } }) == "[my people]", "alias bracketed when needed");
Check(SqlCompletion.Alias("Order_Details", new CompletionOptions { UppercaseAliases = true }) == "OD", "uppercase alias");
Check(SqlCompletion.Alias("People", new CompletionOptions { Aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["people"] = "per" } }) == "per", "user-defined alias");
var aliasing = new CompletionOptions { AliasTables = true };
Check(Sug("SELECT * FROM Peo|", aliasing).Items.Single(i => i.Name == "People").InsertText == "dbo.People p", "table inserted with alias");
Check(Sug("SELECT * FROM Peo|").Items.Single(i => i.Name == "People").InsertText == "dbo.People", "no alias by default");
Check(Sug("SELECT * FROM Peo|", new CompletionOptions { AliasTables = true, AliasWithAs = true }).Items.Single(i => i.Name == "People").InsertText == "dbo.People AS p", "alias with AS");
Check(Sug("SELECT * FROM Peo| x", aliasing).Items.Single(i => i.Name == "People").InsertText == "dbo.People", "existing alias kept");
Check(Sug("SELECT * FROM dbo.People p JOIN Peo|", aliasing).Items.Single(i => i.Name == "People").InsertText == "dbo.People p2", "second copy gets a new alias");
Check(Sug("SELECT * FROM dbo.People p WHERE EXISTS (SELECT 1 FROM Lin|", aliasing).Items.Single(i => i.Name == "Lines").InsertText == "sales.Lines l", "alias in subquery");
var unqualified = new CompletionOptions { QualifyObjects = false };
Check(Sug("SELECT * FROM Peo|", unqualified).Items.Single(i => i.Name == "People").InsertText == "People" &&
    Sug("SELECT * FROM Lin|", unqualified).Items.Single(i => i.Name == "Lines").InsertText == "sales.Lines", "default schema dropped, other schemas kept");
var joinTable = Sug("SELECT * FROM dbo.People p JOIN |", aliasing).Items[0];
Check(joinTable.Name == "Orders o ON o.PersonId = p.Id" && joinTable.InsertText == "dbo.Orders o ON o.PersonId = p.Id" && joinTable.Promoted, "JOIN leads with FK table and condition");
Check(Sug("SELECT * FROM dbo.People p JOIN |").Items[0].InsertText == "dbo.Orders ON Orders.PersonId = p.Id", "FK join table without alias");
Check(Sug("SELECT * FROM dbo.Orders o JOIN |", aliasing).Items.Select(i => i.InsertText).Take(2).OrderBy(n => n)
    .SequenceEqual(new[] { "dbo.People p ON p.Id = o.PersonId", "sales.Lines l ON l.OrderId = o.OrderId" }), "FK join tables in both directions");
Check(Sug("SELECT * FROM dbo.People p JOIN |", new CompletionOptions { AliasTables = true, SwapJoinOrder = true }).Items[0].InsertText == "dbo.Orders o ON p.Id = o.PersonId", "swapped join order");
Check(Sug("SELECT * FROM dbo.People p JOIN Ord|", aliasing).Items[0].Promoted && Sug("SELECT * FROM dbo.People p JOIN Ord|", aliasing).Items[1].InsertText == "dbo.Orders o", "typed join table keeps the FK item first");
Check(Sug("SELECT * FROM dbo.People p JOIN |", new CompletionOptions { JoinTables = false }).Items.All(i => !i.Promoted), "join tables can be turned off");
Check(Sug("SELECT * FROM dbo.People JOIN |").Items.All(i => !i.InsertText.StartsWith("dbo.People ON")), "self join needs an alias");
var onItems = Sug("SELECT * FROM sales.Lines l JOIN sales.Notes n ON |");
Check(onItems.Items[0].Name == "n.OrderId = l.OrderId AND n.LineNumber = l.LineNumber" && onItems.Items[0].Description.StartsWith("foreign key") &&
    onItems.Items.Single(i => i.Name == "n.OrderId = l.OrderId").Description.StartsWith("matching column name") &&
    onItems.Items.Any(i => i.Name == "n.LineNumber = l.LineNumber"), "ON offers FK then matching names");
Check(Sug("SELECT * FROM sales.Lines l JOIN sales.Notes n ON |", new CompletionOptions { JoinNameMatches = false }).Items.Count(i => i.Promoted) == 1, "name matches can be turned off");
Check(Sug("SELECT * FROM sales.Lines l JOIN sales.Notes n ON |", new CompletionOptions { SwapJoinOrder = true }).Items[0].Name == "l.OrderId = n.OrderId AND l.LineNumber = n.LineNumber", "swapped ON condition");
var afterTable = Sug("SELECT * FROM dbo.People |");
Check(afterTable.Soft && afterTable.Items.All(i => i.Description == "keyword") && Names(afterTable).Contains("WHERE") && Names(afterTable).Contains("INNER JOIN"), "after a table: keywords, alias may be typed");
var afterAlias = Sug("SELECT * FROM dbo.People p |");
Check(!afterAlias.Soft && Names(afterAlias).Contains("LEFT JOIN") && !Names(afterAlias).Contains("People"), "after an alias: keywords only");
var typedKeyword = Sug("SELECT * FROM dbo.People p WH|");
Check(typedKeyword.Items[0].Name == "WHERE" && typedKeyword.Start == 27 && typedKeyword.Length == 2, "typed follow keyword");
Check(Names(Sug("SELECT * FROM dbo.People p JOIN dbo.Orders o |")).Contains("ON") && Sug("SELECT * FROM dbo.People p JOIN dbo.Orders |").Soft, "after a joined table");
var newAlias = Sug("SELECT * FROM dbo.People AS |");
Check(newAlias.Items.Count == 0 && newAlias.Soft && Sug("SELECT Id AS | FROM dbo.People").Items.Count == 0, "nothing listed where an alias is defined");
Check(!Sug("SELECT CAST(Id AS | FROM dbo.People").Soft, "CAST AS is not an alias");
Check(Names(Sug("SELECT * FROM dbo.People p WHERE |")).Contains("Name") && !Sug("SELECT * FROM dbo.People p WHERE |").Soft, "WHERE still lists columns");
Check(Names(Sug("SELECT * FROM dbo.People p JOIN dbo.Orders o ON o.PersonId |")).Contains("AND") , "after a column in ON: general list");
var plainList = Sug("SELECT * FROM dbo.People |", new CompletionOptions { ContextKeywords = false });
Check(!plainList.Soft && plainList.Items.Any(i => i.Description != "keyword"), "context keywords can be turned off");
var unfiltered = Sug("SELECT * FROM Peo|", new CompletionOptions { Filter = false });
Check(Names(unfiltered).Contains("Orders") && unfiltered.Start == 14 && unfiltered.Length == 3, "unfiltered list for a filtering host");
Check(Names(Sug("SELECT p.| FROM dbo.People p")).Where(n => n != "*").SequenceEqual(new[] { "Id", "Name", "Born" }), "columns in definition order");
Check(Names(Sug("SELECT p.| FROM dbo.People p", new CompletionOptions { ColumnsAlphabetical = true })).Where(n => n != "*").SequenceEqual(new[] { "Born", "Id", "Name" }), "columns alphabetical");
Check(Names(Sug("SELECT o.pi| FROM dbo.Orders o")).Contains("PersonId") && Names(Sug("SELECT o.otal| FROM dbo.Orders o")).Contains("Total"), "word-start and substring matches listed");
var ranked = Names(Sug("SELECT o.t| FROM dbo.Orders o"));
Check(ranked[0].StartsWith("Total") && Array.IndexOf(ranked, "Total") < Array.IndexOf(ranked, "PersonId") || !ranked.Contains("PersonId"), "prefix matches before others");
// AI assistant: JSON, prompt building, provider wire formats, MCP protocol. No network or SSMS involved.
var jsonValue = MiniJson.Parse("{\"a\":[1,2.5,\"x\\n\\t\",true,null],\"b\":{\"c\":\"d\"}}");
Check(MiniJson.Text(MiniJson.Get(jsonValue, "b"), "c") == "d" && MiniJson.Get(jsonValue, "a") is List<object?> jsonList && jsonList.Count == 5 && (double)jsonList[1]! == 2.5 && (string)jsonList[2]! == "x\n\t", "json parse");
Check(MiniJson.Write(MiniJson.Parse(MiniJson.Write(jsonValue))) == MiniJson.Write(jsonValue) && MiniJson.Write(new Dictionary<string, object?> { ["q"] = "a\"b\\c\n" }) == "{\"q\":\"a\\\"b\\\\c\\n\"}", "json round trip and escaping");
Throws<FormatException>(() => MiniJson.Parse("{\"a\":"), "json syntax error");
Check(MiniJson.Write(new Dictionary<string, object?> { ["g"] = Guid.Empty, ["d"] = DayOfWeek.Monday, ["n"] = 5m, ["f"] = float.NaN, ["s"] = new Dictionary<string, string> { ["k"] = "v" } })
    == "{\"g\":\"00000000-0000-0000-0000-000000000000\",\"d\":\"Monday\",\"n\":5,\"f\":null,\"s\":{\"k\":\"v\"}}", "json quotes GUIDs and enums and writes other dictionaries as objects");
Throws<InvalidOperationException>(() => AiAssist.Messages(AiTask.Generate, " ", "SELECT 1", null), "generate needs a prompt");
Throws<InvalidOperationException>(() => AiAssist.Messages(AiTask.Explain, null, "  ", null), "explain needs SQL");
var aiMessages = AiAssist.Messages(AiTask.Fix, null, "SELEC 1", "CREATE TABLE dbo.People (Id int)", "Line 1, Msg 102: Incorrect syntax");
Check(aiMessages[0].Role == "system" && aiMessages.Last().Role == "user" && aiMessages.Last().Content.Contains("SELEC 1") && aiMessages.Last().Content.Contains("Msg 102") && aiMessages.Any(m => m.Content.Contains("dbo.People")), "fix prompt carries script, errors and schema");
Check(AiAssist.Endpoint("https://api.deepseek.com/", false) == "https://api.deepseek.com/chat/completions" && AiAssist.Endpoint("https://api.openai.com/v1", false) == "https://api.openai.com/v1/chat/completions"
    && AiAssist.Endpoint("https://api.anthropic.com", true) == "https://api.anthropic.com/v1/messages" && AiAssist.Endpoint("http://localhost:11434/v1/chat/completions", false) == "http://localhost:11434/v1/chat/completions", "provider endpoints");
Throws<InvalidOperationException>(() => AiAssist.Endpoint("api.deepseek.com", false), "endpoint needs a scheme");
var openAiBody = MiniJson.Parse(AiAssist.RequestBody("deepseek-chat", aiMessages, false));
Check(MiniJson.Text(openAiBody, "model") == "deepseek-chat" && MiniJson.Get(openAiBody, "messages") is List<object?> sentMessages && sentMessages.Count == aiMessages.Count && MiniJson.Text(sentMessages[0], "role") == "system", "OpenAI-compatible request");
var anthropicBody = MiniJson.Parse(AiAssist.RequestBody("some-model", aiMessages, true));
Check(MiniJson.Get(anthropicBody, "system") is string && MiniJson.Get(anthropicBody, "messages") is List<object?> anthropicMessages && anthropicMessages.All(m => MiniJson.Text(m, "role") != "system") && MiniJson.Get(anthropicBody, "max_tokens") is double, "Anthropic request keeps system apart");
Check(AiAssist.ReplyText("{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"hi\"}}]}", false) == "hi" && AiAssist.ReplyText("{\"content\":[{\"type\":\"text\",\"text\":\"a\"},{\"type\":\"text\",\"text\":\"b\"}]}", true) == "ab", "provider replies");
Throws<InvalidOperationException>(() => AiAssist.ReplyText("{\"error\":{\"message\":\"bad key\"}}", false), "provider error surfaces");
Throws<InvalidOperationException>(() => AiAssist.ReplyText("<html>", false), "non-JSON reply");
Check(AiAssist.ExtractSql("Here:\n```sql\nSELECT 1;\n```\nand\n```text\nnote\n```") == "SELECT 1;" && AiAssist.ExtractSql("```\nSELECT 2;\n```") == "SELECT 2;" && AiAssist.ExtractSql("no code") == null, "SQL taken from code blocks");
Check(AiAssist.CompletionText("```sql\nWHERE Id = 1<CURSOR>\n```") == "WHERE Id = 1" && AiAssist.CompletionWindow("SELECT  FROM t", 7) == "SELECT <CURSOR> FROM t", "completion window and text");
var aiBatches = AiAssist.Batches("SELECT 1;\nGO\n\nSELECT 2;\ngo 3 -- again\nSELECT 3;");
Check(aiBatches.Count == 3 && aiBatches[0].Line == 1 && aiBatches[1].Text.Contains("SELECT 2") && aiBatches[2].Text.Contains("SELECT 3") && aiBatches[1].Line == 2 && aiBatches[2].Line == 5 && AiAssist.Batches(" \nGO\n").Count == 0, "GO batches with line numbers");
var aiCatalog = Enumerable.Range(0, 50).Select(i => new SchemaTable("dbo", "T" + i, "Id", "Value")).Append(new SchemaTable("sales", "Invoices", "InvoiceId", "Total")).ToArray();
string aiSchema = AiAssist.SchemaContext(aiCatalog, "total of invoices per month", 5);
Check(aiSchema.Contains("sales.Invoices") && aiSchema.Contains("InvoiceId") && aiSchema.IndexOf("dbo.T40") > aiSchema.IndexOf("Other objects") && aiSchema.IndexOf("Other objects") > 0
    && AiAssist.SchemaContext(aiCatalog, "x", 30, 200).IndexOf("Other objects") <= 201, "schema context details mentioned tables, names the rest and is capped");
var mcpServer = new McpProtocol("test", "1.0", new[]
{
    new McpTool("echo", "Echo", a => "got " + McpProtocol.Argument(a, "text") + " x" + McpProtocol.Argument(a, "times", 1), true, ("text", "Text"), ("times#?", "Count")),
    new McpTool("refuse", "Fails", a => throw new InvalidOperationException("declined"), false),
});
object? Mcp(string request) => MiniJson.Parse(mcpServer.Handle(request)!);
var mcpStart = Mcp("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2025-03-26\"}}");
Check(MiniJson.Text(MiniJson.Get(mcpStart, "result"), "protocolVersion") == "2025-03-26" && MiniJson.Text(MiniJson.Get(MiniJson.Get(mcpStart, "result"), "serverInfo"), "name") == "test" && (double)MiniJson.Get(mcpStart, "id")! == 1, "MCP initialize");
Check(mcpServer.Handle("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}") == null, "MCP notification gets no reply");
var mcpList = (List<object?>)MiniJson.Get(MiniJson.Get(Mcp("{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/list\"}"), "result"), "tools")!;
var echoSchema = MiniJson.Get(mcpList[0], "inputSchema");
Check(mcpList.Count == 2 && MiniJson.Text(mcpList[0], "name") == "echo" && MiniJson.Text(MiniJson.Get(MiniJson.Get(echoSchema, "properties"), "times"), "type") == "integer"
    && MiniJson.Write(MiniJson.Get(echoSchema, "required")) == "[\"text\"]" && MiniJson.Get(MiniJson.Get(mcpList[1], "annotations"), "readOnlyHint") is false, "MCP tool list and schemas");
object? McpCall(string name, string arguments) => MiniJson.Get(Mcp("{\"jsonrpc\":\"2.0\",\"id\":\"c\",\"method\":\"tools/call\",\"params\":{\"name\":\"" + name + "\",\"arguments\":" + arguments + "}}"), "result");
string? McpText(object? result) => MiniJson.Text(((List<object?>)MiniJson.Get(result, "content")!)[0], "text");
Check(McpText(McpCall("echo", "{\"text\":\"hi\",\"times\":3}")) == "got hi x3" && MiniJson.Get(McpCall("echo", "{\"text\":\"hi\"}"), "isError") is false, "MCP tool call");
Check(McpText(McpCall("echo", "{}")) == "Missing argument: text" && MiniJson.Get(McpCall("echo", "{}"), "isError") is true, "MCP missing argument is a tool error");
Check(McpText(McpCall("refuse", "{}")) == "declined" && MiniJson.Get(McpCall("refuse", "{}"), "isError") is true, "MCP handler refusal is a tool error");
Check((double)MiniJson.Get(MiniJson.Get(Mcp("{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"tools/call\",\"params\":{\"name\":\"nope\"}}"), "error"), "code")! == -32602
    && (double)MiniJson.Get(MiniJson.Get(Mcp("{\"jsonrpc\":\"2.0\",\"id\":4,\"method\":\"resources/list\"}"), "error"), "code")! == -32601
    && (double)MiniJson.Get(MiniJson.Get(Mcp("not json"), "error"), "code")! == -32700, "MCP protocol errors");
Check(McpText(McpCall("echo", "{\"text\":\"hi\",\"times\":99999999999}")) == "got hi x1" && (double)MiniJson.Get(MiniJson.Get(Mcp("[]"), "error"), "code")! == -32600
    && MiniJson.Text(MiniJson.Get(Mcp("{\"jsonrpc\":\"2.0\",\"id\":5,\"method\":\"tools/call\",\"params\":{}}"), "error"), "message") == "Missing tool name", "MCP out-of-range integers, empty batches and missing tool names");
var aiMarkdown = AiMarkdown.Parse("# Answer\n\nUse **bold**, *italic* and `code`.\n\n- One\n2. Two\n\n```sql\nSELECT N'--not comment'; -- comment\n```\n```text\nplain\n```\n\n| A | B |\n| --- | :---: |\n| 1 | 2 |");
Check(aiMarkdown.Count == 7 && aiMarkdown[0].Kind == AiBlockKind.Heading && aiMarkdown[0].Level == 1 && aiMarkdown[2].Prefix == "•" && aiMarkdown[3].Prefix == "2.", "aiMarkdown block types and list markers");
Check(aiMarkdown[1].Inlines.Any(i => i.Bold && i.Text == "bold") && aiMarkdown[1].Inlines.Any(i => i.Italic && i.Text == "italic") && aiMarkdown[1].Inlines.Any(i => i.Code && i.Text == "code"), "aiMarkdown emphasis and inline code");
Check(aiMarkdown[4].IsSql && aiMarkdown[4].Closed && !aiMarkdown[5].IsSql && aiMarkdown[6].Kind == AiBlockKind.Table, "aiMarkdown SQL actions limited to SQL fences and table fallback");
var unfinishedCode = AiMarkdown.Parse("~~~tsql\nSELECT 1;").Single();
Check(unfinishedCode.IsSql && !unfinishedCode.Closed && unfinishedCode.Text == "SELECT 1;" && AiMarkdown.Parse("````sql\n```\n````").Single().Text == "```", "unfinished and longer fences preserve code");
string untrustedMarkdown = "<img src=x> [open](file:///secret) ![remote](https://example.com/a.png)";
Check(string.Concat(AiMarkdown.Parse(untrustedMarkdown).Single().Inlines.Select(i => i.Text)) == untrustedMarkdown, "aiMarkdown HTML, links and images remain literal text");
var sqlColors = AiMarkdown.SqlTokens(aiMarkdown[4].Text);
Check(string.Concat(sqlColors.Select(t => t.Text)) == aiMarkdown[4].Text && sqlColors.Any(t => t.Kind == "keyword" && t.Text == "SELECT") && sqlColors.Any(t => t.Kind == "string" && t.Text.Contains("--not comment")) && sqlColors.Any(t => t.Kind == "comment" && t.Text == "-- comment"), "ScriptDom SQL coloring preserves text and separates comments and literals");
Check(string.Concat(AiMarkdown.SqlTokens("SELECT 'unfinished").Select(t => t.Text)) == "SELECT 'unfinished", "SQL coloring preserves incomplete SQL");
Check(MiniJson.Get(MiniJson.Parse(AiAssist.RequestBody("m", aiMessages, false, stream: true)), "stream") is true && MiniJson.Get(MiniJson.Parse(AiAssist.RequestBody("m", aiMessages, true, stream: true)), "stream") is true, "stream requests for both API formats");
var openAiStream = new AiStream(false);
string StreamEvent(AiStream parser, params string[] lines) => string.Concat(lines.Append("").Select(parser.Line));
Check(StreamEvent(openAiStream, ": keepalive", "event: message", "data: {\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\"}}]}") == "" && !openAiStream.Done, "SSE ignores comments and role-only events");
Check(StreamEvent(openAiStream, """data: {"choices":[{"index":0,"delta":{"content":"SELECT "}}]}""") == "SELECT ", "OpenAI text delta");
Check(StreamEvent(openAiStream, "data: {\"choices\":[", "data: {\"index\":0,\"delta\":{\"content\":\"1;\\n\"}}]}") == "1;\n", "SSE joins multiple data lines and preserves text whitespace");
Check(StreamEvent(openAiStream, "data: {\"choices\":[{\"index\":1,\"delta\":{\"content\":\"ignored\"}}]}") == "" && StreamEvent(openAiStream, "data: [DONE]") == "" && openAiStream.Done && openAiStream.Finish() == "", "SSE completion and first choice only");
var claudeStream = new AiStream(true);
Check(StreamEvent(claudeStream, "event: ping", "data: {\"type\":\"ping\"}") == "" && StreamEvent(claudeStream, "event: content_block_delta", "data: {\"type\":\"content_block_delta\",\"delta\":{\"type\":\"text_delta\",\"text\":\"hello\"}}") == "hello", "Anthropic ping and text deltas");
Check(StreamEvent(claudeStream, "data: {\"type\":\"content_block_delta\",\"delta\":{\"type\":\"thinking_delta\",\"thinking\":\"hidden\"}}") == "" && StreamEvent(claudeStream, "event: message_stop", "data: {\"type\":\"message_stop\"}") == "" && claudeStream.Done, "Anthropic only displays text and completes on message_stop");
var eofStream = new AiStream(false);
eofStream.Line("data: [DONE]"); Check(eofStream.Finish() == "" && eofStream.Done, "SSE dispatches final event without trailing blank line");
Throws<InvalidOperationException>(() => new AiStream(false).Finish(), "truncated SSE is not success");
Throws<InvalidOperationException>(() => StreamEvent(new AiStream(false), "data: {broken}"), "malformed SSE fails clearly");
Throws<InvalidOperationException>(() => StreamEvent(new AiStream(true), "event: error", "data: {\"error\":{\"message\":\"credential echo\"}}"), "SSE provider error");
Check(AiStream.Rejected(400, "{\"error\":{\"message\":\"stream is not supported\"}}") && !AiStream.Rejected(401, "{\"error\":\"stream unsupported\"}") && !AiStream.Rejected(400, "{\"error\":\"invalid model\"}"), "fallback limited to explicit streaming rejection");
Check(AiAssist.SqlChanges("SELECT 'DELETE DROP'; -- UPDATE\nSELECT [CREATE] FROM dbo.t;").Count == 0, "AI run warning ignores literals, comments and quoted identifiers");
Check(AiAssist.SqlChanges("IF 1 = 1 DELETE FROM dbo.t; EXEC dbo.p; CREATE TABLE dbo.x (Id int);").SequenceEqual(new[] { "DELETE", "EXEC", "CREATE" }), "AI run warning includes nested writes, execution and DDL");
Check(AiAssist.SqlChanges("SELECT Id INTO dbo.CopyOfT FROM dbo.t;").Contains("INTO") && AiAssist.SqlChanges("SELECT FROM;").Any(c => c.Contains("effects unknown")) && AiAssist.SqlChanges("SELEC 1;").Contains("EXEC"), "AI run warning flags SELECT INTO, implicit execution and unparsed SQL conservatively");
AiAssist.SchemaContext(aiCatalog, "invoices", out int schemaSent, 5);
AiAssist.SchemaContext(aiCatalog, "invoices", out int cappedSchemaSent, 5, 1);
Check(schemaSent == 1 && cappedSchemaSent == 0, "schema preview counts definitions actually sent, including character cap");
var allAiWrites = AiAssist.SqlChanges("INSERT dbo.t (Id) VALUES (1); UPDATE dbo.t SET Id=2; DELETE FROM dbo.t; MERGE dbo.t AS t USING (SELECT 1 AS Id) AS s ON t.Id=s.Id WHEN MATCHED THEN UPDATE SET Id=s.Id; TRUNCATE TABLE dbo.t; DROP TABLE dbo.t; ALTER TABLE dbo.t ADD Value int; CREATE TABLE dbo.x (Id int); EXEC dbo.p;");
Check(new[] { "INSERT", "UPDATE", "DELETE", "MERGE", "TRUNCATE", "DROP", "ALTER", "CREATE", "EXEC" }.All(allAiWrites.Contains), "AI run warning covers every requested write operation");
var serverAiWrites = AiAssist.SqlChanges("BACKUP DATABASE d TO DISK = 'x'; RESTORE DATABASE d FROM DISK = 'x'; DBCC SHRINKDATABASE (d); KILL 53; RECONFIGURE; DISABLE TRIGGER trg ON dbo.t; SHUTDOWN;");
Check(new[] { "BACKUP", "RESTORE", "DBCC", "KILL", "RECONFIGURE", "DISABLE TRIGGER", "SHUTDOWN" }.All(serverAiWrites.Contains), "AI run warning covers server and trigger operations");
Check(AiAssist.SqlChanges("SELECT 1 WHERE " + string.Concat(Enumerable.Repeat("NOT ", 3000)) + "1 = 1; DELETE FROM dbo.t;").Contains("DELETE"), "AI run warning still scans SQL too deep to parse");
Console.WriteLine($"PASS: {checks} total checks including AI markdown, streaming and SQL safety. SSMS integration not tested.");

// Provider metadata and reasoning remain separate from answer text; fixtures use fictional model IDs.
Check(AiProvider.Models("""{"data":[{"id":"fixture-fast"},{"id":"fixture-think"},{"id":"fixture-fast"},{"id":" "},{"id":3}]}""")
    .SequenceEqual(new[] { "fixture-fast", "fixture-think" }), "model list preserves provider IDs and removes duplicates and invalid IDs");
Check(AiProvider.ModelsNext("""{"data":[],"has_more":true,"last_id":"fixture-next"}""") == "fixture-next"
    && AiProvider.ModelsNext("""{"data":[],"has_more":false,"last_id":"fixture-last"}""") == null, "model-list pagination follows has_more and last_id");
Throws<InvalidOperationException>(() => AiProvider.Models("""{"error":{"message":"not supported"}}"""), "model-list failure keeps manual entry path");
Throws<InvalidOperationException>(() => AiProvider.ModelsNext("""{"has_more":true}"""), "model-list pagination refuses missing cursor");

var reasonOffBody = MiniJson.Parse(AiAssist.RequestBody("fixture-fast", aiMessages, false));
Check(MiniJson.Get(reasonOffBody, "reasoning_effort") == null && MiniJson.Get(reasonOffBody, "thinking") == null, "reasoning Off sends no reasoning fields");
foreach (var level in new[] { AiReasoningLevel.Low, AiReasoningLevel.Medium, AiReasoningLevel.High })
{
    var effortBody = MiniJson.Parse(AiAssist.RequestBody("fixture-think", aiMessages, false, reasoning: level));
    Check(MiniJson.Text(effortBody, "reasoning_effort") == level.ToString().ToLowerInvariant() && MiniJson.Get(effortBody, "temperature") == null,
        "OpenAI-compatible effort mapping and omitted temperature " + level);
    var budgetBody = MiniJson.Parse(AiAssist.RequestBody("fixture-think", aiMessages, true, reasoning: level));
    double budget = (double)MiniJson.Get(MiniJson.Get(budgetBody, "thinking"), "budget_tokens")!;
    Check(MiniJson.Text(MiniJson.Get(budgetBody, "thinking"), "type") == "enabled" && budget >= 1024 && (double)MiniJson.Get(budgetBody, "max_tokens")! > budget
        && MiniJson.Get(budgetBody, "temperature") == null, "Anthropic manual budget fits output cap " + level);
}
var documentedOpenAiBody = MiniJson.Parse(AiAssist.RequestBody("fixture-think", aiMessages, false, stream: true, reasoning: AiReasoningLevel.High, baseUrl: "https://api.openai.com/v1"));
Check(MiniJson.Get(documentedOpenAiBody, "max_tokens") == null && (double)MiniJson.Get(documentedOpenAiBody, "max_completion_tokens")! == 4096
    && MiniJson.Get(MiniJson.Get(documentedOpenAiBody, "stream_options"), "include_usage") is true, "official OpenAI token cap and streaming usage options");
var deepSeekThinkingBody = MiniJson.Parse(AiAssist.RequestBody("fixture-think", aiMessages, false, reasoning: AiReasoningLevel.Medium, baseUrl: "https://api.deepseek.com/v1"));
Check(MiniJson.Text(MiniJson.Get(deepSeekThinkingBody, "thinking"), "type") == "enabled" && MiniJson.Text(deepSeekThinkingBody, "reasoning_effort") == "high"
    && MiniJson.Get(deepSeekThinkingBody, "max_tokens") is double, "documented DeepSeek thinking switch and medium-to-high effort mapping");
Check(MiniJson.Get(MiniJson.Parse(AiAssist.RequestBody("fixture-fast", aiMessages, true, baseUrl: "https://api.anthropic.com")), "temperature") == null
    && MiniJson.Get(MiniJson.Parse(AiAssist.RequestBody("fixture-fast", aiMessages, false, temperature: null)), "temperature") == null,
    "official Anthropic and explicit null omit temperature");
var extraBody = MiniJson.Parse(AiAssist.RequestBody("fixture-think", aiMessages, false, reasoning: AiReasoningLevel.Low,
    extraRequestJson: """{"reasoning_effort":"high","temperature":null,"max_tokens":5000,"vendor_option":true}"""));
Check(MiniJson.Text(extraBody, "reasoning_effort") == "high" && MiniJson.Get(extraBody, "temperature") == null
    && (double)MiniJson.Get(extraBody, "max_tokens")! == 5000 && MiniJson.Get(extraBody, "vendor_option") is true, "custom options override mapped fields safely");
string extraThinking = """{"thinking":{"type":"enabled","budget_tokens":1024},"reasoning_effort":"high","vendor":{"enable_thinking":true},"output_config":{"effort":"high","format":"json"},"max_tokens":5000}""";
string extraOff = AiAssist.RequestBody("fixture-fast", aiMessages, false, extraRequestJson: extraThinking);
Check(!extraOff.Contains("thinking") && !extraOff.Contains("reasoning") && !extraOff.Contains("effort") && extraOff.Contains("\"format\":\"json\"")
    && (double)MiniJson.Get(MiniJson.Parse(extraOff), "max_tokens")! == 5000 && !AiProvider.WithoutReasoningJson(extraThinking)!.Contains("thinking"),
    "Off and fallback strip nested reasoning options and keep other custom fields");
var raisedBudgetBody = MiniJson.Parse(AiAssist.RequestBody("fixture-think", aiMessages, true, reasoning: AiReasoningLevel.Low,
    extraRequestJson: """{"thinking":{"type":"enabled","budget_tokens":6000},"max_tokens":1000}"""));
Check((double)MiniJson.Get(raisedBudgetBody, "max_tokens")! > 6000, "custom Anthropic budget raises too-small output cap");
var adaptiveBody = MiniJson.Parse(AiAssist.RequestBody("fixture-think", aiMessages, true, reasoning: AiReasoningLevel.High,
    extraRequestJson: """{"thinking":{"type":"adaptive"},"output_config":{"effort":"high"}}"""));
Check(MiniJson.Text(MiniJson.Get(adaptiveBody, "thinking"), "type") == "adaptive" && MiniJson.Get(MiniJson.Get(adaptiveBody, "thinking"), "budget_tokens") == null,
    "extra JSON supports adaptive provider options without model guessing");
foreach (string protectedField in new[] { "messages", "system", "model", "stream", "API_KEY", "authorization", "headers" })
    Throws<InvalidOperationException>(() => AiAssist.RequestBody("fixture-fast", aiMessages, false, extraRequestJson: "{\"" + protectedField + "\":\"fixture\"}"), "protected extra field " + protectedField);
foreach (string invalidExtra in new[] { "[]", "{broken}", "{\"max_tokens\":0}", "{\"max_tokens\":1.5}", "{\"temperature\":\"high\"}", "{\"temperature\":1e999}", "{\"thinking\":\"on\"}", "{\"reasoning_effort\":true}", "{\"vendor\":{\"api_key\":\"fixture\"}}", new string(' ', 16_384) + "{}" })
    Throws<InvalidOperationException>(() => AiAssist.RequestBody("fixture-fast", aiMessages, false, extraRequestJson: invalidExtra), "invalid or unsafe extra JSON");
Throws<InvalidOperationException>(() => AiAssist.RequestBody("fixture-think", aiMessages, true, reasoning: AiReasoningLevel.Low,
    extraRequestJson: """{"thinking":{"type":"enabled","budget_tokens":100}}"""), "Anthropic budget minimum enforced after custom merge");
Throws<ArgumentOutOfRangeException>(() => AiAssist.RequestBody("fixture-fast", aiMessages, false, maxTokens: 0), "request token limit must be positive");
Throws<ArgumentOutOfRangeException>(() => AiAssist.RequestBody("fixture-fast", aiMessages, false, reasoning: (AiReasoningLevel)99), "reasoning level must be recognized");

var reasoningReply = AiProvider.Reply("""{"choices":[{"message":{"content":"```sql\nSELECT 1;\n```","reasoning_content":"```sql\nDELETE dbo.Hidden;\n```"}}],"usage":{"prompt_tokens":11,"completion_tokens":7,"completion_tokens_details":{"reasoning_tokens":3}}}""", false);
Check(AiAssist.ExtractSql(reasoningReply.Text) == "SELECT 1;" && reasoningReply.Reasoning.Contains("DELETE dbo.Hidden") && !reasoningReply.Text.Contains("DELETE")
    && reasoningReply.InputTokens == 11 && reasoningReply.OutputTokens == 7 && reasoningReply.ReasoningTokens == 3, "OpenAI-compatible reasoning and usage stay separate from runnable SQL");
var anthropicReasoningReply = AiProvider.Reply("""{"content":[{"type":"thinking","thinking":"private plan","signature":"fixture-signature"},{"type":"redacted_thinking","data":"fixture-hidden"},{"type":"text","text":"answer"}],"usage":{"input_tokens":12,"output_tokens":9,"output_tokens_details":{"thinking_tokens":4}}}""", true);
Check(anthropicReasoningReply.Text == "answer" && anthropicReasoningReply.Reasoning == "private plan" && anthropicReasoningReply.InputTokens == 12
    && anthropicReasoningReply.OutputTokens == 9 && anthropicReasoningReply.ReasoningTokens == 4 && !anthropicReasoningReply.Reasoning.Contains("signature"), "Anthropic thinking blocks and token breakdown exclude signatures and redacted content");
var unknownUsageReply = AiProvider.Reply("""{"choices":[{"message":{"content":"answer"}}],"usage":{"prompt_tokens":-1,"completion_tokens":2.5,"completion_tokens_details":{"reasoning_tokens":"3"}}}""", false);
Check(unknownUsageReply.InputTokens == null && unknownUsageReply.OutputTokens == null && unknownUsageReply.ReasoningTokens == null
    && AiProvider.Reply("""{"content":[{"type":"text","text":"answer"}]}""", true).InputTokens == null, "missing or malformed usage remains unknown");
Check(AiAssist.ReplyText("""{"choices":[{"message":{"content":[{"type":"thinking","text":"hidden"},{"type":"text","text":"answer"}]}}]}""", false) == "answer", "content-part thinking cannot enter answer text");

var reasoningStream = new AiStream(false);
Check(StreamEvent(reasoningStream, """data: {"choices":[{"index":0,"delta":{"reasoning_content":"private plan"}}]}""") == "" && reasoningStream.ReasoningDelta == "private plan",
    "OpenAI-compatible reasoning delta does not become answer delta");
Check(reasoningStream.Line(": keepalive") == "" && reasoningStream.ReasoningDelta == "", "reasoning delta resets on every line");
Check(StreamEvent(reasoningStream, """data: {"choices":[{"index":0,"delta":{"content":"answer","reasoning_content":"more thought"}}]}""") == "answer"
    && reasoningStream.ReasoningDelta == "more thought", "simultaneous answer and reasoning deltas remain separate");
Check(StreamEvent(reasoningStream, """data: {"choices":[],"usage":{"prompt_tokens":21,"completion_tokens":13,"completion_tokens_details":{"reasoning_tokens":8}}}""") == ""
    && reasoningStream.InputTokens == 21 && reasoningStream.OutputTokens == 13 && reasoningStream.ReasoningTokens == 8, "usage-only stream chunk retained without answer text");
Check(StreamEvent(reasoningStream, "data: [DONE]") == "" && reasoningStream.Done && reasoningStream.InputTokens == 21, "stream completion retains usage");
var thinkingStream = new AiStream(true);
Check(StreamEvent(thinkingStream, """data: {"type":"message_start","message":{"usage":{"input_tokens":31,"output_tokens":1}}}""") == "" && thinkingStream.InputTokens == 31,
    "Anthropic message_start records initial usage");
Check(StreamEvent(thinkingStream, """data: {"type":"content_block_start","content_block":{"type":"thinking","thinking":"start "}}""") == "" && thinkingStream.ReasoningDelta == "start "
    && StreamEvent(thinkingStream, """data: {"type":"content_block_delta","delta":{"type":"thinking_delta","thinking":"thought"}}""") == "" && thinkingStream.ReasoningDelta == "thought", "Anthropic thinking starts and deltas remain separate");
Check(StreamEvent(thinkingStream, """data: {"type":"content_block_delta","delta":{"type":"signature_delta","signature":"fixture-signature"}}""") == "" && thinkingStream.ReasoningDelta == "", "thinking signatures are never displayed");
Check(StreamEvent(thinkingStream, """data: {"type":"message_delta","usage":{"output_tokens":19,"output_tokens_details":{"thinking_tokens":10}}}""") == ""
    && thinkingStream.InputTokens == 31 && thinkingStream.OutputTokens == 19 && thinkingStream.ReasoningTokens == 10, "Anthropic cumulative final usage preserves initial input count");

string requestedReasoning = AiAssist.RequestBody("fixture-think", aiMessages, false, reasoning: AiReasoningLevel.High);
Check(AiProvider.ReasoningRejected(400, """{"error":{"message":"reasoning_effort is not supported"}}""", false, requestedReasoning)
    && !AiProvider.ReasoningRejected(400, """{"error":{"message":"reasoning_effort is not supported"}}""", false, AiAssist.RequestBody("fixture-fast", aiMessages, false)), "reasoning retry requires field actually sent");
Check(!AiProvider.ReasoningRejected(401, """{"error":"reasoning_effort unsupported"}""", false, requestedReasoning)
    && !AiProvider.ReasoningRejected(400, """{"error":"invalid API key for reasoning_effort"}""", false, requestedReasoning)
    && !AiProvider.ReasoningRejected(400, """{"error":"insufficient credit for reasoning_effort"}""", false, requestedReasoning)
    && !AiProvider.ReasoningRejected(400, """{"error":"invalid model"}""", false, requestedReasoning), "reasoning fallback excludes auth credit and unrelated errors");
string requestedThinking = AiAssist.RequestBody("fixture-think", aiMessages, true, reasoning: AiReasoningLevel.Low);
Check(AiProvider.ReasoningRejected(400, """{"error":{"message":"thinking.type.enabled is not supported"}}""", true, requestedThinking)
    && AiProvider.ReasoningRejected(400, """{"error":{"message":"unknown parameter","param":"budget_tokens"}}""", true, requestedThinking)
    && !AiProvider.ReasoningRejected(400, """{"error":"invalid type"}""", true, requestedThinking), "Anthropic retry names thinking or budget field rather than generic nested type");
Console.WriteLine($"PASS: {checks} total checks including provider models, reasoning and usage. SSMS integration not tested.");

Check(!AiStream.Rejected(400, "{\"error\":{\"message\":\"stream not supported; credit exhausted\"}}"), "stream fallback must not retry billing failures");
Check(!AiStream.Rejected(400, "{\"error\":{\"message\":\"stream not supported; invalid api_key\"}}"), "stream fallback must not retry credential failures");
Check(!AiAssist.RequestBody("fixture-model", aiMessages, false, baseUrl: "https://api.openai.com/v1").Contains("\"temperature\""), "official OpenAI omits temperature even when effort is omitted");
Console.WriteLine($"PASS: {checks} total checks including provider fallback and default reasoning compatibility. SSMS integration not tested.");

var customThinking = MiniJson.Parse(AiAssist.RequestBody("fixture-model", aiMessages, false, reasoning: AiReasoningLevel.High,
    extraRequestJson: "{\"reasoning_effort\":null,\"thinking\":true}"));
Check(MiniJson.Get(customThinking, "thinking") is true && MiniJson.Get(customThinking, "reasoning_effort") == null,
    "custom thinking flag can omit default effort field");
var customReasoning = MiniJson.Parse(AiAssist.RequestBody("fixture-model", aiMessages, false, reasoning: AiReasoningLevel.High,
    extraRequestJson: "{\"reasoning_effort\":null,\"reasoning\":{\"effort\":\"high\"}}"));
Check(MiniJson.Text(MiniJson.Get(customReasoning, "reasoning"), "effort") == "high" && MiniJson.Get(customReasoning, "reasoning_effort") == null,
    "custom nested effort can replace generic effort field");
Console.WriteLine($"PASS: {checks} total checks including custom reasoning overrides. SSMS integration not tested.");

// DeepSeek shares its output cap between reasoning and the final answer, even with default thinking.
foreach (var level in new[] { AiReasoningLevel.Off, AiReasoningLevel.Low, AiReasoningLevel.Medium, AiReasoningLevel.High })
{
    var deepSeekDefault = MiniJson.Parse(AiAssist.RequestBody("fixture-think", aiMessages, false, reasoning: level, baseUrl: "https://api.deepseek.com/v1"));
    Check((double)MiniJson.Get(deepSeekDefault, "max_tokens")! == 65_536, "DeepSeek default leaves room for reasoning and answer " + level);
}
foreach (int explicitCap in new[] { 700, 4096 })
{
    var deepSeekExplicit = MiniJson.Parse(AiAssist.RequestBody("fixture-think", aiMessages, false, maxTokens: explicitCap,
        reasoning: AiReasoningLevel.High, baseUrl: "https://api.deepseek.com"));
    Check((double)MiniJson.Get(deepSeekExplicit, "max_tokens")! == explicitCap, "explicit DeepSeek cap preserved " + explicitCap);
}
var deepSeekOverride = MiniJson.Parse(AiAssist.RequestBody("fixture-think", aiMessages, false, reasoning: AiReasoningLevel.High,
    extraRequestJson: """{"max_tokens":8192}""", baseUrl: "https://api.deepseek.com"));
Check((double)MiniJson.Get(deepSeekOverride, "max_tokens")! == 8192, "DeepSeek custom output limit wins over default");
foreach (string providerUrl in new[] { "https://api.openai.com/v1", "https://api.deepseek.com.evil.example", "https://example.com/api.deepseek.com", "http://localhost:11434/v1" })
{
    var unchangedLimit = MiniJson.Parse(AiAssist.RequestBody("fixture-think", aiMessages, false, reasoning: AiReasoningLevel.High, baseUrl: providerUrl));
    Check((double)(MiniJson.Get(unchangedLimit, "max_tokens") ?? MiniJson.Get(unchangedLimit, "max_completion_tokens"))! == 4096,
        "DeepSeek output default requires exact provider host " + providerUrl);
}
var unchangedAnthropicLimit = MiniJson.Parse(AiAssist.RequestBody("fixture-think", aiMessages, true, baseUrl: "https://api.deepseek.com"));
Check((double)MiniJson.Get(unchangedAnthropicLimit, "max_tokens")! == 4096, "DeepSeek output default applies only to OpenAI-compatible format");

var exhaustedReply = AiProvider.Reply("""{"choices":[{"message":{"content":null,"reasoning_content":"private plan"},"finish_reason":"length"}],"usage":{"prompt_tokens":3000,"completion_tokens":4096,"completion_tokens_details":{"reasoning_tokens":4096}}}""", false);
string? exhaustedProblem = AiProvider.CompletionProblem(exhaustedReply);
Check(exhaustedReply.Text == "" && exhaustedReply.Reasoning == "private plan" && exhaustedReply.TokenLimitReached && exhaustedReply.FinishReason == "length"
    && exhaustedReply.InputTokens == 3000 && exhaustedReply.OutputTokens == 4096 && exhaustedReply.ReasoningTokens == 4096,
    "reasoning-only token-limited JSON retains stop reason and usage");
Check(exhaustedProblem != null && exhaustedProblem.Contains("while reasoning") && exhaustedProblem.Contains("Out: 4096") && exhaustedProblem.Contains("Reasoning tokens: 4096")
    && !exhaustedProblem.Contains("private plan"), "token-limit diagnostic gives local remedy and usage without provider text");
var anthropicExhaustedReply = AiProvider.Reply("""{"content":[{"type":"thinking","thinking":"private plan"}],"stop_reason":"max_tokens","usage":{"input_tokens":17,"output_tokens":8192}}""", true);
Check(anthropicExhaustedReply.TokenLimitReached && anthropicExhaustedReply.OutputTokens == 8192 && AiProvider.CompletionProblem(anthropicExhaustedReply)!.Contains("token limit"),
    "Anthropic reasoning-only max_tokens stop receives same diagnostic");
var contextLimitReply = AiProvider.Reply("""{"content":[],"stop_reason":"model_context_window_exceeded","usage":{"input_tokens":8192,"output_tokens":0}}""", true);
Check(!contextLimitReply.TokenLimitReached && AiProvider.CompletionProblem(contextLimitReply)!.Contains("context limit"), "context exhaustion is distinguished from output exhaustion");
var partialLimitReply = AiProvider.Reply("""{"choices":[{"message":{"content":"partial answer"},"finish_reason":"length"}]}""", false);
Check(partialLimitReply.Text == "partial answer" && partialLimitReply.TokenLimitReached && AiProvider.CompletionProblem(partialLimitReply) == null,
    "nonempty token-limited answer remains visible with stop metadata");
Throws<InvalidOperationException>(() => AiProvider.Reply("""{"choices":[{"message":{"content":"","reasoning_content":"private plan"},"finish_reason":"stop"}]}""", false),
    "reasoning-only normal stop remains an empty-answer failure");
var unknownFinishReply = AiProvider.Reply("""{"choices":[{"message":{"content":"answer"},"finish_reason":"credential echo\nfixture-secret"}]}""", false);
Check(unknownFinishReply.FinishReason == null && !unknownFinishReply.TokenLimitReached
    && new AiReply("answer", finishReason: "LENGTH").FinishReason == null, "unknown or malicious stop reasons cannot enter diagnostic metadata");

var exhaustedStream = new AiStream(false);
Check(StreamEvent(exhaustedStream, """data: {"choices":[{"index":0,"delta":{"reasoning_content":"private plan"},"finish_reason":"length"}]}""") == ""
    && exhaustedStream.ReasoningDelta == "private plan" && exhaustedStream.TokenLimitReached, "streaming reasoning-only length stop preserves separate reasoning");
StreamEvent(exhaustedStream, """data: {"choices":[],"usage":{"prompt_tokens":31,"completion_tokens":4096}}""");
StreamEvent(exhaustedStream, "data: [DONE]");
Check(exhaustedStream.Done && exhaustedStream.Finish() == "" && exhaustedStream.FinishReason == "length" && exhaustedStream.OutputTokens == 4096,
    "usage-only and DONE chunks retain token-limit stop reason");
var exhaustedThinkingStream = new AiStream(true);
StreamEvent(exhaustedThinkingStream, """data: {"type":"message_delta","delta":{"stop_reason":"max_tokens"},"usage":{"output_tokens":8192}}""");
StreamEvent(exhaustedThinkingStream, """data: {"type":"message_stop"}""");
Check(exhaustedThinkingStream.Done && exhaustedThinkingStream.TokenLimitReached && exhaustedThinkingStream.FinishReason == "max_tokens" && exhaustedThinkingStream.OutputTokens == 8192,
    "Anthropic message_delta retains final stop reason through message_stop");
var unknownFinishStream = new AiStream(false);
StreamEvent(unknownFinishStream, """data: {"choices":[{"index":1,"delta":{},"finish_reason":"length"},{"index":0,"delta":{},"finish_reason":"fixture-secret"}]}""");
Check(unknownFinishStream.FinishReason == null && !unknownFinishStream.TokenLimitReached, "other-choice and arbitrary stream stop reasons are ignored");
StreamEvent(unknownFinishStream, """data: {"choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}""");
StreamEvent(unknownFinishStream, """data: {"choices":[{"index":0,"delta":{},"finish_reason":"fixture-secret"}]}""");
Check(unknownFinishStream.FinishReason == "stop", "unknown stream stop value cannot replace known metadata");
Check(CompletionRules.OpensList('=', "WHERE a =", "") && CompletionRules.OpensList('(', "COUNT(", "") && CompletionRules.OpensList('o', "FROM o", "o"), "list opens on letters and operators");
Check(!CompletionRules.OpensList(' ', "   ", "") && !CompletionRules.OpensList('5', "TOP 5", "5") && !CompletionRules.OpensList('a', "TOP 1a", "1a") && !CompletionRules.OpensList(';', "x;", ""), "no list on blank line, number or semicolon");
Check(CompletionRules.Group(MatchKind.Exact, false, true, true) == 0 && CompletionRules.Group(MatchKind.Prefix, true, false, true) == 1 &&
    CompletionRules.Group(MatchKind.Prefix, false, false, true) == 2 && CompletionRules.Group(MatchKind.Prefix, true, true, true) == 3 &&
    CompletionRules.Group(MatchKind.Exact, false, false, false) == 2, "groups: exact, promoted, other, snippet");
Check(CompletionRules.KeepsTyping("my table", "my", ' ') && !CompletionRules.KeepsTyping("mytable", "my", ' '), "commit key that continues the name is typed");
Check(CompletionRules.SwallowsKey("COUNT(", '(') && CompletionRules.SwallowsKey("[My Table]", ']') && CompletionRules.SwallowsKey("FROM ", ' ') &&
    !CompletionRules.SwallowsKey("Orders", '(') && !CompletionRules.SwallowsKey("GO", ' ') && !CompletionRules.SwallowsKey("a.", '.'), "doubled commit key is swallowed");
var tvf_tables = new[] { new SchemaTable("GL", "fn_GLCo", "Code", "Description"), new SchemaTable("GL", "GLCo", "Code") };
var tvf_procs = new[] { new SchemaProcedure("GL", "fn_GLCo", new[] { new SchemaParameter("@YEAR", "int", false, true), new SchemaParameter("@MONTH", "int", false, true) }, isFunction: true) };
CompletionItem Tvf(string text, string name) { int at = text.IndexOf('|'); return SqlCompletion.Complete(text.Remove(at, 1), at, tvf_tables, procedures: tvf_procs).Items.First(i => i.Name == name); }
Check(Tvf("SELECT * FROM gl.fn_GLCo|", "fn_GLCo") is { InsertText: "fn_GLCo(", Description: "function GL.fn_GLCo" }, "table-valued function after schema is a function call");
Check(Tvf("SELECT * FROM fn_|", "fn_GLCo") is { InsertText: "GL.fn_GLCo(", Description: "function GL.fn_GLCo" }, "table-valued function in FROM gets no alias before its arguments");
Check(Tvf("SELECT * FROM gl.GLC|", "GLCo") is { InsertText: "GLCo", Description: "table GL.GLCo" }, "tables next to functions stay tables");
Check(SqlCompletion.Complete("SELECT f. FROM GL.fn_GLCo(2025, 6) AS f", 9, tvf_tables, procedures: tvf_procs).Items.Any(i => i.Name == "Description"), "table-valued function columns still listed");
Console.WriteLine($"PASS: {checks} total checks including reasoning output caps and safe stop diagnostics. SSMS integration not tested.");
