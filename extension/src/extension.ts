import * as vscode from 'vscode';
import * as path from 'path';
import * as os from 'os';
import * as fs from 'fs';
import { randomUUID } from 'crypto';
import { ConnectionStore, ExternalSource, ResolvedConnection } from './connectionStore';
import { MssqlConnectionBridge, MSSQL_SOURCE_ID } from './mssqlConnections';

// DESIGN.md §17 + CLAUDE.md orientation: "extension/ — TypeScript VS Code shell (DAP wiring
// only; no logic)." Everything here is registration/wiring: resolving the adapter executable
// path, the §16/A42 Connection Manager (saved profiles → launch config, the SQL password
// injected to the adapter via a child-process env var), the §16/A40 informed-consent UI
// (active-connection status bar + one-time launch warning), and the commit modal — never
// interpreting T-SQL or touching the protocol itself.

export function activate(context: vscode.ExtensionContext): void {
	const store = new ConnectionStore(context, () => resolveAdapterPath(context));
	const mssql = new MssqlConnectionBridge();
	// Holds SQL passwords sourced from mssql for the brief window between config resolution and
	// adapter spawn (see SAFETY note on TsqlDebugConfigurationProvider). Never persisted.
	const ephemeralSecrets = new EphemeralSecrets();
	const connectionStatus = new ConnectionStatusIndicator(context, store);
	const tracePanel = new TraceRunPanel(context);
	context.subscriptions.push(
		vscode.debug.registerDebugConfigurationProvider('tsql', new TsqlDebugConfigurationProvider(store, mssql, ephemeralSecrets)),
		vscode.debug.registerDebugAdapterDescriptorFactory('tsql', new TsqlDebugAdapterDescriptorFactory(context, store, ephemeralSecrets)),
		vscode.workspace.registerTextDocumentContentProvider(TsqlSourceContentProvider.scheme, new TsqlSourceContentProvider()),
		vscode.debug.onDidReceiveDebugSessionCustomEvent(handleCommitConfirmEvent),
		// A74: the trace-run panel — renders the adapter's tsqldbg_trace* event stream.
		vscode.debug.onDidReceiveDebugSessionCustomEvent((e) => tracePanel.onCustomEvent(e)),
		tracePanel,
		vscode.debug.onDidStartDebugSession((s) => connectionStatus.onSessionStart(s)),
		vscode.debug.onDidTerminateDebugSession((s) => connectionStatus.onSessionEnd(s)),
		// A74 review MED-2: an infra-faulted trace run terminates without a summary event —
		// tell the panel so it never claims "running…" over a dead session.
		vscode.debug.onDidTerminateDebugSession((s) => tracePanel.onSessionTerminated(s)),
		// One-click "Debug T-SQL Script" — the editor-title button and Command Palette entry.
		// Starts a SCRIPT-mode session on the active .sql file with no launch.json needed; the
		// config provider below fills the connection from the Connection Manager.
		vscode.commands.registerCommand('tsqldbg.debugEditorScript', async () => {
			const editor = vscode.window.activeTextEditor;
			// A60: allow an untitled buffer even if its language isn't SQL yet — a brand-new
			// "New File" starts as plaintext, and the whole point is to debug it without first
			// saving (and thus without VS Code inferring .sql). A *saved* file still must be SQL.
			if (!editor || (editor.document.languageId !== 'sql' && !editor.document.isUntitled)) {
				void vscode.window.showInformationMessage('Open a .sql file (or an unsaved buffer) to debug it as a T-SQL script.');
				return;
			}
			await vscode.debug.startDebugging(vscode.workspace.getWorkspaceFolder(editor.document.uri), {
				type: 'tsql',
				request: 'launch',
				name: 'Debug T-SQL script',
				mode: 'script',
				...buildScriptSource(editor.document),
				// stopOnEntry deliberately unset (A69): resolveDebugConfiguration fills it from
				// tsqlDbg.defaults.*; the adapter defaults it to true when nothing sets it.
			// A60: we debug the buffer's live text (buildScriptSource passes scriptText for unsaved/dirty
			// docs), so VS Code's save-before-debug step is pure friction here — for an untitled buffer it
			// pops a Save As dialog. Suppress it; a saved-but-dirty file is read from the live buffer too.
			}, { suppressSaveBeforeStart: true });
		}),
		// A73: one-click "Trace T-SQL Script" — the §24.3 Mode A trace on the interactive
		// surface. Same source resolution as the debug button; `traceRun` flips the adapter
		// into the non-interactive trace-to-end mode, stepMode "into" because the button's
		// whole point is the everything-inlined trace (launch.json users pick their own).
		vscode.commands.registerCommand('tsqldbg.traceEditorScript', async () => {
			const editor = vscode.window.activeTextEditor;
			if (!editor || (editor.document.languageId !== 'sql' && !editor.document.isUntitled)) {
				void vscode.window.showInformationMessage('Open a .sql file (or an unsaved buffer) to trace it as a T-SQL script.');
				return;
			}
			await vscode.debug.startDebugging(vscode.workspace.getWorkspaceFolder(editor.document.uri), {
				type: 'tsql',
				request: 'launch',
				name: 'Trace T-SQL script',
				mode: 'script',
				traceRun: { stepMode: 'into' },
				...buildScriptSource(editor.document),
			}, { suppressSaveBeforeStart: true });
		}),
		// "T-SQL File" in File → New File… (file/newFile menu). Opens an untitled buffer with the
		// sql language already set, so the ▷ Debug button, breakpoints, and F5 work immediately —
		// combined with A60 the file never needs to touch disk.
		vscode.commands.registerCommand('tsqldbg.newSqlFile', async () => {
			const doc = await vscode.workspace.openTextDocument({ language: 'sql' });
			await vscode.window.showTextDocument(doc);
		}),
		vscode.commands.registerCommand('tsqldbg.connections.manage', async () => {
			await store.manage();
			connectionStatus.refreshIdle();
		}),
		vscode.commands.registerCommand('tsqldbg.connections.select', async () => {
			await store.pickActive();
			connectionStatus.refreshIdle();
		}),
		connectionStatus,
	);

	// Publish the bundled MCP server (DESIGN §24, M11) to VS Code's agent mode so installing
	// this extension makes the debugger's programmatic surface discoverable to Copilot / MCP
	// clients with no hand-written mcp.json. Pure wiring; the server owns all behavior.
	registerMcpServerProvider(context);
}

