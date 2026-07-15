import * as vscode from 'vscode';
import type { AuthType, ResolvedConnection } from './connectionStore';

// DESIGN §16/§17 (informed-consent connection model): a bridge to Microsoft's "SQL Server
// (mssql)" extension (ms-mssql.mssql) so a T-SQL debug session can borrow a connection the user
// already has there — either the one the active .sql editor is connected to (silent auto-use), or
// one chosen from mssql's own connection picker. This is pure wiring (CLAUDE.md: extension = no
// logic): it hands the SAME launch fields a saved profile produces to the descriptor factory.
//
// SAFETY — the mssql connection carries a SQL password. It flows to the adapter ONLY via the
// out-of-band `ResolvedConnection.password` (→ ephemeral token → TSQLDBG_SQL_PASSWORD env var);
// it is never written into the DAP config, launch.json, logs, or the --trace. Same invariant the
// saved-profile path already upholds, just sourced from mssql instead of our SecretStorage.

/** Our extension id, as mssql's connection-sharing service keys per-extension approvals on. */
const EXTENSION_ID = 'ibaxo.tsql-step-debugger';
const MSSQL_EXTENSION_ID = 'ms-mssql.mssql';

/** The launch-chooser id for the "pick from mssql" entry (see ConnectionStore.ExternalSource). */
export const MSSQL_SOURCE_ID = 'mssql';

// --- The subset of the vscode-mssql extension API we depend on. Declared locally so mssql stays
// an OPTIONAL runtime dependency (no npm dep, no extensionDependencies) — every call is guarded
// by feature detection, so the debugger degrades cleanly when mssql is absent or older. ----------

interface IConnectionInfo {
	server: string;
	database: string;
	user: string;
	password: string;
	port: number;
	authenticationType: string;
	azureAccountToken?: string | undefined;
	encrypt: string | boolean;
	trustServerCertificate?: boolean | undefined;
}

interface IConnectionSharingService {
	getActiveEditorConnectionId(extensionId: string): Promise<string | undefined>;
	getActiveDatabase(extensionId: string): Promise<string | undefined>;
	getConnectionString(extensionId: string, connectionId: string): Promise<string | undefined>;
}

interface IMssqlExtension {
	promptForConnection?(ignoreFocusOut?: boolean): Promise<IConnectionInfo | undefined>;
	connectionSharing?: IConnectionSharingService;
}

/** Bridge to ms-mssql.mssql. Every method is a no-op (undefined) when mssql isn't usable. */
export class MssqlConnectionBridge {
	private async getApi(): Promise<IMssqlExtension | undefined> {
		const ext = vscode.extensions.getExtension<IMssqlExtension>(MSSQL_EXTENSION_ID);
		if (!ext) {
			return undefined; // mssql not installed — feature simply doesn't appear
		}
		try {
			return ext.isActive ? ext.exports : await ext.activate();
		} catch {
			return undefined; // mssql failed to activate — degrade silently
		}
	}

	/** True when mssql is installed (so we know whether to offer the "pick from mssql" entry). */
	isAvailable(): boolean {
		return vscode.extensions.getExtension(MSSQL_EXTENSION_ID) !== undefined;
	}

	/**
	 * Silent auto-use: the connection the ACTIVE editor is connected to in mssql, or undefined if
	 * there is none / mssql can't share it. The first call ever triggers mssql's own one-time
	 * approve/deny prompt (its consent gate); a denial or any error just yields undefined here so
	 * the launch falls through to the Connection Manager.
	 */
	async resolveActiveEditor(): Promise<ResolvedConnection | undefined> {
		const api = await this.getApi();
		const sharing = api?.connectionSharing;
		if (!sharing) {
			return undefined; // mssql too old to expose connection sharing
		}
		try {
			const connectionId = await sharing.getActiveEditorConnectionId(EXTENSION_ID);
			if (!connectionId) {
				return undefined; // active editor isn't connected in mssql
			}
			const [connectionString, database] = await Promise.all([
				sharing.getConnectionString(EXTENSION_ID, connectionId),
				sharing.getActiveDatabase(EXTENSION_ID).catch(() => undefined),
			]);
			if (!connectionString) {
				return undefined;
			}
			const resolved = fromConnectionString(connectionString, database);
			if ('unsupported' in resolved) {
				// AAD/token connection on the active editor: don't hijack the launch, fall through.
				return undefined;
			}
			return resolved;
		} catch {
			// Permission not granted / denied, no active editor, connection not found, etc. —
			// mssql throws for these. Treat every failure as "no mssql connection to adopt".
			return undefined;
		}
	}

	/**
	 * The "Pick from SQL Server (mssql)…" launch-chooser entry: shows mssql's own connection
	 * quickpick (the top-level API — no approval gate) and maps the choice to launch fields.
	 * Undefined = the user backed out (or picked an unsupported connection, reported inline).
	 */
	async pick(): Promise<ResolvedConnection | undefined> {
		const api = await this.getApi();
		if (!api?.promptForConnection) {
			void vscode.window.showWarningMessage(
				'The SQL Server (mssql) extension is not available to pick a connection from.',
			);
			return undefined;
		}
		let info: IConnectionInfo | undefined;
		try {
			info = await api.promptForConnection(true);
		} catch {
			return undefined;
		}
		if (!info) {
			return undefined; // user dismissed mssql's picker
		}
		const resolved = fromConnectionInfo(info);
		if ('unsupported' in resolved) {
			void vscode.window.showWarningMessage(`T-SQL Debug: ${resolved.unsupported}`);
			return undefined;
		}
		return resolved;
	}
}

