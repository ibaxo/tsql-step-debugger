# T-SQL Step Debugger

A **client-side** step debugger for T-SQL. Set a breakpoint, step through a script or a
stored procedure — watching variables, temp tables, and the call stack — against a real
SQL Server. No engine-side debug support required (no SSDT, no `sysadmin`).

Everything runs on the client inside a transaction that **rolls back by default**, so you
can step through real logic on a dev/test database without leaving anything behind.

It comes in two surfaces over the same debugger core:

| Surface | For | How you drive it |
|---|---|---|
| **VS Code extension** | a human, in the editor | breakpoints + F5; the Debug Adapter (DAP) host |
| **MCP server** | an AI agent (e.g. Claude Code) | tool calls over MCP; the programmatic host |

- Targets **SQL Server 2016+**. The interactive adapter ships self-contained for **Windows (x64)**.
- The full design is in [`docs/DESIGN.md`](docs/DESIGN.md); a developer reference is in
  [`docs/README.md`](docs/README.md).

---

# Using it in VS Code

Set a breakpoint in a `.sql` file, press **F5**, and step.

## Quick start

1. **Add a connection.** Click the **`$(database)` T-SQL** item in the status bar (or run
   **T-SQL Debug: Manage Connections**) and add a server + database — **Windows (integrated)**
   auth, or a **SQL login** (its password is kept in VS Code SecretStorage, never in a file).
2. **Open a `.sql` file and debug it.** Click **▷ Debug T-SQL Script** in the editor title
   bar, or press **F5**. No `launch.json` needed — you'll pick your connection the first time.
3. **Set breakpoints** in the gutter and step from there.

## While you're stopped

- **Step** — Over (`F10`), Into a called proc (`F11`), Out (`Shift+F11`), Continue (`F5`).
- **Call stack**, and a **Variables** panel: **Locals**, **Temp Tables** (browse `#temp` /
  `@table` contents), and **System** (`@@TRANCOUNT`, `XACT_STATE()`, `@@SPID`).
- **Watch** and **hover**; **Set Value** to edit a local.
- **User-defined types** — alias types (`CREATE TYPE dbo.Name FROM nvarchar(50)`) and table
  types (`DECLARE @t dbo.OrderRows`) step like any other variable; a table-type variable
  shows up under **Temp Tables**, and passing one as a table-valued parameter to a procedure
  works (that call is stepped over, not into).
- **Debug Console** (`Ctrl+Shift+Y`) — a live T-SQL REPL against the current frame, so
  `SELECT @x` and `SELECT * FROM #work` just work. Writable by default.
- **Breakpoints** — conditional, hit-count, and **logpoints**; toggle **Caught / Unhandled**
  error breaks; **Jump to Cursor** to move execution.

## Safety

- **Nothing persists** — the session rolls back when it ends. To keep changes, set
  `"commitMode": "commit"`; you then confirm a modal on Stop (that confirmation is the
  authorization). Declining, a timeout, a disconnect, or an error always rolls back.
- **Dev/test servers only** — while paused, the session holds its transaction locks open.
  The active server/database shows in the status bar the whole time.

## Debugging a T-SQL script

The **▷ Debug T-SQL Script** button and **F5** both debug the active `.sql` file with no
`launch.json` at all — script mode is the default. The file **doesn't have to be saved**: open a
new untitled buffer (or one with unsaved edits), type your T-SQL, and press F5 — the exact text
on screen is debugged in place, nothing is written to disk, and breakpoints and the step arrow
land in that same editor. Add a `launch.json` entry when you want to
tweak an option; *Run and Debug → create a launch.json file* generates exactly this, which
behaves identically to the button, with **every option at its default**:

```json
{
  "type": "tsql",
  "request": "launch",
  "name": "Debug T-SQL script",
  "mode": "script",
  "script": "${file}",
  "stopOnEntry": true,
  "commitMode": "rollback",
  "waitfor": "skip",
  "boost": false,
  "allowConsoleWrites": true,
  "executeAs": null,
  "compatLevel": 0,
  "logLevel": "normal",
  "sourceMap": [],
  "commandTimeoutSec": 300,
  "consoleTimeoutSec": 30,
  "maxConsoleRows": 200,
  "tempTablePageSize": 50,
  "displayValueChars": 256,
  "watchBudgetMs": 2000,
  "trace": false
}
```

