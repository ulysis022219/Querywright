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
| AI assistant (Alt+Z) | Dockable panel: write a query, change the selection, explain, fix, optimize, ask follow-ups. Recent prompts are kept in `%LocalAppData%\Querywright\ai-prompts.json`. |
| AI: explain SQL | Explains the selection or script. |
| AI: fix errors | Compiles each GO batch with `SET NOEXEC ON` (nothing runs) and asks for a corrected script. |
| AI: optimize query | Reads the estimated plan with `SET SHOWPLAN_XML ON` (nothing runs) and asks for a faster rewrite. |
| AI: complete at caret | Inserts the model's continuation at the caret. Ctrl+Z removes it. |
| Copy MCP configuration | Copies the client configuration, including the access token. |

The panel's answer can be inserted at the caret, put over the selection, opened in a new query window, run there, or copied.

Limits: completion is a command, not grey text while you type; answers arrive whole, not streamed; Fix sees compile
errors only (a missing table or a runtime error is not found by `NOEXEC`).

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