// VS Code MCP provider API (finalized 1.101): hand VS Code the path to the bundled,
// self-contained MCP host (extension/bin-mcp/<rid>/tsqldbg-mcp) plus the workspace targets.json
// allowlist location, so agent mode discovers and launches it automatically. CLAUDE.md
// "extension/ … no logic": we only wire the executable + env; §24.1 default-deny still governs
// (no targets file ⇒ the server refuses start_session). Bundled binary = same trust model and
// same version as the adapter shipped beside it.
function registerMcpServerProvider(context: vscode.ExtensionContext): void {
	// Degrade quietly on VS Code older than the extension's engine floor rather than throwing
	// during activate (a mismatched host, or a future API rename).
	const lm = vscode.lm as { registerMcpServerDefinitionProvider?: Function } | undefined;
	if (typeof lm?.registerMcpServerDefinitionProvider !== 'function') {
		return;
	}
	const provider = vscode.lm.registerMcpServerDefinitionProvider('tsqldbg.mcpServer', {
		provideMcpServerDefinitions: async (): Promise<vscode.McpServerDefinition[]> => {
			const command = resolveMcpServerPath(context);
			// Only advertise the server when its binary is actually present: a from-source dev
			// build (or a platform the VSIX didn't bundle) has no bin-mcp payload, and surfacing
			// a server that can't launch is worse than surfacing none. A dev override still wins.
			if (!fs.existsSync(command)) {
				return [];
			}
			const env: Record<string, string> = {};
			const targets = resolveWorkspaceTargetsFile();
			if (targets) {
				// DESIGN §24.9: the allowlist location. When absent we set nothing — VS Code
				// merges our env over the parent, so a machine-wide MSSQL_DEBUG_TARGETS still
				// flows through, and failing that the server default-denies. Never fabricated.
				env.MSSQL_DEBUG_TARGETS = targets;
			}
			return [
				new vscode.McpStdioServerDefinition(
					'T-SQL Step Debugger',
					command,
					[],
					env,
					String(context.extension.packageJSON.version),
				),
			];
		},
		resolveMcpServerDefinition: (server) => server,
	});
	context.subscriptions.push(provider);
}

// Mirror of resolveAdapterPath for the MCP host binary. The packaging step publishes it
// self-contained into extension/bin-mcp/<rid>/ — a folder SEPARATE from the adapter's bin/<rid>/
// so the two self-contained .NET runtimes never overwrite each other. Dev override: tsqlDbg.mcpPath.
function resolveMcpServerPath(context: vscode.ExtensionContext): string {
	const devOverride = vscode.workspace.getConfiguration('tsqlDbg').get<string>('mcpPath');
	if (devOverride && devOverride.length > 0) {
		return devOverride;
	}
	const exeName = os.platform() === 'win32' ? 'tsqldbg-mcp.exe' : 'tsqldbg-mcp';
	const mcpPath = path.join(context.extensionPath, 'bin-mcp', adapterRid(), exeName);
	// As with the adapter, a VSIX zipped on Windows drops the Unix executable bit — restore it
	// before VS Code launches the server (no-op if absent; the missing-binary case is caught by
	// the existsSync guard in the provider above).
	if (os.platform() !== 'win32') {
		try {
			fs.chmodSync(mcpPath, 0o755);
		} catch {
			/* best-effort */
		}
	}
	return mcpPath;
}

