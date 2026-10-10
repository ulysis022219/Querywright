# AI assistant and MCP server

Querywright's AI features work with the provider you choose. DeepSeek is the default; any service that speaks the
OpenAI chat-completions format or the Anthropic messages format works.

## Setup

Tools > Options > Querywright > AI (also Querywright > AI options...).

| Setting | Default | Notes |
| --- | --- | --- |
| API format | OpenAI-compatible | DeepSeek, OpenAI, OpenRouter, Groq, Mistral, Ollama, LM Studio and most others. Pick Anthropic for the Claude API. |
| Base URL | `https://api.deepseek.com` | `/chat/completions` (or `/v1/messages`) is added when missing. A local server such as `http://localhost:11434/v1` needs no key. |
| Model | `deepseek-chat` | The provider's model name. |
| API key | (none) | Type it once; it is encrypted for your Windows account (DPAPI) in `%LocalAppData%\Querywright\ai-key.bin` and never shown again. |
| Schema tables sent | 30 | Table definitions sent as context. 0 sends none. |
| Ask before running AI SQL | on | Confirmation box before the panel's Run button or the MCP `execute_sql` tool runs anything. |
| Run MCP server / port | off / 8765 | See below. |

## What is sent to the provider

Only when you use an AI command: your prompt, the script (or selection) in the query window, and the definitions of
the tables that the prompt or script mentions (names, columns, types, foreign keys) plus the names of other objects.
Fix adds SQL Server's compile errors; Optimize adds the estimated plan XML. Table data is never sent by the panel.

## Commands

| Command | What it does |
| --- | --- |
| AI assistant (Alt+Z) | Dockable chat transcript: write a query, change the selection, explain, fix, optimize, ask follow-ups. Up in an empty prompt recalls the last prompt. Recent prompts are kept in `%LocalAppData%\Querywright\ai-prompts.json`. |
| AI: explain SQL | Explains the selection or script. |
| AI: fix errors | Compiles each GO batch with `SET NOEXEC ON` (nothing runs) and asks for a corrected script. |
| AI: optimize query | Reads the estimated plan with `SET SHOWPLAN_XML ON` (nothing runs) and asks for a faster rewrite. |
| AI: complete at caret | Inserts the model's continuation at the caret. Ctrl+Z removes it. |
| Copy MCP configuration | Copies the client configuration, including the access token. |

## Chat panel

The panel uses a Codex-style layout: a scrolling transcript, user cards, spacious assistant replies and a rounded
composer at the bottom. Colors follow the SSMS light, dark or blue theme. Prose uses the normal UI font; SQL uses
Consolas with ScriptDom keyword, string and comment coloring. Markdown headings, lists, bold, italic, inline code,
fenced code and simple tables are rendered as text only; HTML, links and images are never loaded or opened.

Each SQL block has its own Copy, Insert, Replace, New window and Run actions. Earlier replies remain visible;
scrolling up suspends automatic scrolling until you return to the bottom. Explain, Fix, Optimize, Change selection
and Write query chips choose the next task. Without a chip, the first prompt writes a query and subsequent prompts
follow up. A chip starts fresh model context while earlier messages remain visible. New conversation clears both.
Enter or Ctrl+Enter sends; Shift+Enter inserts a newline. The prompt grows to six lines. Example prompts only fill
the composer; they do not send anything.

Answers stream as text, refreshed at most about 20 times a second; completed and stopped replies receive Markdown
formatting. Stop cancels the network request and leaves a clearly marked partial answer. A broken stream keeps its
partial text and reports failure. Explicit provider rejection of streaming retries once without streaming; providers
that return ordinary JSON also work. Failed requests do not add a turn to model history. Stopped partial answers
stay visible; they are not sent back as completed assistant turns.

The context bar shows server/database, Selection (line count) or Whole script, and the number of schema definitions.
Before Send, the count is a cached preview; while sending, it shows the actual count. Click scope to toggle editor
selection versus whole script. Uncheck Include schema to omit new schema context for one message; it resets after
the request. Previously sent schema remains in follow-up history: use New conversation to discard that history.
Fix and Optimize additionally send compile errors or estimated plan XML. No query results are collected or sent.

Replace opens a read-only side-by-side preview with Apply and Cancel; Cancel is the default. If the editor changes
while the preview is open, Apply refuses the stale edit. Run still goes through the existing confirmation option;
its confirmation flags ScriptDom-detected data/schema writes, execution, SELECT INTO and unrecognized SQL.

