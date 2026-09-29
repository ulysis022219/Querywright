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

var folder = Path.Combine(Path.GetTempPath(), "Querywright-check-" + Guid.NewGuid().ToString("N"));
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

var library = Path.Combine(Path.GetTempPath(), "Querywright-library-" + Guid.NewGuid().ToString("N"));
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
        ["sd"] = "x", ["smf"] = "x", ["ii"] = "dbo.T", ["df"] = "dbo.T WHERE Id = 1;",
        ["cj"] = "dbo.B b", ["gb"] = "x", ["ob"] = "x", ["cte"] = "SELECT 1 AS x", ["ct"] = "Name nvarchar(50) NULL", ["ctt"] = "Name nvarchar(50) NULL",
        ["cv"] = "SELECT 1 AS x;", ["csf"] = "@param + 1", ["af"] = "@param + 1", ["ctf"] = "INSERT @result VALUES (@param);",
        ["citf"] = "SELECT @param AS Id", ["at"] = "dbo.T ADD x int NULL", ["ata"] = "x int NULL", ["atd"] = "x", ["ac"] = "x bigint NOT NULL",
        ["dt"] = "dbo.X", ["dp"] = "dbo.X", ["dv"] = "dbo.X", ["dfn"] = "dbo.X", ["di"] = "IX_T", ["lk"] = "abc",
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

    var listed = SnippetFiles.List(library);
    Check(listed.Select(p => p.Key).SequenceEqual(shortcuts.Append("Select").Order(StringComparer.Ordinal)), "list valid seeded shortcuts in ordinal order");
    Check(listed.Single(p => p.Key == "ssf").Value == "SELECT * FROM $CURSOR$", "list first line");
    Check(listed.Single(p => p.Key == "tc").Value == "BEGIN TRY", "list multiline first line");
}
finally { Directory.Delete(library, true); }

var listFolder = Path.Combine(Path.GetTempPath(), "Querywright-list-" + Guid.NewGuid().ToString("N"));
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
Check(Complete("SELECT p.Na|me FROM dbo.People p;").Items.Single().InsertText == "Name", "partial identifier replacement");
Check(Complete("SELECT p.| FROM dbo.People p;").Items.Any(i => i.InsertText == "[odd]]column]"), "escaped insertion");
Check(Complete("SELECT 1 FROM dbo.Pe|;").Items.Single().InsertText == "People", "schema objects");
Check(Complete("SELECT 1 FROM Pe|;").Items.Single().InsertText == "dbo.People", "qualified object insertion");
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
Console.WriteLine($"PASS: {checks} total checks including navigation. SSMS integration not tested.");

Check(SnippetFiles.ShortcutBefore("SELECT 1;\nssf", 13) == "ssf", "shortcut word");
Check(SnippetFiles.ShortcutBefore("@ssf", 4) == null && SnippetFiles.ShortcutBefore("x.ssf", 5) == null && SnippetFiles.ShortcutBefore("ssf ", 4) == null, "shortcut boundaries");
var shortcutFolder = Path.Combine(Path.GetTempPath(), "Querywright-shortcut-" + Guid.NewGuid().ToString("N"));
try
{
    SnippetFiles.Initialize(shortcutFolder);
    var ssf = SnippetFiles.FindShortcut(shortcutFolder, "ssf");
    Check(ssf != null && Snippets.Expand(SnippetFiles.Read(ssf), context, now).Text == "SELECT * FROM ", "ssf snippet");
    Check(SnippetFiles.FindShortcut(shortcutFolder, "nope") == null && SnippetFiles.FindShortcut(shortcutFolder, "../ssf") == null, "unknown/unsafe shortcut");
}
finally { Directory.Delete(shortcutFolder, true); }
Console.WriteLine($"PASS: {checks} total checks including snippet shortcuts. SSMS integration not tested.");

string[] Rules(string sql) => SqlAnalysis.Analyze(sql).Diagnostics.Select(d => d.Rule).Distinct().Order().ToArray();
Check(Rules("DELETE FROM dbo.T;").SequenceEqual(new[] { "SW005" }) && Rules("DELETE FROM dbo.T WHERE Id = 1;").Length == 0, "DELETE without WHERE");
Check(Rules("UPDATE dbo.T SET x = 1;").SequenceEqual(new[] { "SW006" }) && Rules("UPDATE dbo.T SET x = 1 WHERE Id = 1;").Length == 0, "UPDATE without WHERE");
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
    Rules("CREATE PROCEDURE dbo.p @a int AS SET NOCOUNT ON; SELECT 1;").Length == 0, "unused variables");
Check(Rules("EXEC GetPeople;").SequenceEqual(new[] { "SW017" }) && Rules("EXEC dbo.GetPeople; EXEC sp_who; EXEC #tmp;").Length == 0, "unqualified EXEC");
var strict = new WorkbenchSettings { SW005 = RuleSeverity.Disabled };
Check(SqlAnalysis.Analyze("DELETE FROM dbo.T;", settings: strict).Diagnostics.Count == 0 && strict.Severity("PARSE1") == RuleSeverity.Error, "new rules configurable");
Console.WriteLine($"PASS: {checks} total checks including analysis batch 2. SSMS integration not tested.");