// The workspace's targets.json allowlist, if present in the first workspace folder — the same
// file the adapter resolves as ${workspaceFolder}/targets.json (§4-step-1). Passed to the MCP
// server as MSSQL_DEBUG_TARGETS so an agent's start_session has an allowlist with no manual env
// setup. No workspace or no file ⇒ undefined, and the server's §24.1 default-deny does the rest.
function resolveWorkspaceTargetsFile(): string | undefined {
	// First workspace folder only: a background MCP server has no "current file" to key a
	// multi-root choice off, so a targets.json in a non-first root isn't auto-found (the user
	// falls back to a machine-wide MSSQL_DEBUG_TARGETS). Safe by construction — the worst case
	// is over-denial (§24.1), never over-permission.
	const folder = vscode.workspace.workspaceFolders?.[0];
	if (!folder) {
		return undefined;
	}
	const candidate = path.join(folder.uri.fsPath, 'targets.json');
	return fs.existsSync(candidate) ? candidate : undefined;
}

// DESIGN.md §2/§17: the self-contained adapter exe, bundled at extension/bin/<rid>/ by the
// packaging step (overridable by the dev setting for F5-debugging the adapter). Shared by the
// descriptor factory (the debug session) and the Connection Manager (the metadata CLI).
function resolveAdapterPath(context: vscode.ExtensionContext): string {
	const devOverride = vscode.workspace.getConfiguration('tsqlDbg').get<string>('adapterPath');
	if (devOverride && devOverride.length > 0) {
		return devOverride;
	}
	const exeName = os.platform() === 'win32' ? 'TsqlDbg.Adapter.exe' : 'TsqlDbg.Adapter';
	const adapterPath = path.join(context.extensionPath, 'bin', adapterRid(), exeName);
	// A VSIX zipped on Windows drops the Unix executable bit, so a Linux/macOS adapter would not
	// be launchable as shipped. Restore it before we spawn (no-op if already +x; a dev override
	// or an unbundled platform simply surfaces as a spawn error). Skipped on Windows.
	if (os.platform() !== 'win32') {
		try {
			fs.chmodSync(adapterPath, 0o755);
		} catch {
			/* best-effort; the missing-binary case is reported when the adapter is spawned */
		}
	}
	return adapterPath;
}

// The .NET runtime-identifier subfolder the packaging step publishes the self-contained adapter
// into. A platform-specific VSIX (built with `vsce package --target`) carries only its own
// bin/<rid>/ payload, so this picks the one matching the host and the adapter resolves on every OS.
function adapterRid(): string {
	const arch = process.arch === 'arm64' ? 'arm64' : 'x64';
	switch (os.platform()) {
		case 'win32':
			return `win-${arch}`;
		case 'linux':
			return `linux-${arch}`;
		case 'darwin':
			return `osx-${arch}`;
		default:
			// Unsupported host: fall back to the Windows layout so the spawn fails with a clear
			// ENOENT rather than silently resolving somewhere wrong.
			return `win-${arch}`;
	}
}

// DESIGN.md §16/§5.2 (M7 commit-modal): DAP has no built-in "ask the user something"
// mechanism, so the adapter sends a custom `tsqldbg_commitConfirm` event on the EXPLICIT
// terminate path (never disconnect/error) when commitMode:"commit" is armed, and expects a
// reply via a custom `tsqldbg_commitDecision` request on the SAME session. A modal warning
// (blocking, one action item) is the deliberate choice: committing is durable and a passive
// notification is too easy to miss while the adapter's own 60s timeout is ticking. No reply at
// all (an older extension build without this handler, or the request throwing) falls back to
// the adapter's own timeout -> rollback -- pure wiring, no logic beyond "ask, relay the answer".
async function handleCommitConfirmEvent(e: vscode.DebugSessionCustomEvent): Promise<void> {
	if (e.event !== 'tsqldbg_commitConfirm') {
		return;
	}

	const server = e.body?.server ?? 'unknown server';
	const database = e.body?.database ?? 'unknown database';
	const env = e.body?.env ?? 'unknown';

	const choice = await vscode.window.showWarningMessage(
		`Commit this T-SQL debug session's changes to ${server}/${database} (env: ${env})? ` +
			'This is durable — all writes made so far become permanent.',
		{ modal: true },
		'Commit',
	);

	try {
		await e.session.customRequest('tsqldbg_commitDecision', { commit: choice === 'Commit' });
	} catch {
		// Best-effort: the adapter's own 60s timeout covers a reply that never arrives.
	}
}

