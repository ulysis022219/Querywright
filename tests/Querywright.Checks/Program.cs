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
Check(Complete("SELECT p.Na|me FROM dbo.People p;").Items.Single().InsertText == "[Name]", "partial identifier replacement");
Check(Complete("SELECT p.| FROM dbo.People p;").Items.Any(i => i.InsertText == "[odd]]column]"), "escaped insertion");
Check(Complete("SELECT 1 FROM dbo.Pe|;").Items.Single().InsertText == "[People]", "schema objects");
Check(Complete("SELECT 1 FROM Pe|;").Items.Single().InsertText == "[dbo].[People]", "qualified object insertion");
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
Check(Expand("SELECT *| FROM dbo.People;").Text == "[Id], [Name], [odd]]column]", "single-table wildcard");
Check(Expand("SELECT o.*|, 1 FROM dbo.People p JOIN dbo.Orders o ON p.Id = o.PersonId;") is var q && q.Text == "[o].[OrderId], [o].[PersonId]" && q.Start == 7 && q.Length == 3, "qualified wildcard span");
Check(Expand("SELECT |* FROM People p, dbo.Orders o;").Text.StartsWith("[p].[Id], [p].[Name], [p].[odd]]column], [o].[OrderId]"), "multi-table wildcard order");
Check(Expand("WITH c AS (SELECT OrderId FROM dbo.Orders) SELECT *| FROM c;").Text == "[OrderId]", "CTE wildcard");
Check(Expand("SELECT 1 FROM dbo.People p WHERE EXISTS (SELECT *| FROM dbo.Orders);").Text == "[OrderId], [PersonId]", "inner scope wildcard");
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
    Rules("SELECT a FROM dbo.T WITH (READCOMMITTEDLOCK, INDEX(ix));").Length == 0, "NOLOCK hint");
Check(Rules("CREATE PROCEDURE dbo.p AS BEGIN SET NOCOUNT ON; WAITFOR DELAY '00:00:01'; RETURN 0; END").SequenceEqual(new[] { "SW025" }) &&
    Rules("WAITFOR DELAY '00:00:01';").Length == 0, "WAITFOR DELAY in procedure");
Check(Rules("SELECT TOP (5) a FROM dbo.T;").SequenceEqual(new[] { "SW026" }) &&
    Rules("SELECT TOP (5) a FROM dbo.T ORDER BY a; IF EXISTS (SELECT TOP 1 1 FROM dbo.T) SELECT 1;").Length == 0, "TOP without ORDER BY");
Check(Rules("DECLARE @s nvarchar(100) = N'SELECT 1'; EXEC (@s);").SequenceEqual(new[] { "SW027" }) &&
    Rules("DECLARE @s nvarchar(100) = N'SELECT 1'; EXEC sys.sp_executesql @s;").Length == 0, "EXECUTE(string)");
Console.WriteLine($"PASS: {checks} total checks including analysis batch 3. SSMS integration not tested.");

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
