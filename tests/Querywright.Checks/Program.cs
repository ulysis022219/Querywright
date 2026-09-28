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

// SQL Prompt-style completion: no schema, incomplete SQL, foreign keys and column types.
CompletionResult At(string text, IReadOnlyList<SchemaTable>? schema)
{
    int position = text.IndexOf('|');
    return SqlCompletion.Complete(text.Remove(position, 1), position, schema);
}
string[] Names(CompletionResult r) => r.Items.Select(i => i.Name).ToArray();
var sel = At("SEL|", null);
Check(sel.Items[0].Name == "SELECT" && sel.Items[0].InsertText == "SELECT" && sel.Items[0].Description == "keyword" && sel.Start == 0 && sel.Length == 3, "no-schema keyword");
Check(At("sel|", Array.Empty<SchemaTable>()).Items[0].Name == "SELECT", "empty schema, case-insensitive prefix");
var count = At("SELECT CO|", null).Items.Single(i => i.Name == "COUNT");
Check(count.InsertText == "COUNT(" && count.Description == "function", "function inserts parenthesis");
Check(At("SELECT IS|", null).Items[0].Name == "IS" && At("SELECT IS|", null).Items[1].Name == "ISNULL", "prefix-exact before context order");
Check(At("|", null).Items.Count is > 50 and <= 200, "empty input offers keywords and functions within the cap");
Check(Names(At("DECLARE @id int = 1, @name nvarchar(10);\nDECLARE @t TABLE (x int);\nSELECT @|", null)).SequenceEqual(new[] { "@id", "@name", "@t" }), "declared variables");
Check(At("DECLARE @old int;\nGO\nSELECT @|; DECLARE @later int;", null).Items.Count == 0, "variable batch and order isolation");
Check(Names(At("CREATE PROCEDURE dbo.P @pid int, @flag bit AS SELECT @p|", null)).SequenceEqual(new[] { "@pid" }), "procedure parameters");
Check(Names(At("DECLARE @t TABLE (x int); DECLARE @n int; SELECT * FROM @|", null)).SequenceEqual(new[] { "@t" }), "table variables after FROM");
Check(Names(At("DECLARE @a int = (SELECT MAX(x) FROM dbo.T WHERE y IN (1, 2)), @b int; SELECT @| FROM (", null)).SequenceEqual(new[] { "@a", "@b" }), "variables in unparsable SQL");
Check(At("SELECT N'abc|", null).Items.Count == 0 && At("/* SEL|", null).Items.Count == 0, "unterminated literal and comment");
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

var nameItem = At("SELECT p.| FROM dbo.People p;", fkCatalog).Items.Single(i => i.Name == "Name");
Check(nameItem.Description == "column nvarchar(100) p.Name" && nameItem.InsertText == "[Name]", "typed column description");
Check(At("SELECT Na|me FROM dbo.People;", fkCatalog).Items[0].Description == "column nvarchar(100) People.Name", "typed unqualified column");
Check(At("SELECT * FROM |", fkCatalog).Items.Single(i => i.Name == "Orders").Description == "table dbo.Orders", "table description");
var noFrom = At("SELECT Na|", fkCatalog);
Check(noFrom.Items[0].Name == "Name" && noFrom.Items[0].InsertText == "[Name]" && noFrom.Start == 7 && noFrom.Length == 2, "SELECT column without FROM");
Check(Names(At("SELECT * FROM dbo.|", fkCatalog)).SequenceEqual(new[] { "Orders", "People" }) && At("SELECT * FROM dbo.|", fkCatalog).Items[0].InsertText == "[Orders]", "schema-dot tables");
Check(Names(At("SELECT * FROM sales.| WHERE", fkCatalog)).SequenceEqual(new[] { "Lines", "Notes" }), "schema-dot tables in unparsable SQL");
Check(Names(At("SELECT * FROM sales.L|", fkCatalog)).SequenceEqual(new[] { "Lines" }), "schema-dot prefix");
Check(At("SELECT p.| FROM dbo.People p WHERE", fkCatalog).Items.Count == 3, "alias-dot columns in unparsable SQL");
Check(Names(At("SELECT p.Na| FROM dbo.People AS p JOIN", fkCatalog)).SequenceEqual(new[] { "Name" }), "alias-dot prefix with AS");
Check(Names(At("SELECT * FROM dbo.People p WHERE p.| AND", fkCatalog)).SequenceEqual(new[] { "Born", "Id", "Name" }), "alias-dot in WHERE");
Check(At("SELECT dbo.People.| FROM dbo.People", fkCatalog).Items.Count == 3, "schema.table-dot columns");
Check(At("SELECT People.| FROM", fkCatalog).Items.Count == 3, "table-dot columns without FROM source");
Check(At("SELECT x.| FROM dbo.Missing x WHERE", fkCatalog).Items.Count == 0, "unknown alias metadata in unparsable SQL");
Check(Names(At("SELECT d.| FROM (SELECT Id AS Pid, Name FROM dbo.People) d WHERE", fkCatalog)).SequenceEqual(new[] { "Name", "Pid" }), "derived columns in unparsable SQL");
Check(Names(At("WITH recent (Oid) AS (SELECT OrderId FROM dbo.Orders), big AS (SELECT Total FROM dbo.Orders) SELECT r.| FROM recent r, big b WHERE", fkCatalog))
    .SequenceEqual(new[] { "Oid" }), "CTE columns in unparsable SQL");