// DESIGN.md §13/§5.2 (A22): module sources without a matching workspace file are served as
// read-only virtual documents. Content comes entirely from the adapter's `tsqldbg_source`
// custom request — this provider is a dumb pipe (CLAUDE.md "extension/ ... no logic").
class TsqlSourceContentProvider implements vscode.TextDocumentContentProvider {
	static readonly scheme = 'tsqldbg';

	async provideTextDocumentContent(uri: vscode.Uri): Promise<string> {
		const session = vscode.debug.activeDebugSession;
		if (!session) {
			return '-- tsqldbg: no active debug session.';
		}

		try {
			const response = await session.customRequest('tsqldbg_source', { path: uri.toString() });
			return typeof response?.content === 'string' ? response.content : '-- tsqldbg: no content returned.';
		} catch (err) {
			const message = err instanceof Error ? err.message : String(err);
			return `-- tsqldbg: ${message}`;
		}
	}
}

// DESIGN §17 (A60): resolve the script-mode source of an editor document into launch fields.
// For an unsaved/untitled buffer — or a saved file with pending edits — pass the LIVE buffer
// text as `scriptText` so the adapter debugs exactly what's on screen without a disk read (or a
// temp file). `script` still carries a path/URI the client can map the step arrow onto: the real
// filesystem path for a saved file (dirty or not), or the untitled: URI for an unsaved one.
function buildScriptSource(document: vscode.TextDocument): { script: string; scriptText?: string } {
	if (document.isUntitled) {
		return { script: document.uri.toString(), scriptText: document.getText() };
	}
	if (document.isDirty) {
		return { script: document.uri.fsPath, scriptText: document.getText() };
	}
	return { script: document.uri.fsPath };
}

export function deactivate(): void {
	// Nothing to clean up: the adapter process owns its own connection/transaction lifecycle
	// (DESIGN.md §4 teardown) and exits on `disconnect`.
}

// A one-shot, in-memory registry for SQL passwords sourced from the mssql extension. A saved
// profile's password lives in SecretStorage keyed by its profile id; an mssql-sourced connection
// has no profile, so its password is stashed here under a random token that the (traceable) DAP
// config carries in our place — the descriptor factory redeems the token for the password and
// injects it as an env var. The password itself never enters the config, launch.json, or --trace.
class EphemeralSecrets {
	private readonly map = new Map<string, string>();

	put(secret: string): string {
		const token = randomUUID();
		this.map.set(token, secret);
		return token;
	}

	take(token: string): string | undefined {
		const value = this.map.get(token);
		this.map.delete(token);
		return value;
	}
}

// DESIGN §17 (A69): the only launch keys whose default may come from the tsqlDbg.defaults.*
// user/workspace settings. commitMode and allowConsoleWrites are deliberately NOT here — a
// sticky, invisible settings default must never change the session's safety posture (§16).
const SETTINGS_DEFAULTABLE_KEYS = [
	'stopOnEntry',
	'waitfor',
	'boost',
	'maxConsoleRows',
	'tempTablePageSize',
	'displayValueChars',
	'watchBudgetMs',
	'logLevel',
] as const;

// DESIGN §17 (A69): fill launch keys the config leaves unset from tsqlDbg.defaults.*.
// inspect(), not get(): only a value the user explicitly set applies (folder > workspace >
// user). The `default`s declared in package.json are Settings-UI display only, so the
// adapter's LaunchConfig stays the single behavioral source of defaults for anything unset.
function applySettingsDefaults(config: vscode.DebugConfiguration): void {
	const defaults = vscode.workspace.getConfiguration('tsqlDbg.defaults');
	for (const key of SETTINGS_DEFAULTABLE_KEYS) {
		if (config[key] !== undefined) {
			continue; // an explicit launch.json value always wins
		}
		const info = defaults.inspect(key);
		const value = info?.workspaceFolderValue ?? info?.workspaceValue ?? info?.globalValue;
		if (value !== undefined) {
			config[key] = value;
		}
	}
}