Check(Rules("DECLARE c CURSOR FOR SELECT 1; OPEN c;").SequenceEqual(new[] { "SW018" }) && Rules("DECLARE @c CURSOR; SET @c = CURSOR FOR SELECT 1; OPEN @c;").SequenceEqual(new[] { "SW018" }) &&
    Rules("DECLARE c CURSOR LOCAL FAST_FORWARD FOR SELECT 1; DECLARE g CURSOR GLOBAL FOR SELECT 1;").Length == 0, "cursor scope");
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
    Rules("SELECT TOP (5) a FROM dbo.T ORDER BY a; IF EXISTS (SELECT TOP 1 1 FROM dbo.T) SELECT 1;").Length == 0, "TOP without ORDER BY");
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
Check(Rules("SELECT 'x' = 1;").SequenceEqual(new[] { "SW040" }) && Rules("SELECT 1 AS 'x';").SequenceEqual(new[] { "SW040" }) &&
    Rules("SELECT 1 AS x, 2 AS [y], z = 3;").Length == 0, "string alias");
Check(Rules("CREATE PROCEDURE dbo.p;2 AS SET NOCOUNT ON;").SequenceEqual(new[] { "SW041" }) && Rules("CREATE PROCEDURE dbo.p AS SET NOCOUNT ON;").Length == 0, "numbered procedure");
Check(Rules("SELECT a FROM dbo.T WHERE a !< 1;").SequenceEqual(new[] { "SW042" }) && Rules("SELECT a FROM dbo.T WHERE a !> 1 OR a <> 2;").SequenceEqual(new[] { "SW042" }), "!< !>");
Check(Rules("SELECT TOP 100 PERCENT a FROM dbo.T ORDER BY a;").SequenceEqual(new[] { "SW043" }) &&
    Rules("SELECT TOP 50 PERCENT a FROM dbo.T ORDER BY a;").Length == 0, "TOP 100 PERCENT");
Check(Rules("IF EXISTS (SELECT COUNT(*) FROM dbo.T WHERE a = 1) SELECT 1;").SequenceEqual(new[] { "SW044" }) &&
    Rules("IF EXISTS (SELECT COUNT(*) FROM dbo.T GROUP BY a) SELECT 1; IF EXISTS (SELECT MAX(a) FROM dbo.T HAVING MAX(a) > 1) SELECT 1;").Length == 0, "EXISTS aggregate");
Check(Rules("EXEC master.dbo.xp_cmdshell 'dir';").SequenceEqual(new[] { "SW046" }) && Rules("EXEC xp_cmdshell 'dir';").SequenceEqual(new[] { "SW017", "SW046" }) &&
    Rules("EXEC dbo.xp_other;").Length == 0, "xp_cmdshell");
Check(Rules("-- querywright-disable SW029\nGOTO done; done: SELECT 1;").Length == 0, "new rule suppression");
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
Check(nameItem.Description == "column nvarchar(100) p.Name" && nameItem.InsertText == "Name", "typed column description");
Check(CompleteAt("SELECT Na|me FROM dbo.People;", fkCatalog).Items[0].Description == "column nvarchar(100) People.Name", "typed unqualified column");
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
Check(where.Items[0].Description.StartsWith("column") && where.Items.Any(i => i.Name == "p" && i.Description == "alias dbo.People") &&
    where.Items.TakeWhile(i => i.Description.StartsWith("column")).Count() == 3, "columns first after WHERE");
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
Check(chain.Items[0].Name == "l.OrderId = o.OrderId" && chain.Items[1].Description.StartsWith("column"), "FK join to earlier source, then columns");
var typed = CompleteAt("SELECT * FROM dbo.People p JOIN dbo.Orders o ON o|", fkCatalog);
Check(typed.Items[0].Name == "o.PersonId = p.Id" && typed.Start == 48 && typed.Length == 1, "FK join with typed prefix");
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
Check(Info("SELECT p.Full|Name FROM dbo.People p") == "FullName: column nvarchar(100) p.FullName", "quick info column");
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
Check(Updates.ErrorLine("0.3.0.42", "SplitTableAsync", typeof(FormatException)) == "Querywright 0.3.0.42 \u00B7 SplitTable \u00B7 FormatException"
    && Updates.ErrorLine(null, "Format", typeof(IOException)) == "Querywright unknown \u00B7 Format \u00B7 IOException", "error report line");
Check(Updates.IssueUrl("Querywright 0.3.0 \u00B7 Format \u00B7 IOException") == "https://github.com/ulysis022219/SqlWorkbench/issues/new?template=bug_report.yml&error=Querywright%200.3.0%20%C2%B7%20Format%20%C2%B7%20IOException", "issue link escapes the line");
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
Console.WriteLine($"PASS: {checks} total checks including fill, quick info, object scripts, fixes and object refactors. SSMS integration not tested.");
