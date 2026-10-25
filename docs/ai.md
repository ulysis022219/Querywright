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
| Reasoning | Off | Request Low, Medium or High effort from the panel. Off omits fields; it does not override provider defaults. |
| Extra request JSON | empty | Provider-specific request object; messages, model, stream and credentials cannot be replaced. `{"temperature":null}` omits temperature. Reasoning fields apply only while reasoning is selected. |
| API key | (none) | Type it once; it is encrypted for your Windows account (DPAPI) in `%LocalAppData%\Querywright\ai-key.bin` and never shown again. |
| Schema tables sent | 30 | Table definitions sent as context. 0 sends none. |
| Ask before running AI SQL | on | Confirmation box before the panel's Run button or the MCP `execute_sql` tool runs anything. |
| Run MCP server / port | off / 8765 | See below. |

## What is sent to the provider

Only when you use an AI command: your prompt, the script (or selection) in the query window, and the definitions of
the tables that the prompt or script mentions (names, columns, types, foreign keys) plus the names of other objects.
Fix adds SQL Server's compile errors; Optimize adds the estimated plan XML; Review adds Querywright's own analysis
findings for the script. Each `@name` in the prompt adds that object's full script, the same text the MCP
`list_*_details` tools return: columns and keys for a table, the module code for a view or procedure. Mentions are
sent even with Schema switched off, so do not mention an object whose code you would not paste to the provider.
Table data is never sent by the panel.
Opening the model dropdown or clicking Refresh models sends a metadata-only GET with the stored key; no SQL,
schema or prompt is sent. Opening the panel alone makes no provider request.

## Commands

| Command | What it does |
| --- | --- |
| AI assistant (Alt+Z) | Dockable chat transcript: write a query, change the selection, explain, fix, optimize, review, document, write tests, ask follow-ups. Up in an empty prompt recalls the last prompt. Recent prompts are kept in `%LocalAppData%\Querywright\ai-prompts.json`. |
| AI: explain SQL | Explains the selection or script. |
| AI: fix errors | Compiles each GO batch with `SET NOEXEC ON` (nothing runs) and asks for a corrected script. |
| AI: optimize query | Reads the estimated plan with `SET SHOWPLAN_XML ON` (nothing runs) and asks for a faster rewrite. |
| AI: complete at caret | Inserts the model's continuation at the caret. Ctrl+Z removes it. |
| Copy MCP configuration | Copies the client configuration, including the access token. |

## Chat panel

The panel has three parts: a header, a scrolling transcript and a composer at the bottom. Colors follow the SSMS
light, dark or blue theme. Prose uses the normal UI font; SQL uses Consolas with ScriptDom keyword, string and
comment coloring and an accent rail down its left edge. Markdown headings, lists, bold, italic, inline code, fenced
code and tables are rendered as text only (tables as real grids); HTML, links and images are never loaded or opened.

The header shows the current model. Click it to open a drawer with the model picker, Refresh models, reasoning
effort and Provider settings; click again to close it. The icons on the right start a new conversation, export the
conversation as Markdown and open the diagnostic logs. An empty conversation shows starter prompts (write, explain,
review, mention a table, list commands); they fill the composer and send nothing.

Each SQL block has its own Copy, Insert, Replace, New window and Run actions. Earlier replies remain visible;
scrolling up suspends automatic scrolling until you return to the bottom. The chips under the prompt choose the next
task: Write query, Change selection, Explain, Fix, Optimize, Review, Document and Tests. Without a chip, the first
prompt writes a query and subsequent prompts follow up. A chip starts fresh model context while earlier messages
remain visible. New conversation clears both. Enter or Ctrl+Enter sends; Shift+Enter inserts a newline. The prompt
grows to six lines.

| Task | What it asks for |
| --- | --- |
| Review | Findings grouped under correctness, performance, security and maintainability, each with its line and fix. Querywright's analysis findings are sent as hints. A corrected script follows only when a change is worth making. |
| Document | The same script with a header comment (purpose, parameters, result, side effects) and comments on non-obvious steps. Code is not changed. |
| Tests | A test script that arranges data in a transaction, asserts with `THROW` and rolls back, covering normal, edge and failure cases. |