// DESIGN §16 (A42, M10): when the launch config doesn't pin a server, resolve it — silently from
// the mssql extension's active-editor connection when available, else from a saved connection
// profile (the Connection Manager, which now also offers a "pick from mssql" entry). A pinned
// server/database is passed through untouched (power users / CI). The password is NEVER placed in
// the config — the descriptor factory injects it into the adapter via env (§4/A41): from
// SecretStorage for a saved profile, or from the ephemeral registry for an mssql connection. The
// workspaceFolder is still injected so the adapter can resolve ${workspaceFolder}/targets.json.
class TsqlDebugConfigurationProvider implements vscode.DebugConfigurationProvider {
	constructor(
		private readonly store: ConnectionStore,
		private readonly mssql: MssqlConnectionBridge,
		private readonly secrets: EphemeralSecrets,
	) {}

	async resolveDebugConfiguration(
		folder: vscode.WorkspaceFolder | undefined,
		config: vscode.DebugConfiguration,
	): Promise<vscode.DebugConfiguration | undefined> {
		// Zero-config: F5 with no launch.json on a .sql file — VS Code calls us with an empty
		// config. Default to SCRIPT mode on the active file so opening a .sql and pressing F5
		// "just works" (procedure mode is an explicit config; see the README).
		if (!config.type && !config.request && !config.name) {
			const editor = vscode.window.activeTextEditor;
			// A60: an unsaved buffer is a valid F5 target even before its language is SQL.
			if (editor && (editor.document.languageId === 'sql' || editor.document.isUntitled)) {
				config.type = 'tsql';
				config.request = 'launch';
				config.name = 'Debug T-SQL script';
				config.mode = 'script';
				// stopOnEntry deliberately left unset (A69): the settings-defaults chain below
				// applies, and the adapter defaults it to true when nothing sets it.
			}
		}
		if (config.type !== 'tsql') {
			return config; // not a T-SQL session — leave it for whoever owns it
		}

		// A69: settings-supplied defaults — only for keys this launch config leaves unset.
		applySettingsDefaults(config);

		// Script is the default mode (procedure mode is opt-in via an explicit `procedure`).
		if (!config.mode) {
			config.mode = config.procedure ? 'procedure' : 'script';
		}
		// Script mode defaults to the document you're editing. A60: an unsaved/untitled (or
		// dirty) buffer contributes its live text as `scriptText` so it debugs without saving.
		if (config.mode === 'script' && !config.script) {
			const document = vscode.window.activeTextEditor?.document;
			if (document) {
				const source = buildScriptSource(document);
				config.script = source.script;
				if (source.scriptText !== undefined) {
					config.scriptText = source.scriptText;
				}
			} else {
				config.script = '${file}';
			}
		}

		if (folder && !config.workspaceFolder) {
			config.workspaceFolder = folder.uri.fsPath;
		}

		// DESIGN §17 (A74): in VS Code a trace run renders in the T-SQL Trace panel by
		// default — inject view:"panel" into any traceRun launch that doesn't pin a view
		// (objectifying `traceRun: true` first). The ADAPTER's own default stays "console"
		// so a bare DAP client is never silently mute; `view: "console"` in launch.json
		// opts back into the Debug Console line stream.
		if (config.traceRun === true) {
			config.traceRun = {};
		}
		// === undefined, not falsy (A74 review): an explicit-but-empty view must reach the
		// adapter untouched so both surfaces judge the same value the same way.
		if (config.traceRun && typeof config.traceRun === 'object' && config.traceRun.view === undefined) {
			config.traceRun.view = 'panel';
		}

		if (!config.server) {
			// Silent auto-use: if the active .sql editor is already connected in the mssql
			// extension, debug against that same instance with no picker (opt out via the
			// `tsqlDbg.mssql.useActiveEditorConnection` setting). The first ever attempt triggers
			// mssql's own one-time approve/deny prompt; a denial just falls through below.
			const autoUse = vscode.workspace
				.getConfiguration('tsqlDbg')
				.get<boolean>('mssql.useActiveEditorConnection', true);
			let conn = autoUse ? await this.mssql.resolveActiveEditor() : undefined;

			if (!conn) {
				// Manual choice. When mssql is installed, lead with its own connection picker (its
				// full connection list) — mssql exposes no enumerable list to fold into our own
				// quickpick, only its native picker. Set `tsqlDbg.mssql.useConnectionPicker` false to
				// lead with the debugger's own Connection Manager instead, with mssql offered as a
				// secondary entry. Either way, backing out of one falls through to the other, so a
				// profile targeting a server mssql doesn't know about is never stranded.
				const mssqlAvailable = this.mssql.isAvailable();
				const useMssqlPicker =
					mssqlAvailable &&
					vscode.workspace.getConfiguration('tsqlDbg').get<boolean>('mssql.useConnectionPicker', true);
				if (useMssqlPicker) {
					conn = await this.mssql.pick();
				}
				if (!conn) {
					// mssql leads → no in-chooser entry (we just came from it); debugger leads → offer
					// mssql as a secondary "Pick from SQL Server (mssql)…" entry.
					const externalSources: ExternalSource[] =
						mssqlAvailable && !useMssqlPicker
							? [
								{
									id: MSSQL_SOURCE_ID,
									label: '$(plug) Pick from SQL Server (mssql)…',
									detail: 'Use a connection configured in the Microsoft mssql extension',
									resolve: () => this.mssql.pick(),
								},
							  ]
							: [];
					conn = await this.store.resolveForLaunch(externalSources);
				}
			}
			if (!conn) {
				return undefined; // cancelled — abort the launch quietly (VS Code convention)
			}
			this.applyResolvedConnection(config, conn);
		}

		if (!config.database) {
			const database = await vscode.window.showInputBox({
				title: `T-SQL debug: database on ${config.server}`,
				prompt: 'Database to debug in.',
				ignoreFocusOut: true,
			});
			if (!database) {
				return undefined;
			}
			config.database = database.trim();
		}

		// DESIGN §16/A40: fold the active connection into the session name so the CALL STACK
		// header always shows where you are stepping (the status bar shows it too).
		if (typeof config.name === 'string' && config.server) {
			const where = config.database ? `${config.server}/${config.database}` : String(config.server);
			if (!config.name.includes(where)) {
				config.name = `${config.name} — ${where}`;
			}
		}

		return config;
	}

