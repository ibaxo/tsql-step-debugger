# T-SQL Step Debugger

Set a breakpoint in a `.sql` file, press **F5**, and step through your T-SQL — watching
variables, temp tables, and the call stack — against a real SQL Server. No engine-side
debug support required (no SSDT, no `sysadmin`).

Each statement runs on the client inside a transaction that **rolls back by default**, so
you can step through real logic on a dev/test database without leaving anything behind.

> **Preview** — ships a self-contained **Windows (x64)** adapter; targets **SQL Server 2016+**.

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
`launch.json` at all — script mode is the default. To pin the settings in a config, add a
`launch.json` entry; here is the script configuration with **every option at its default**:

```json
{
  "type": "tsql",
  "request": "launch",
  "name": "Debug T-SQL script",
  "mode": "script",
  "script": "${file}",
  "server": "",
  "database": "",
  "authType": "integrated",
  "sqlUser": "",
  "encrypt": false,
  "options": "",
  "stopOnEntry": true,
  "commitMode": "rollback",
  "waitfor": "skip",
  "boost": false,
  "allowConsoleWrites": true,
  "executeAs": null,
  "compatLevel": 150,
  "logLevel": "normal",
  "targetsFile": "${workspaceFolder}/targets.json",
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

You *can* set `server`/`database`/`authType`/`sqlUser` here, but the **Connection Manager**
is the recommended place: leave them blank to pick a saved connection at launch. There is no
`password` field by design — SQL passwords live in VS Code SecretStorage, never in
`launch.json`. Every option is described in the [Configuration reference](#configuration-reference).

## Debugging a stored procedure

To debug a **deployed procedure** instead of a script, add a `launch.json` entry
(Run and Debug → *create a launch.json file*) with `mode: "procedure"` — shown here with
**every option at its default** (`procedure` and `args` are the procedure-mode fields that
replace `script`):

```json
{
  "type": "tsql",
  "request": "launch",
  "name": "Debug my procedure",
  "mode": "procedure",
  "procedure": "dbo.MyProcedure",
  "args": { "@OrderId": "42", "@Mode": "N'FULL'" },
  "server": "",
  "database": "",
  "authType": "integrated",
  "sqlUser": "",
  "encrypt": false,
  "options": "",
  "stopOnEntry": true,
  "commitMode": "rollback",
  "waitfor": "skip",
  "boost": false,
  "allowConsoleWrites": true,
  "executeAs": null,
  "compatLevel": 150,
  "logLevel": "normal",
  "targetsFile": "${workspaceFolder}/targets.json",
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
literal** — note `N'…'` for Unicode — and defaults to `{}`. Leave `server`/`database` blank
to pick a saved connection at launch.

## Configuration reference

Everything goes in a `launch.json` entry (`"type": "tsql"`). Only `mode` (and `procedure`
for procedure mode) really matters; the rest have sensible defaults.

| Option | Type | Default | Description |
|---|---|---|---|
| `mode` | `script` \| `procedure` | `script` | Debug the active `.sql` file, or a deployed module. |
| `server` / `database` | string | *(pick at launch)* | Omit to choose a saved connection (Connection Manager). |
| `script` | string | `${file}` | The `.sql` file to debug (script mode). |
| `procedure` | string | — | Two/three-part name; required for `mode: procedure`. |
| `args` | object | `{}` | Parameter → T-SQL literal, e.g. `{ "@Id": "42" }` (procedure mode). |
| `stopOnEntry` | boolean | `true` | Stop at the first statement before running. |
| `commitMode` | `rollback` \| `commit` | `rollback` | `commit` keeps changes after a confirmed Stop. |
| `authType` | `integrated` \| `sql` | `integrated` | Windows (SSPI) or a SQL login. Best set in the Connection Manager. |
| `sqlUser` | string | — | SQL login name (when `authType: sql`). The **password is never a config field** — it lives in SecretStorage; set it in the Connection Manager. |
| `encrypt` / `options` | boolean / string | `false` / — | `encrypt` = `Encrypt=Mandatory`; `options` appends a raw connection-string fragment. |
| `targetsFile` | string | `${workspaceFolder}/targets.json` | Optional per-server metadata (`env`, connection `options`). |
| `boost` | boolean | `false` | Run whole `IF`/`WHILE` blocks as one batch under Continue (faster, less granular). |
| `waitfor` | `skip` \| `honor` | `skip` | `skip` logs `WAITFOR DELAY/TIME` instead of blocking. |
| `allowConsoleWrites` | boolean | `true` | Let the Debug Console write (DML/DDL/`SET @x`), not just `SELECT`. |
| `sourceMap` | string[] | — | Globs binding a module's server definition to your real `.sql` files (breakpoints in called procs). |
| `executeAs` | string | — | `EXECUTE AS <clause>` at start, `REVERT`ed at end. |
| `compatLevel` | `150` \| `160` \| `170` | `150` | ScriptDom parser version. |
| `logLevel` | `normal` \| `verbose` | `normal` | `verbose` also shows the debugger's own diagnostic notes (NOCOUNT, `GO`-batch, trigger heads-ups). |
| `commandTimeoutSec` | number | `300` | Per-statement timeout. |
| `consoleTimeoutSec` | number | `30` | Debug Console timeout. |
| `maxConsoleRows` | number | `200` | Debug Console row cap. |
| `tempTablePageSize` | number | `50` | Rows per page in Temp Tables. |
| `displayValueChars` | number | `256` | Max characters shown per value. |
| `watchBudgetMs` | number | `2000` | Per-stop budget for evaluating Watch expressions. |
| `trace` | boolean | `false` | Write a full adapter log (for diagnosing the debugger itself). |

**Requirements:** VS Code 1.85+ on Windows (x64) — the adapter is bundled, so no .NET
needed — and a reachable SQL Server 2016+ dev/test instance. Debugging needs only ordinary
`EXECUTE`/`SELECT` + `VIEW DEFINITION`; no `sysadmin`.

**Using a locally-built adapter:** set `tsqlDbg.adapterPath` to your own `TsqlDbg.Adapter` build.