### Slash commands and mentions

Type `/` at the start of the prompt for a command list; Up and Down choose, Tab or Enter inserts, Escape closes.
`/help` or `/?` shows the list in the transcript without contacting the provider.

| Command | Does |
| --- | --- |
| `/write`, `/edit`, `/explain`, `/fix`, `/optimize` | Same as the matching chip, for this message only |
| `/review`, `/doc`, `/test` | Review, Document or Tests for this message |
| `/ask` | Follow-up question in this conversation |
| `/export` | Save the conversation as a Markdown file |
| `/clear` | Start a new conversation |

Type `@` to pick a table or view from the connected database; `@dbo.Orders` and `@[Sales].[usp_Recalc]` also work
typed by hand. Up to eight mentioned objects per message are scripted and sent with it (see What is sent). A name
the database does not know, such as a T-SQL `@variable`, is skipped.

Export writes the visible conversation, prompts and answers in order, as Markdown (`.md`) or plain text, with the
database and time at the top. Reasoning text, the `/help` card and a reply still streaming are not exported.

Answers stream as text, refreshed at most about 20 times a second; completed and stopped replies receive Markdown
formatting. Stop cancels the network request and leaves a clearly marked partial answer. A broken stream keeps its
partial text and reports failure. Explicit provider rejection of streaming retries once without streaming; providers
that return ordinary JSON also work. Failed requests do not add a turn to model history. Stopped partial answers
stay visible; they are not sent back as completed assistant turns.

The row above the prompt shows what the next message shares: a connection dot with Database on Server and the
number of schema tables, a scope pill reading Selection (with its line count) or Whole script, and a Schema pill.
Before Send, the count is a cached preview; while sending, it shows the actual count. Click the scope pill to toggle
editor selection versus whole script. Switch Schema off to omit new schema context for one message; it resets after
the request. Previously sent schema remains in follow-up history: use New conversation to discard that history.
Fix and Optimize additionally send compile errors or estimated plan XML. No query results are collected or sent.

Replace opens SSMS's native comparison tab and a modeless Apply/Cancel window; Cancel is the default. The tab remains
usable while deciding. Closing either window, the original editor or SSMS cancels. Old/new text use temporary SQL
files, removed when review ends. If the editor changes while the preview is open, Apply refuses the stale edit.
Run still goes through the existing confirmation option;
its confirmation flags ScriptDom-detected data/schema writes, execution, SELECT INTO and unrecognized SQL.

Regenerate retries the last request with its original SQL/schema context. Edit prompt fills the composer;
Send replaces that prompt/reply using current editor context. Copy answer copies the entire Markdown response.
Transcripts stay in memory until you export them; automatic conversation storage, actual plans and Messages-pane
capture are deferred. Inline ghost completion is outside this round; it needs editor adornments, cancellation and a
separate acceptance/undo path. The existing completion command remains available.

Limits: completion is a command, not grey text while you type; Fix sees compile
errors only (a missing table or a runtime error is not found by `NOEXEC`).

## Models and reasoning

The editable model picker loads `/models` from the configured OpenAI-compatible endpoint, or `/v1/models` for
Anthropic, including paginated Anthropic lists. Lists are cached per format/base URL for the session; Refresh
models reloads them. If discovery fails, type an identifier. Last ten choices and selected model per base URL
persist in `%LocalAppData%\Querywright\ai-models.json`; provider URL keys are hashes. Model choice and requested
reasoning are saved back to Tools > Options. Neither file stores the API key or reasoning text.

Off sends no reasoning/thinking/effort/budget fields, including nested Extra request JSON. Providers may still
reason by default. Low/Medium/High request `reasoning_effort` for generic OpenAI-compatible endpoints. The
documented DeepSeek host also receives `thinking:{"type":"enabled"}`; Medium maps to High there. Anthropic uses
manual budgets 1024/4096/8192 and raises `max_tokens` above the budget. Selected reasoning omits temperature.
The official Anthropic and OpenAI hosts always omit it, because default reasoning can reject sampling settings;
the official OpenAI host uses `max_completion_tokens`.