var cte = At("WITH recent AS (SELECT OrderId FROM dbo.Orders) SELECT * FROM re|", fkCatalog).Items.Single();
Check(cte.Name == "recent" && cte.Description == "cte", "CTE names in scope");
var where = At("SELECT * FROM dbo.People p WHERE |", fkCatalog);
Check(where.Items[0].Description.StartsWith("column") && where.Items.Any(i => i.Name == "p" && i.Description == "alias dbo.People") &&
    where.Items.TakeWhile(i => i.Description.StartsWith("column")).Count() == 3, "columns first after WHERE");
Check(At("SELECT * FROM dbo.People p |", fkCatalog).Items[0].Description == "keyword" && Names(At("SELECT * FROM dbo.People p WH|", fkCatalog)).SequenceEqual(new[] { "WHEN", "WHERE", "WHILE" }), "keywords first after a source");
var fromItems = At("SELECT * FROM |", fkCatalog).Items;
Check(fromItems.Count == 4 && fromItems.All(i => i.Description.StartsWith("table")), "only tables after FROM");
Check(At("UPDATE |", fkCatalog).Items.Count == 4 && At("INSERT INTO sales.|", fkCatalog).Items.Count == 2 && At("SELECT * FROM dbo.People p, |", fkCatalog).Items.Count == 4, "table positions");
var wide = Enumerable.Range(0, 300).Select(i => new SchemaTable("dbo", "T" + i, "c")).ToArray();
Check(At("SELECT * FROM T|", wide).Items.Count == 200, "cap applies to tables");
Console.WriteLine($"PASS: {checks} total checks including incomplete-SQL completion. SSMS integration not tested.");

Check(At("SELECT * FROM dbo.People p JOIN dbo.Orders o ON |", fkCatalog).Items[0].InsertText == "o.PersonId = p.Id", "FK join child to parent");
var reverse = At("SELECT * FROM dbo.Orders AS o INNER JOIN dbo.People AS p ON |", fkCatalog).Items[0];
Check(reverse.Name == "p.Id = o.PersonId" && reverse.Description == "foreign key dbo.Orders -> dbo.People", "FK join parent to child");
Check(At("SELECT * FROM dbo.People JOIN dbo.Orders ON |", fkCatalog).Items[0].Name == "Orders.PersonId = People.Id", "FK join without aliases");
Check(At("SELECT * FROM People p LEFT JOIN Orders ON |", fkCatalog).Items[0].Name == "Orders.PersonId = p.Id", "FK join default schema");
Check(At("SELECT * FROM sales.Lines l JOIN sales.Notes n ON |", fkCatalog).Items[0].Name == "n.OrderId = l.OrderId AND n.LineNumber = l.LineNumber", "composite FK join");
Check(At("SELECT * FROM sales.Notes n JOIN sales.Lines l ON |", fkCatalog).Items[0].Name == "l.OrderId = n.OrderId AND l.LineNumber = n.LineNumber", "composite FK join reverse");
var chain = At("SELECT * FROM dbo.People p JOIN dbo.Orders o ON o.PersonId = p.Id\nJOIN sales.Lines l ON |\nWHERE p.Id = 1;", fkCatalog);
Check(chain.Items[0].Name == "l.OrderId = o.OrderId" && chain.Items[1].Description.StartsWith("column"), "FK join to earlier source, then columns");
var typed = At("SELECT * FROM dbo.People p JOIN dbo.Orders o ON o|", fkCatalog);
Check(typed.Items[0].Name == "o.PersonId = p.Id" && typed.Start == 48 && typed.Length == 1, "FK join with typed prefix");
Check(At("SELECT * FROM dbo.People p JOIN sales.Notes n ON |", fkCatalog).Items.All(i => !i.Name.Contains(" = ")), "no FK, no join condition");
Check(At("SELECT * FROM dbo.People p WHERE |", fkCatalog).Items.All(i => !i.Name.Contains(" = ")), "join conditions only after ON");
Console.WriteLine($"PASS: {checks} total checks including FK join completion. SSMS integration not tested.");