Regenerate retries the last request with its original SQL/schema context. Edit last prompt fills the composer;
Send replaces that prompt/reply using current editor context. Copy answer copies the entire Markdown response.
Transcripts remain in memory only; full conversation storage, slash templates, model switching, token usage, actual
plans and Messages-pane capture are deferred. Inline ghost completion is outside this round; it needs editor
adornments, cancellation and a separate acceptance/undo path. The existing completion command remains available.

Limits: completion is a command, not grey text while you type; Fix sees compile
errors only (a missing table or a runtime error is not found by `NOEXEC`).

## Not yet run in SSMS

Automated Core checks cover markdown, token coloring, SSE framing, schema counts and SQL warnings. Local HTTP
fixtures cover both stream formats, fallback, errors, truncation and cancellation without contacting a provider.
Offscreen panel/dialog checks cover per-block actions, theme resource changes and 100/125/150/200% layout. Run:

```powershell
dotnet run --project tests/Querywright.Checks
dotnet build src/Querywright.Ssms
powershell -NoProfile -File scripts/Test-AiClient.ps1
powershell -NoProfile -STA -File scripts/Test-AiPanel.ps1 -Out artifacts/ai-panel
100,125,150,200 | ForEach-Object { powershell -NoProfile -STA -File scripts/Test-Dialogs.ps1 -Scale $_ }
powershell -NoProfile -File scripts/Test-Package.ps1
```

`Test-AiPanel.ps1` loads SDK assemblies from an installed SSMS 22 without starting SSMS; its screenshots use fixture
theme dictionaries, not a claim of hands-on host validation. Hands-on panel checks still required:

- SSMS 22 light and dark themes at 100% and 150% scaling; docked and floating; keyboard navigation and long SQL.
- Send multiple prompts; scroll up during a reply; recall a prompt with Up; send with Enter/Ctrl+Enter and add a line with Shift+Enter.
- Multiple SQL blocks: each action must use its own block. Run with confirmation on and off against a disposable database.
- DeepSeek/OpenAI-compatible and Anthropic-format streaming; Stop mid-answer; non-streaming provider fallback; interrupted connection.
- Selection/whole-script toggle, schema off for one message, schema count after loading, changed active connection.
- Replace Apply/Cancel; edit the original buffer during preview and confirm stale Apply is refused.
- Regenerate preserves original context; edit/resend replaces only the last exchange; Copy answer preserves Markdown.

## MCP server

Lets an external agent (Claude Code, Claude Desktop, Cursor, any MCP client) work with the SSMS window you have open.
Turn on "Run MCP server", then Querywright > Copy MCP configuration and paste into the client's configuration:

```json
{"mcpServers":{"querywright":{"type":"http","url":"http://localhost:8765/mcp","headers":{"Authorization":"Bearer <token>"}}}}
```

Tools:

| Tool | Result |
| --- | --- |
| `get_active_query_editor_contents`, `get_active_query_editor_selected_text` | Script or selection of the active query window |
| `get_database_information` | Server, database, login, compatibility level, collation, version |
| `list_tables`, `list_views`, `list_stored_procedures` | Names, optional `filter` |
| `list_table_details`, `list_view_details`, `list_stored_procedure_details` | Script of one object (`name` as schema.name) |
| `list_related_objects` | Foreign keys both ways and objects that use it |
| `format_tsql` | Formatted text in your Querywright style |
| `list_tsql_execution_errors` | Compile errors (`NOEXEC`) |
| `list_tsql_code_analysis_issues`, `fix_code_analysis_issues` | Querywright analysis findings; script with automatic fixes applied |
| `get_query_execution_plan` | Estimated plan XML |
| `execute_sql` | **Really executes** the SQL on the query window's connection and login; returns result sets (200 rows each unless `max_rows`), messages and rows affected |

Tools that take `sql` use the active query window's script when it is omitted. No tool edits the query window.

### Security

`execute_sql` runs whatever the agent sends, with your login's permissions, and nothing is rolled back.

- The server listens on the loopback interface only and is off until you turn it on.
- Every request needs the bearer token (`%LocalAppData%\Querywright\mcp-token.txt`). Delete that file and restart SSMS to revoke it. Anyone holding the token can run SQL through your open window, so keep the configuration out of shared files.
- Requests carrying a non-loopback `Origin` (a web page in your browser) are refused.
- "Ask before running AI SQL" shows each `execute_sql` script, with server and database, for a Yes/No before it runs. Turning it off lets the agent run SQL unattended; do that only against a database you can afford to lose.
- Result rows returned by `execute_sql` go to the agent and from there to whatever model the agent uses.
