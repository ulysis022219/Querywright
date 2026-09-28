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
Check(Rules("CREATE TABLE dbo.T (a ntext);").SequenceEqual(new[] { "SW010" }), "deprecated types");
Check(Rules("SELECT a.x FROM dbo.A a, dbo.B b WHERE a.x = b.x;").SequenceEqual(new[] { "SW011" }) &&
    Rules("SELECT a.x FROM dbo.A a JOIN dbo.B b ON a.x = b.x;").Length == 0, "old-style join");
Check(Rules("CREATE PROCEDURE dbo.sp_x AS SET NOCOUNT ON; SELECT 1;").SequenceEqual(new[] { "SW012" }), "sp_ prefix");
Check(Rules("CREATE PROCEDURE dbo.p AS SELECT 1;").SequenceEqual(new[] { "SW015" }) &&
    Rules("CREATE PROCEDURE dbo.p AS BEGIN SET NOCOUNT ON; SELECT 1; END").Length == 0, "SET NOCOUNT ON");
Check(Rules("SELECT ISNUMERIC('1');").SequenceEqual(new[] { "SW013" }), "ISNUMERIC");
Check(Rules("SELECT a FROM dbo.A WHERE a NOT IN (SELECT b FROM dbo.B);").SequenceEqual(new[] { "SW014" }) &&
    Rules("SELECT a FROM dbo.A WHERE a NOT IN (1, 2);").Length == 0, "NOT IN subquery");
Check(Rules("DECLARE @unused int; DECLARE @t TABLE (x int); SELECT x FROM @t;").SequenceEqual(new[] { "SW016" }) &&
    Rules("DECLARE @x int = 1;\nGO\nSELECT 1;").SequenceEqual(new[] { "SW016" }) &&
    Rules("CREATE PROCEDURE dbo.p @a int AS SET NOCOUNT ON; SELECT 1;").Length == 0, "unused variables");
Check(Rules("EXEC GetPeople;").SequenceEqual(new[] { "SW017" }) && Rules("EXEC dbo.GetPeople; EXEC sp_who; EXEC #tmp;").Length == 0, "unqualified EXEC");
var strict = new WorkbenchSettings { SW005 = RuleSeverity.Disabled };
Check(SqlAnalysis.Analyze("DELETE FROM dbo.T;", settings: strict).Diagnostics.Count == 0 && strict.Severity("PARSE1") == RuleSeverity.Error, "new rules configurable");
Console.WriteLine($"PASS: {checks} total checks including analysis batch 2. SSMS integration not tested.");

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