	// Fold resolved launch fields into the config. The SQL password (saved-profile → SecretStorage,
	// or mssql → inline) is routed out-of-band so it never lands in the traceable config: a saved
	// profile leaves a profile-id ref; an mssql connection stashes the password in the ephemeral
	// registry and leaves only a one-shot token.
	private applyResolvedConnection(config: vscode.DebugConfiguration, conn: ResolvedConnection): void {
		config.server = conn.server;
		if (!config.database) {
			config.database = conn.database;
		}
		config.authType = conn.authType;
		if (conn.sqlUser) {
			config.sqlUser = conn.sqlUser;
		}
		config.encrypt = conn.encrypt;
		if (conn.options) {
			config.options = conn.options;
		}
		if (conn.profileId) {
			config.__tsqldbgProfileId = conn.profileId;
		}
		if (conn.password !== undefined) {
			config.__tsqldbgSecretToken = this.secrets.put(conn.password);
		}
	}
}

// DESIGN §17 (A74): the T-SQL Trace panel — a webview that renders the adapter's
// tsqldbg_traceStart/Step/Summary custom events (a trace run with view:"panel"). Dumb
// renderer by design (CLAUDE.md "extension/ … no logic"): every payload is the adapter's
// §24.8 line verbatim; this class only relays messages and services navigation clicks.
// One panel, reused across runs (a new tsqldbg_traceStart resets it).
class TraceRunPanel implements vscode.Disposable {
	private panel: vscode.WebviewPanel | undefined;
	// Events that arrive before the webview's script posts {type:'ready'} — messages
	// posted into a still-loading webview are silently dropped, so buffer and flush.
	private pending: unknown[] = [];
	private ready = false;
	// A74 review MED-3: the panel renders ONE run — the debug session whose
	// tsqldbg_traceStart arrived last. Step/summary events from any other session
	// (two parallel trace runs) are dropped instead of interleaving into one grid.
	private activeSessionId: string | undefined;
	private sawSummary = false;

	constructor(private readonly context: vscode.ExtensionContext) {}

	onCustomEvent(e: vscode.DebugSessionCustomEvent): void {
		if (e.session.type !== 'tsql') {
			return;
		}
		switch (e.event) {
			case 'tsqldbg_traceStart':
				this.activeSessionId = e.session.id;
				this.sawSummary = false;
				this.begin();
				this.post({ type: 'start', payload: e.body });
				break;
			case 'tsqldbg_traceStep':
				if (e.session.id === this.activeSessionId) {
					this.post({ type: 'step', payload: e.body });
				}
				break;
			case 'tsqldbg_traceSummary':
				if (e.session.id === this.activeSessionId) {
					this.sawSummary = true;
					this.post({ type: 'summary', payload: e.body });
				}
				break;
		}
	}

	// A74 review MED-2: a trace that dies on infrastructure (connection lost, the trace
	// file unwritable) ends the session via the generic fault path, which sends no
	// summary event — without this the panel's status badge says "running…" forever.
	// The Debug Console has the real story; the panel just needs to stop claiming life.
	onSessionTerminated(session: vscode.DebugSession): void {
		if (session.type === 'tsql' && session.id === this.activeSessionId && !this.sawSummary) {
			this.post({ type: 'ended' });
		}
	}