// --- Mapping mssql connections onto our launch fields --------------------------------------------

type Unsupported = { unsupported: string };
const AAD_UNSUPPORTED = "Azure Active Directory / access-token authentication isn't supported by the T-SQL debugger yet — pick a Windows or SQL-login connection.";

/** Map a structured IConnectionInfo (from promptForConnection) to launch fields. */
function fromConnectionInfo(info: IConnectionInfo): ResolvedConnection | Unsupported {
	const auth = (info.authenticationType ?? '').toLowerCase();
	if (info.azureAccountToken || auth.includes('azure') || auth.includes('active directory')) {
		return { unsupported: AAD_UNSUPPORTED };
	}
	const integrated = auth === 'integrated' || auth === 'windows' || !info.user;
	const server = info.port && info.port !== 1433 ? `${info.server},${info.port}` : info.server;
	const trust = info.trustServerCertificate === true;
	return {
		password: integrated ? undefined : info.password || undefined,
		label: `mssql: ${server}`,
		server,
		database: info.database || 'master',
		authType: integrated ? 'integrated' : 'sql',
		sqlUser: integrated ? undefined : info.user,
		encrypt: normalizeEncrypt(info.encrypt),
		options: trust ? 'TrustServerCertificate=True' : undefined,
	};
}

/** Map an ADO.NET connection string (from connection sharing) to launch fields. */
function fromConnectionString(connectionString: string, databaseOverride?: string): ResolvedConnection | Unsupported {
	const m = parseConnectionString(connectionString);
	const auth = (m.get('authentication') ?? '').toLowerCase();
	if (m.has('azureaccounttoken') || auth.includes('active directory') || auth.includes('azure')) {
		return { unsupported: AAD_UNSUPPORTED };
	}
	const server = m.get('server') ?? m.get('data source') ?? m.get('address') ?? m.get('addr') ?? m.get('network address') ?? '';
	const database = databaseOverride || m.get('database') || m.get('initial catalog') || 'master';
	const integratedFlag = (m.get('integrated security') ?? m.get('trusted_connection') ?? '').toLowerCase();
	const integrated = integratedFlag === 'true' || integratedFlag === 'yes' || integratedFlag === 'sspi';
	const user = m.get('user id') ?? m.get('uid') ?? m.get('user');
	const password = m.get('password') ?? m.get('pwd');
	const isSql = !integrated && !!user;
	const trust = ['true', 'yes'].includes((m.get('trustservercertificate') ?? m.get('trust server certificate') ?? '').toLowerCase());
	return {
		password: isSql ? password : undefined,
		label: `mssql: ${server}`,
		server,
		database,
		authType: isSql ? 'sql' : ('integrated' as AuthType),
		sqlUser: isSql ? user : undefined,
		encrypt: normalizeEncrypt(m.get('encrypt')),
		options: trust ? 'TrustServerCertificate=True' : undefined,
	};
}

/** Encrypt is a tri-state in modern SqlClient (Optional/Mandatory/Strict) or a bool. → our bool. */
function normalizeEncrypt(value: string | boolean | undefined): boolean {
	if (typeof value === 'boolean') {
		return value;
	}
	const v = (value ?? '').toLowerCase();
	return v === 'true' || v === 'mandatory' || v === 'strict';
}

/**
 * Parse an ADO.NET / Microsoft.Data.SqlClient connection string into a lowercased key→value map.
 * Handles the quoting rules SqlClient uses for values with special characters (a value wrapped in
 * single or double quotes, with the quote char doubled to escape it) so an embedded ';' in a
 * password doesn't split the token.
 */
function parseConnectionString(cs: string): Map<string, string> {
	const map = new Map<string, string>();
	let i = 0;
	const n = cs.length;
	while (i < n) {
		while (i < n && (cs[i] === ';' || cs[i] === ' ' || cs[i] === '\t')) i++;
		if (i >= n) break;
		let key = '';
		while (i < n && cs[i] !== '=') {
			key += cs[i];
			i++;
		}
		if (i >= n) break; // malformed trailing key with no '='
		i++; // consume '='
		while (i < n && cs[i] === ' ') i++;
		let value = '';
		const quote = cs[i];
		if (quote === '"' || quote === "'") {
			i++;
			while (i < n) {
				if (cs[i] === quote) {
					if (cs[i + 1] === quote) {
						value += quote; // doubled quote → literal quote
						i += 2;
						continue;
					}
					i++;
					break;
				}
				value += cs[i];
				i++;
			}
		} else {
			while (i < n && cs[i] !== ';') {
				value += cs[i];
				i++;
			}
			value = value.trim();
		}
		const normalizedKey = key.trim().toLowerCase();
		if (normalizedKey.length > 0) {
			map.set(normalizedKey, value);
		}
	}
	return map;
}