The connection is deliberately absent: with no `server` in the config you pick a saved
connection from the **Connection Manager** at launch, which is where server, database, auth
type and login belong. You *can* pin `server`/`database`/`authType`/`sqlUser`/`encrypt`/
`options` here (CI, or a config you never want to prompt), but there is no `password` field by
design — SQL passwords live in VS Code SecretStorage, never in `launch.json`. Every option is
described in the [Configuration reference](#configuration-reference).

## Debugging a stored procedure

To debug a **deployed procedure** instead of a script, add a `launch.json` entry with
`mode: "procedure"` — shown here with **every option at its default** (`procedure` and `args`
are the procedure-mode fields that replace `script`):

```json
{
  "type": "tsql",
  "request": "launch",
  "name": "Debug my procedure",
  "mode": "procedure",
  "procedure": "dbo.MyProcedure",
  "args": { "@OrderId": "42", "@Mode": "N'FULL'" },
  "stopOnEntry": true,
  "commitMode": "rollback",
  "waitfor": "skip",
  "boost": false,
  "allowConsoleWrites": true,
  "executeAs": null,
  "compatLevel": 0,
  "logLevel": "normal",
  "sourceMap": [],
  "commandTimeoutSec": 300,
  "consoleTimeoutSec": 30,
  "maxConsoleRows": 200,
  "tempTablePageSize": 50,
  "displayValueChars": 256,
  "watchBudgetMs": 2000,
  "trace": false
}
```

`procedure` is a two/three-part name (required); `args` maps each parameter to a **T-SQL
literal** — note `N'…'` for Unicode — and defaults to `{}`. **Every parameter the procedure
declares (including `OUTPUT` parameters) must be supplied.** The connection comes from the
Connection Manager here too.

## Configuration reference

Everything goes in a `launch.json` entry (`"type": "tsql"`). Only `mode` (and `procedure`
for procedure mode) really matters; the rest have sensible defaults.

| Option | Type | Default | Description |
|---|---|---|---|
| `mode` | `script` \| `procedure` | `script` | Debug the active `.sql` file, or a deployed module. |
| `server` / `database` | string | *(pick at launch)* | Omit (recommended) to choose a saved connection (Connection Manager). |
| `script` | string | `${file}` | The `.sql` file to debug (script mode). For an unsaved buffer this is the editor URI, set automatically. |
| `scriptText` | string | — | Inline script body, run verbatim with no file read. Set automatically for unsaved/dirty buffers — you normally never write this by hand. |
| `procedure` | string | — | Two/three-part name; required for `mode: procedure`. |
| `args` | object | `{}` | Parameter → T-SQL literal, e.g. `{ "@Id": "42" }` (procedure mode). |
| `stopOnEntry` | boolean | `true` | Stop at the first statement before running. |
| `commitMode` | `rollback` \| `commit` | `rollback` | `commit` keeps changes after a confirmed Stop. |
| `authType` | `integrated` \| `sql` | `integrated` | Windows (SSPI) or a SQL login. Best set in the Connection Manager. |
| `sqlUser` | string | — | SQL login name (when `authType: sql`). The **password is never a config field** — it lives in SecretStorage; set it in the Connection Manager. |
| `encrypt` / `options` | boolean / string | `false` / — | `encrypt` = `Encrypt=Mandatory`; `options` appends a raw connection-string fragment. |
| `targetsFile` | string | *(`MSSQL_DEBUG_TARGETS`, else `${workspaceFolder}/targets.json`)* | Optional per-server metadata (`env`, connection `options`). |
| `boost` | boolean | `false` | Run whole `IF`/`WHILE` blocks as one batch under Continue (faster, less granular). |
| `waitfor` | `skip` \| `honor` | `skip` | `skip` logs `WAITFOR DELAY/TIME` instead of blocking. |
| `allowConsoleWrites` | boolean | `true` | Let the Debug Console write (DML/DDL/`SET @x`), not just `SELECT`. |
| `sourceMap` | string[] | — | Globs binding a module's server definition to your real `.sql` files (breakpoints in called procs). |
| `executeAs` | string | — | `EXECUTE AS <clause>` at start, `REVERT`ed at end. |
| `compatLevel` | `0` \| `150` \| `160` \| `170` | `0` | ScriptDom parser version. `0` = auto-detect from the server (SQL 2019 → `150`, 2022 → `160`, 2025 → `170`). |
| `logLevel` | `normal` \| `verbose` | `normal` | `verbose` also shows the debugger's own diagnostic notes (NOCOUNT, `GO`-batch, trigger heads-ups). |
| `commandTimeoutSec` | number | `300` | Per-statement timeout. |
| `consoleTimeoutSec` | number | `30` | Debug Console timeout. |
| `maxConsoleRows` | number | `200` | Debug Console row cap. |
| `tempTablePageSize` | number | `50` | Rows per page in Temp Tables. |
| `displayValueChars` | number | `256` | Max characters shown per value. |
| `watchBudgetMs` | number | `2000` | Per-stop budget for evaluating Watch expressions. |
| `trace` | boolean | `false` | Write a full adapter log (for diagnosing the debugger itself). |

**Using a locally-built adapter:** set `tsqlDbg.adapterPath` to your own `TsqlDbg.Adapter` build.

---

# Using it from an AI agent (MCP server)

`TsqlDbg.Mcp` is a [Model Context Protocol](https://modelcontextprotocol.io) server that
exposes the same debugger to an AI agent — so an assistant like Claude Code can debug a
procedure by itself: trace it, set breakpoints, step, and inspect variables. It speaks MCP
over stdio; the agent's client launches it as a subprocess. Full spec: [`docs/DESIGN.md`
§24](docs/DESIGN.md).

## Two ways an agent uses it

- **Trace first (recommended).** `trace_procedure` / `trace_script` run the target to
  completion, capture a **per-statement JSONL trace** (each statement, the variables after it,
  output, result sets, errors) to a file, and return a summary plus the path. One tool call
  answers most "why does this do X" questions — cheap, and the agent reads the file offline.
- **Interactive drill-down.** When a trace is ambiguous: `start_session`, `set_breakpoints`,
  `continue` / `step`, `get_variables`, `evaluate` — every call returns the current stop
  state (call stack, current error, transaction state), so the agent always knows where it is.

## Safety model

Because there is **no human present to consent**, the programmatic surface is stricter than
the VS Code one:

- **Default-deny allowlist.** Every session resolves the target against a `targets.json`
  allowlist; an unknown server is **refused before any connection opens** (there is no
  informed-consent fallback). Point at it with the `MSSQL_DEBUG_TARGETS` env var or `--targets`.
- **Rollback is the default, always.** Every teardown — end, idle timeout, shutdown, error —
  rolls back. Committing requires **both** `commitMode: "commit"` **and** the target's
  `allowWrites: true`; a cancelled or faulted trace never commits partial work.
- **Writes off by default.** `allowConsoleWrites` defaults `false` here; `evaluate` only reads
  unless a session opts in *and* the target allows writes.
- **Credentials never in a tool argument.** A SQL-auth password is read only from the
  `TSQLDBG_SQL_PASSWORD` env var — never a tool argument (which would land in the transcript),
  never the trace file. Integrated (Windows) auth needs no password.
- **Idle sessions self-tear-down.** A paused session holds locks; an idle timeout (default
  300s) and a max-live-session cap (default 4) bound the exposure with no human watching.

A `targets.json` is just:

```json
{ "targets": { "localhost": { "env": "dev", "allowWrites": false } } }
```

## The tools

| Group | Tools |
|---|---|
| **Lifecycle** | `start_session`, `end_session`, `list_sessions`, `get_state`, `get_stack` |
| **Stepping** | `step` (over/in/out), `continue`, `goto` |
| **Breakpoints** | `set_breakpoints` (line + optional condition/hit-count), `clear_breakpoints`, `set_exception_filters` |
| **Inspection** | `get_variables` (locals/system/temp/errorContext), `get_temp_rows`, `evaluate`, `set_variable` |
| **Trace** | `trace_procedure`, `trace_script` |

## Build and run

```bash
# from the repo root — build, or publish a self-contained exe
dotnet build src/TsqlDbg.Mcp/TsqlDbg.Mcp.csproj
dotnet publish src/TsqlDbg.Mcp -c Release -r win-x64 --self-contained   # → tsqldbg-mcp(.exe)
```

Server-level options (env or process args): `MSSQL_DEBUG_TARGETS` (or `--targets <path>`),
`--max-sessions <n>` (default 4), `--idle-timeout-sec <n>` (default 300), `--trace-dir <path>`
(where trace files go), `--trace <path>` (the host's own protocol log).

## Connecting an MCP client

Register it as an MCP server in your client. For Claude Code:

```bash
claude mcp add tsql-debugger --env MSSQL_DEBUG_TARGETS=C:\path\to\targets.json -- \
  dotnet C:\path\to\tsqldbg-mcp.dll
```

…or the equivalent client config block:

```json
{
  "mcpServers": {
    "tsql-debugger": {
      "command": "dotnet",
      "args": ["C:\\path\\to\\tsqldbg-mcp.dll"],
      "env": { "MSSQL_DEBUG_TARGETS": "C:\\path\\to\\targets.json" }
    }
  }
}
```

For SQL-auth targets, also set `TSQLDBG_SQL_PASSWORD` in `env` (never as a tool argument).

---

# Project structure

```
tsql-step-debugger/
├── extension/                # VS Code extension shell (TypeScript, esbuild) — DAP client
├── src/
│   ├── TsqlDbg.Core/         # interpreter, rewriter, state, error model — NO DAP/VS Code deps
│   ├── TsqlDbg.Adapter/      # DAP host (stdio) — the interactive surface (VS Code)
│   └── TsqlDbg.Mcp/          # MCP host (stdio) — the programmatic surface (AI agents)
├── tests/
│   ├── TsqlDbg.Core.Tests/       # unit: rewriter, interpreter (fake IStatementExecutor)
│   ├── TsqlDbg.Adapter.Tests/    # unit: DAP host
│   ├── TsqlDbg.Mcp.Tests/        # unit: MCP host + driver tests
│   └── TsqlDbg.Integration/      # integration + fidelity harness (needs a live SQL Server)
└── docs/                     # DESIGN.md (the spec), README.md (developer reference), engine-facts.md
```

`TsqlDbg.Core` holds all the debugging logic and has **no** DAP or VS Code dependency; the
adapter and the MCP server are two thin hosts over it (see [`docs/DESIGN.md`](docs/DESIGN.md)
§3 and §24).

# Building from source

**Prerequisites:** .NET 8 SDK; Node 18+ (for the extension); a reachable SQL Server for the
integration tests.

```bash
# Build everything
dotnet build TsqlDbg.sln

# Unit tests (no database needed)
dotnet test tests/TsqlDbg.Core.Tests
dotnet test tests/TsqlDbg.Adapter.Tests
dotnet test tests/TsqlDbg.Mcp.Tests

# Integration + fidelity harness (needs a SQL Server; skips cleanly if unset)
TSQLDBG_TEST_CONN="Server=localhost;Database=TsqlDbgScratch;Integrated Security=true;TrustServerCertificate=true" \
  dotnet test tests/TsqlDbg.Integration

# Publish the self-contained hosts (Windows x64)
dotnet publish src/TsqlDbg.Adapter -c Release -r win-x64 --self-contained
dotnet publish src/TsqlDbg.Mcp     -c Release -r win-x64 --self-contained

# Build the VS Code extension
cd extension && npm ci && npm run build
```

# Requirements

- **VS Code extension:** VS Code 1.85+ on Windows (x64) — the adapter is bundled, so no .NET
  runtime needed — and a reachable SQL Server 2016+ dev/test instance.
- **MCP server / building from source:** .NET 8 runtime (or SDK to build); any OS the .NET
  runtime supports.
- Debugging needs only ordinary `EXECUTE`/`SELECT` + `VIEW DEFINITION` — **no `sysadmin`**.