	private begin(): void {
		if (this.panel) {
			this.panel.reveal(undefined, /* preserveFocus */ true);
			return;
		}
		this.panel = vscode.window.createWebviewPanel(
			'tsqldbgTrace',
			'T-SQL Trace',
			{ viewColumn: vscode.ViewColumn.Beside, preserveFocus: true },
			{
				enableScripts: true,
				// The panel outlives the (short) debug session and the user will tab away —
				// keep the streamed rows alive instead of re-rendering from nothing.
				retainContextWhenHidden: true,
				localResourceRoots: [vscode.Uri.joinPath(this.context.extensionUri, 'media')],
			},
		);
		this.ready = false;
		this.panel.onDidDispose(() => {
			this.panel = undefined;
			this.ready = false;
			this.pending = [];
		});
		this.panel.webview.onDidReceiveMessage((m) => void this.onWebviewMessage(m));
		this.panel.webview.html = this.buildHtml(this.panel.webview);
	}

	private post(message: unknown): void {
		if (!this.panel) {
			return;
		}
		if (!this.ready) {
			this.pending.push(message);
			return;
		}
		void this.panel.webview.postMessage(message);
	}

	private async onWebviewMessage(m: { type?: string; path?: string; line?: number }): Promise<void> {
		if (m.type === 'ready') {
			this.ready = true;
			const queued = this.pending;
			this.pending = [];
			for (const message of queued) {
				void this.panel?.webview.postMessage(message);
			}
			return;
		}
		if (m.type === 'navigate' && typeof m.path === 'string') {
			await this.navigate(m.path, typeof m.line === 'number' ? m.line : 1);
			return;
		}
		if (m.type === 'openTraceFile' && typeof m.path === 'string') {
			try {
				const doc = await vscode.workspace.openTextDocument(vscode.Uri.file(m.path));
				await vscode.window.showTextDocument(doc, { preview: true });
			} catch {
				void vscode.window.showWarningMessage(`Could not open trace file: ${m.path}`);
			}
			return;
		}
	}

	// The adapter's `source.path` is either a filesystem path or a document URI (A60: an
	// unsaved buffer's untitled: URI). A drive-letter path ("C:\…") must not be mistaken
	// for a URI scheme.
	private async navigate(rawPath: string, line: number): Promise<void> {
		try {
			const isDrivePath = /^[a-zA-Z]:[\\/]/.test(rawPath);
			const hasScheme = /^[a-z][a-z0-9+.-]*:/i.test(rawPath);
			const uri = !isDrivePath && hasScheme ? vscode.Uri.parse(rawPath) : vscode.Uri.file(rawPath);
			const doc = await vscode.workspace.openTextDocument(uri);
			const editor = await vscode.window.showTextDocument(doc, { viewColumn: vscode.ViewColumn.One });
			const pos = new vscode.Position(Math.max(0, line - 1), 0);
			editor.selection = new vscode.Selection(pos, pos);
			editor.revealRange(new vscode.Range(pos, pos), vscode.TextEditorRevealType.InCenterIfOutsideViewport);
		} catch {
			void vscode.window.showWarningMessage(`Could not open ${rawPath}.`);
		}
	}

	private buildHtml(webview: vscode.Webview): string {
		const script = webview.asWebviewUri(vscode.Uri.joinPath(this.context.extensionUri, 'media', 'tracePanel.js'));
		const style = webview.asWebviewUri(vscode.Uri.joinPath(this.context.extensionUri, 'media', 'tracePanel.css'));
		const nonce = randomUUID().replace(/-/g, '');
		return `<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="UTF-8">
<meta http-equiv="Content-Security-Policy"
	content="default-src 'none'; style-src ${webview.cspSource}; script-src 'nonce-${nonce}';">
<meta name="viewport" content="width=device-width, initial-scale=1.0">
<link href="${style}" rel="stylesheet">
<title>T-SQL Trace</title>
</head>
<body>
<div id="app">
	<div id="meta"><input id="filter" type="text" placeholder="Filter steps…"></div>
	<div id="rows"><div id="empty">Waiting for a trace run…</div></div>
	<div id="summary"></div>
</div>
<script nonce="${nonce}" src="${script}"></script>
</body>
</html>`;
	}

	dispose(): void {
		this.panel?.dispose();
	}
}

// DESIGN §16 (A40/A42): the interactive surface is informed-consent, not an allowlist gate, so
// the human must always see which connection is active / which server a session is on, and be
// warned once about the risk. Pure UI wiring (CLAUDE.md "extension/ ... no logic"). Clicking the
// item switches the active connection.
class ConnectionStatusIndicator implements vscode.Disposable {
	private static readonly suppressWarningKey = 'tsqldbg.suppressLaunchWarning';
	private readonly item: vscode.StatusBarItem;
	private readonly active = new Set<string>();