Set a reasoning field to null in Extra request JSON to omit its mapped default. For example,
`{"reasoning_effort":null,"thinking":true}` sends only a custom thinking flag while reasoning is selected;
`{"reasoning_effort":null,"reasoning":{"effort":"high"}}` uses a nested effort field.

For models that require adaptive thinking, choose reasoning and set Extra request JSON such as
`{"thinking":{"type":"adaptive"},"output_config":{"effort":"high"}}`. This is a provider-specific override,
not a capability claim about every model. Validate your endpoint's documentation. An explicit HTTP 400 naming a
rejected reasoning field retries once without reasoning fields and shows **reasoning not supported by this model**.
That suppression lasts for the session and that model/base URL. Authentication, billing and unrelated errors do
not trigger this retry. Inline completion always omits requested reasoning fields.

Provider reasoning text appears as plain selectable text in a collapsed section above the answer, streamed
separately. It has no SQL actions and never enters later conversation turns. No tool calls or thinking signatures
are replayed. Elapsed time starts at Send and stops when answer text arrives, or when a non-streaming reply ends;
it includes context preparation and network latency. Final status shows requested model/effort and any reported input/output/reasoning
tokens. Some providers return counts without readable reasoning text. Stop and the five-minute timeout apply
to both answer and reasoning.

Checked request/usage contracts against official [OpenAI Chat Completions](https://developers.openai.com/api/reference/resources/chat/subresources/completions/methods/create),
[OpenAI models](https://developers.openai.com/api/reference/resources/models/methods/list),
[DeepSeek Chat Completions](https://api-docs.deepseek.com/api/create-chat-completion/),
[Anthropic extended thinking](https://platform.claude.com/docs/en/build-with-claude/extended-thinking), and
[Anthropic models](https://platform.claude.com/docs/en/api/models/list).
Assumed: a custom compatible endpoint accepts the same fields and paths. Typed models, Extra request JSON and
the explicit rejection fallback cover differing protocols; model metadata alone does not prove reasoning support.
Provider requests do not follow redirects, keeping custom API-key headers at the configured endpoint. JSON
responses are capped at two million bytes; SSE lines/events at one million characters and answer/reasoning text
at two million characters each. Provider destination/options are captured before context lookup starts.

## Not yet run in SSMS

Automated Core checks cover markdown, token coloring, SSE framing, schema counts and SQL warnings. Local HTTP
fixtures cover both stream formats, fallback, errors, truncation and cancellation without contacting a provider.
Provider checks cover model parsing, reasoning fields, separate reasoning deltas and usage. Panel fixtures verify
typed model selection, ten-choice persistence and a collapsed reasoning area without SQL actions. Core checks
also cover slash commands, `@` mention parsing and Markdown export. Offscreen panel/dialog checks cover per-block
actions, table grids, the model drawer, starter prompts, theme resource changes and 100/125/150/200% layout. Run:

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
- Scroll native comparison while controller stays open; close comparison/editor or press Escape to cancel; Apply then undo once.
- Regenerate preserves original context; edit/resend replaces only the last exchange; Copy answer preserves Markdown.
- Discover models, refresh, type an unlisted model, switch base URL, restart SSMS and check picker/options agree.
- Off/Low/Med/High with your provider; explicit unsupported-field retry; token counts when reported; Stop during reasoning.
- Expand reasoning containing SQL: no Insert/Replace/Run actions; next request must contain answer text only.
- Review, Document and Tests on a stored procedure; `/help` and `/?` show the card without a provider request.
- Type `@` against a live connection: suggestions list tables and views; a mentioned procedure's code reaches the request.
- Export a conversation to `.md`, open it, and confirm prompts and answers appear in order without reasoning text.
- Open and close the model drawer in each theme; both pickers must match the theme the first time the drawer opens.

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