	constructor(private readonly context: vscode.ExtensionContext, private readonly store: ConnectionStore) {
		this.item = vscode.window.createStatusBarItem(vscode.StatusBarAlignment.Left, 100);
		this.item.command = 'tsqldbg.connections.select';
		this.refreshIdle();
		this.item.show();
	}

	/** Idle text: the active saved connection (click to change). */
	refreshIdle(): void {
		if (this.active.size > 0) {
			return; // a live session is showing instead
		}
		const profile = this.store.getActive();
		this.item.text = profile ? `$(database) ${profile.name}` : '$(database) T-SQL: No connection';
		this.item.tooltip = profile
			? `T-SQL debug connection: ${profile.name} — click to change`
			: 'T-SQL debug: click to add a connection';
	}

	onSessionStart(session: vscode.DebugSession): void {
		if (session.type !== 'tsql') {
			return;
		}
		this.active.add(session.id);

		const server = typeof session.configuration.server === 'string' ? session.configuration.server : 'unknown server';
		const database = typeof session.configuration.database === 'string' ? session.configuration.database : '';
		const where = database ? `${server}/${database}` : server;

		this.item.text = `$(zap) TsqlDbg · ${where}`;
		this.item.tooltip = `T-SQL debug session on ${where}. Stepping holds the transaction's locks while paused — use dev/test servers.`;

		void this.maybeWarn(where);
	}

	onSessionEnd(session: vscode.DebugSession): void {
		if (session.type !== 'tsql') {
			return;
		}
		this.active.delete(session.id);
		if (this.active.size === 0) {
			this.refreshIdle();
		}
	}

	// One-time, dismissible launch warning (the "don't show again" Ivan asked for). The
	// dismissal is machine-level (globalState), deliberately NOT a launch.json field — disabling
	// a safety notice must not be a side effect of editing a shared config.
	private async maybeWarn(where: string): Promise<void> {
		if (this.context.globalState.get<boolean>(ConnectionStatusIndicator.suppressWarningKey)) {
			return;
		}
		const dontShowAgain = "Don't show again";
		const choice = await vscode.window.showWarningMessage(
			`Debugging T-SQL on ${where}. A step-debugger holds the session's transaction — and its locks — open while you are paused; use dev/test servers, not production.`,
			dontShowAgain,
		);
		if (choice === dontShowAgain) {
			await this.context.globalState.update(ConnectionStatusIndicator.suppressWarningKey, true);
		}
	}

	dispose(): void {
		this.item.dispose();
	}
}

// DESIGN.md §17: "The DebugAdapterDescriptorFactory returns a DebugAdapterExecutable pointing at
// the self-contained adapter exe." §16/§4 (A41): for a SQL-auth launch it injects the password
// from SecretStorage as a child-process env var (TSQLDBG_SQL_PASSWORD) — never a DAP launch arg,
// never traced.
class TsqlDebugAdapterDescriptorFactory implements vscode.DebugAdapterDescriptorFactory {
	constructor(
		private readonly context: vscode.ExtensionContext,
		private readonly store: ConnectionStore,
		private readonly secrets: EphemeralSecrets,
	) {}

	async createDebugAdapterDescriptor(
		session: vscode.DebugSession,
		_executable: vscode.DebugAdapterExecutable | undefined,
	): Promise<vscode.DebugAdapterDescriptor> {
		const adapterPath = resolveAdapterPath(this.context);

		const args: string[] = [];
		if (session.configuration.trace) {
			const traceFile = path.join(this.context.logUri.fsPath, `tsqldbg-trace-${Date.now()}.jsonl`);
			args.push('--trace', traceFile);
		}

		const options: vscode.DebugAdapterExecutableOptions = {};
		if (session.configuration.authType === 'sql') {
			// Two secret sources: a saved profile (SecretStorage, keyed by id) or an mssql
			// connection (the ephemeral registry, keyed by a one-shot token). The token wins if
			// both are somehow present — it's the more specific, single-launch reference.
			const token = session.configuration.__tsqldbgSecretToken;
			const profileId = session.configuration.__tsqldbgProfileId;
			let password: string | undefined;
			if (typeof token === 'string') {
				password = this.secrets.take(token);
			} else if (typeof profileId === 'string') {
				password = await this.store.getPassword(profileId);
			}
			if (password) {
				// Merged with the parent environment by VS Code — adds only this one variable.
				options.env = { TSQLDBG_SQL_PASSWORD: password };
			}
		}

		return new vscode.DebugAdapterExecutable(adapterPath, args, options);
	}
}
