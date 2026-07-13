import * as vscode from 'vscode';
import { execFile } from 'child_process';

// DESIGN §16/§4 (A41/A42, M10 Connection Manager): saved connection profiles, adapted from
// vscode-planexplorer's connectionStore.ts. SAFETY: profile JSON — WITHOUT any password —
// lives in globalState; a SQL-auth profile's password lives ONLY in context.secrets under
// `tsqldbg.conn.<id>`. Passwords never go to settings, globalState, launch.json, logs, the
// --trace, or any committed file. Integrated (Windows/SSPI) profiles carry no password.
// Database enumeration and Test-connection run through the bundled .NET adapter's metadata CLI
// (--list-databases / --test-connection — which does BOTH integrated and SQL auth), never a
// Node SQL driver.

export type AuthType = 'integrated' | 'sql';

/** A saved connection. Named instance XOR port. No password field (see SAFETY above). */
export interface ConnectionProfile {
	id: string;
	name: string;
	server: string;
	port?: number;
	instanceName?: string;
	database: string;
	authType: AuthType;
	user?: string; // SQL login name (authType:'sql'); never the password
	encrypt: boolean;
	trustServerCertificate: boolean;
	options?: string; // extra raw connection-string fragment
}

/** The launch fields a profile resolves to (password fetched separately, via secrets). */
export interface ResolvedConnection {
	profileId: string;
	server: string; // host, host\instance, or host,port
	database: string;
	authType: AuthType;
	sqlUser?: string;
	encrypt: boolean;
	options?: string; // TrustServerCertificate + profile.options, appended raw
}

const PROFILES_KEY = 'tsqldbg.connections.profiles';
const ACTIVE_KEY = 'tsqldbg.connections.activeId';
const RECENT_KEY = 'tsqldbg.connections.recent';
const secretKey = (id: string): string => `tsqldbg.conn.${id}`;

export class ConnectionStore {
	constructor(
		private readonly context: vscode.ExtensionContext,
		// The bundled adapter exe path — the metadata CLI runs through it (both auth types).
		private readonly adapterPath: () => string,
	) {}

	getProfiles(): ConnectionProfile[] {
		return this.context.globalState.get<ConnectionProfile[]>(PROFILES_KEY, []);
	}

	getActive(): ConnectionProfile | undefined {
		const activeId = this.context.globalState.get<string>(ACTIVE_KEY);
		const profiles = this.getProfiles();
		return profiles.find((p) => p.id === activeId) ?? profiles[0];
	}

	async setActive(id: string): Promise<void> {
		await this.context.globalState.update(ACTIVE_KEY, id);
		await this.recordRecent(id);
	}

	getPassword(id: string): Thenable<string | undefined> {
		return this.context.secrets.get(secretKey(id));
	}

	/** Resolve the active profile (or pick/create one) into launch fields. Undefined = cancel. */
	async resolveForLaunch(): Promise<ResolvedConnection | undefined> {
		const profile = await this.pickForLaunch();
		return profile ? this.toResolved(profile) : undefined;
	}

	toResolved(p: ConnectionProfile): ResolvedConnection {
		return {
			profileId: p.id,
			server: effectiveServer(p),
			database: p.database,
			authType: p.authType,
			sqlUser: p.authType === 'sql' ? p.user : undefined,
			encrypt: p.encrypt,
			options: connectionOptionsFragment(p),
		};
	}

	// --- MRU (ids only; never a secret) -------------------------------------------------------

	private getRecentIds(): string[] {
		return this.context.globalState.get<string[]>(RECENT_KEY, []);
	}

	private async recordRecent(id: string): Promise<void> {
		const existing = new Set(this.getProfiles().map((p) => p.id));
		const next = [id, ...this.getRecentIds().filter((x) => x !== id)].filter((x) => existing.has(x));
		await this.context.globalState.update(RECENT_KEY, next);
	}

	private profilesByRecent(): ConnectionProfile[] {
		const rank = new Map(this.getRecentIds().map((id, i) => [id, i] as const));
		return [...this.getProfiles()].sort(
			(a, b) => (rank.get(a.id) ?? Number.MAX_SAFE_INTEGER) - (rank.get(b.id) ?? Number.MAX_SAFE_INTEGER),
		);
	}

	private async saveProfiles(profiles: ConnectionProfile[]): Promise<void> {
		const clean = profiles.map((p) => stripSecrets(p));
		await this.context.globalState.update(PROFILES_KEY, clean);
	}

	async upsert(profile: ConnectionProfile, password?: string): Promise<void> {
		const profiles = this.getProfiles().filter((p) => p.id !== profile.id);
		profiles.push(stripSecrets(profile));
		await this.saveProfiles(profiles);
		if (password !== undefined) {
			await this.context.secrets.store(secretKey(profile.id), password);
		}
		if (this.context.globalState.get<string>(ACTIVE_KEY) === undefined) {
			await this.setActive(profile.id);
		}
	}

	async remove(id: string): Promise<void> {
		await this.saveProfiles(this.getProfiles().filter((p) => p.id !== id));
		await this.context.secrets.delete(secretKey(id));
		if (this.context.globalState.get<string>(ACTIVE_KEY) === id) {
			await this.context.globalState.update(ACTIVE_KEY, undefined);
		}
	}

	// --- Interactive flows (QuickPick / InputBox) --------------------------------------------

	/** The `tsqldbg.connections.manage` command: a looping list with inline edit/delete. */
	async manage(): Promise<void> {
		for (;;) {
			const outcome = await this.pickManageAction();
			switch (outcome.kind) {
				case 'close':
					return;
				case 'activate':
					await this.setActive(outcome.id);
					break;
				case 'add':
					await this.addConnection();
					break;
				case 'edit':
					await this.editProfile(outcome.id);
					break;
				case 'delete':
					await this.confirmAndDelete(outcome.id, outcome.name);
					break;
			}
		}
	}

	private pickManageAction(): Promise<ManageOutcome> {
		const editButton: vscode.QuickInputButton = { iconPath: new vscode.ThemeIcon('edit'), tooltip: 'Edit' };
		const deleteButton: vscode.QuickInputButton = { iconPath: new vscode.ThemeIcon('trash'), tooltip: 'Delete' };

		return new Promise<ManageOutcome>((resolve) => {
			const profiles = this.getProfiles();
			const activeId = this.getActive()?.id;

			const qp = vscode.window.createQuickPick<ManageItem>();
			qp.title = 'T-SQL Debug: Manage Connections';
			qp.ignoreFocusOut = true;
			qp.placeholder = profiles.length
				? 'Select a connection to make it active — or use the edit / trash buttons'
				: 'No connections yet — add one';
			qp.items = [
				...profiles.map<ManageItem>((p) => ({
					label: `${p.id === activeId ? '$(check) ' : ''}${p.name}`,
					description: describeTarget(p),
					detail: describeAuth(p),
					buttons: [editButton, deleteButton],
					action: 'activate',
					id: p.id,
					profileName: p.name,
				})),
				{ label: '$(add) Add connection…', action: 'add' },
			];

			let outcome: ManageOutcome = { kind: 'close' };
			qp.onDidTriggerItemButton((event) => {
				const { id, profileName } = event.item;
				if (!id) return;
				outcome = event.button === editButton ? { kind: 'edit', id } : { kind: 'delete', id, name: profileName ?? id };
				qp.hide();
			});
			qp.onDidAccept(() => {
				const item = qp.selectedItems[0];
				if (item?.action === 'add') outcome = { kind: 'add' };
				else if (item?.id) outcome = { kind: 'activate', id: item.id };
				qp.hide();
			});
			qp.onDidHide(() => {
				qp.dispose();
				resolve(outcome);
			});
			qp.show();
		});
	}

	/** Status-bar click (`tsqldbg.connections.select`): pick the active connection. */
	async pickActive(): Promise<void> {
		const profiles = this.getProfiles();
		if (profiles.length === 0) {
			await this.offerToCreate();
			return;
		}
		const activeId = this.getActive()?.id;
		const picked = await vscode.window.showQuickPick(
			profiles.map((p) => ({
				label: `${p.id === activeId ? '$(check) ' : ''}${p.name}`,
				description: describeTarget(p),
				id: p.id,
			})),
			{ title: 'T-SQL Debug: Select Active Connection' },
		);
		if (picked) await this.setActive(picked.id);
	}

	/** Launch-time chooser: MRU-first list + add/manage; accepting sets it active. */
	async pickForLaunch(): Promise<ConnectionProfile | undefined> {
		if (this.getProfiles().length === 0) {
			await this.offerToCreate();
			return this.getActive();
		}
		for (;;) {
			const outcome = await this.pickLaunchAction();
			switch (outcome.kind) {
				case 'cancel':
					return undefined;
				case 'accept':
					await this.setActive(outcome.id);
					return this.getProfiles().find((p) => p.id === outcome.id);
				case 'add': {
					const added = await this.addConnection();
					if (!added) break;
					await this.setActive(added.id);
					return added;
				}
				case 'manage':
					await this.manage();
					break;
				case 'edit':
					await this.editProfile(outcome.id);
					break;
				case 'delete':
					await this.confirmAndDelete(outcome.id, outcome.name);
					break;
			}
		}
	}

	private pickLaunchAction(): Promise<LaunchOutcome> {
		const editButton: vscode.QuickInputButton = { iconPath: new vscode.ThemeIcon('edit'), tooltip: 'Edit' };
		const deleteButton: vscode.QuickInputButton = { iconPath: new vscode.ThemeIcon('trash'), tooltip: 'Delete' };

		return new Promise<LaunchOutcome>((resolve) => {
			const profiles = this.profilesByRecent();
			const activeId = this.getActive()?.id;

			const qp = vscode.window.createQuickPick<LaunchItem>();
			qp.title = 'T-SQL Debug: Choose a connection';
			qp.ignoreFocusOut = true;
			qp.placeholder = 'Select a connection to debug with — or add / manage';
			qp.items = [
				...profiles.map<LaunchItem>((p) => ({
					label: `${p.id === activeId ? '$(check) ' : ''}${p.name}`,
					description: describeTarget(p),
					detail: describeAuth(p),
					buttons: [editButton, deleteButton],
					action: 'accept',
					id: p.id,
					profileName: p.name,
				})),
				{ label: '$(add) Add connection…', action: 'add' },
				{ label: '$(gear) Manage connections…', action: 'manage' },
			];

			let outcome: LaunchOutcome = { kind: 'cancel' };
			qp.onDidTriggerItemButton((event) => {
				const { id, profileName } = event.item;
				if (!id) return;
				outcome = event.button === editButton ? { kind: 'edit', id } : { kind: 'delete', id, name: profileName ?? id };
				qp.hide();
			});
			qp.onDidAccept(() => {
				const item = qp.selectedItems[0];
				if (item?.action === 'add') outcome = { kind: 'add' };
				else if (item?.action === 'manage') outcome = { kind: 'manage' };
				else if (item?.id) outcome = { kind: 'accept', id: item.id };
				qp.hide();
			});
			qp.onDidHide(() => {
				qp.dispose();
				resolve(outcome);
			});
			qp.show();
		});
	}

	// --- Per-field editor (auth-aware) -------------------------------------------------------

	private async editProfile(id: string): Promise<void> {
		for (;;) {
			const profile = this.getProfiles().find((p) => p.id === id);
			if (!profile) return;
			const field = await this.pickField(profile);
			if (field === undefined) return;
			await this.editField(profile, field);
		}
	}

	private async pickField(profile: ConnectionProfile): Promise<FieldId | undefined> {
		interface FieldItem extends vscode.QuickPickItem {
			field?: FieldId;
		}
		const target = profile.instanceName ? `instance ${profile.instanceName}` : `port ${profile.port ?? 1433}`;
		const items: FieldItem[] = [
			{ label: 'Name', description: profile.name, field: 'name' },
			{ label: 'Server', description: profile.server, field: 'server' },
			{ label: 'Instance / Port', description: target, field: 'target' },
			{ label: 'Database', description: profile.database, field: 'database' },
			{ label: 'Authentication', description: profile.authType === 'sql' ? 'SQL login' : 'Windows (integrated)', field: 'auth' },
		];
		if (profile.authType === 'sql') {
			items.push({ label: 'User', description: profile.user ?? '', field: 'user' });
			items.push({ label: 'Update password…', description: '••••••••', field: 'password' });
		}
		items.push(
			{ label: 'Encrypt', description: String(profile.encrypt), field: 'encrypt' },
			{ label: 'Trust certificate', description: String(profile.trustServerCertificate), field: 'trust' },
			{ label: 'Extra options', description: profile.options ?? '', field: 'options' },
			{ label: '$(plug) Test connection', field: 'test' },
			{ label: '$(check) Done', field: undefined },
		);
		const picked = await vscode.window.showQuickPick(items, {
			title: `T-SQL Debug: Edit "${profile.name}"`,
			placeHolder: 'Select a field to change — it saves immediately',
			ignoreFocusOut: true,
		});
		return picked?.field;
	}

	private async editField(profile: ConnectionProfile, field: FieldId): Promise<void> {
		switch (field) {
			case 'name': {
				const value = await input('Connection name', profile.name, 'e.g. Dev SQL 2022');
				if (value?.trim()) await this.upsert({ ...profile, name: value.trim() });
				break;
			}
			case 'server': {
				const value = await input('Server host', profile.server, 'e.g. localhost or 10.0.0.5');
				if (value?.trim()) await this.upsert({ ...profile, server: value.trim() });
				break;
			}
			case 'target':
				await this.editTarget(profile);
				break;
			case 'database': {
				const value = await this.pickDatabase(profile);
				if (value !== undefined) await this.upsert({ ...profile, database: value.trim() || 'master' });
				break;
			}
			case 'auth':
				await this.editAuth(profile);
				break;
			case 'user': {
				const value = await input('SQL login (user)', profile.user ?? '', 'e.g. sa');
				if (value?.trim()) await this.upsert({ ...profile, user: value.trim() });
				break;
			}
			case 'password': {
				const password = await promptPassword();
				if (password !== undefined && password.length > 0) {
					await this.context.secrets.store(secretKey(profile.id), password);
					void vscode.window.showInformationMessage(`T-SQL Debug: password updated for "${profile.name}".`);
				}
				break;
			}
			case 'encrypt': {
				const value = await pickBool('Encrypt connection?', profile.encrypt);
				if (value !== undefined) await this.upsert({ ...profile, encrypt: value });
				break;
			}
			case 'trust': {
				const value = await pickBool('Trust server certificate?', profile.trustServerCertificate);
				if (value !== undefined) await this.upsert({ ...profile, trustServerCertificate: value });
				break;
			}
			case 'options': {
				const value = await input('Extra connection options', profile.options ?? '', 'e.g. Connect Timeout=30');
				if (value !== undefined) await this.upsert({ ...profile, options: value.trim() || undefined });
				break;
			}
			case 'test':
				await this.testConnection(profile);
				break;
		}
	}

	private async editTarget(profile: ConnectionProfile): Promise<void> {
		const choice = await vscode.window.showQuickPick(
			[
				{ label: 'Named instance', via: 'instance' as const },
				{ label: 'Port', via: 'port' as const },
			],
			{ title: 'Connect via a named instance or a port?', ignoreFocusOut: true },
		);
		if (!choice) return;
		if (choice.via === 'instance') {
			const value = await input('Named instance', profile.instanceName ?? '', 'e.g. SQLEXPRESS');
			if (!value?.trim()) return;
			await this.upsert({ ...profile, instanceName: value.trim(), port: undefined });
		} else {
			const value = await input('Port', profile.port ? String(profile.port) : '1433', '1433');
			if (value === undefined) return;
			const parsed = Number.parseInt(value, 10);
			await this.upsert({ ...profile, port: Number.isNaN(parsed) ? 1433 : parsed, instanceName: undefined });
		}
	}

	private async editAuth(profile: ConnectionProfile): Promise<void> {
		const choice = await vscode.window.showQuickPick(
			[
				{ label: 'Windows (integrated)', authType: 'integrated' as AuthType },
				{ label: 'SQL login', authType: 'sql' as AuthType },
			],
			{ title: 'Authentication', ignoreFocusOut: true },
		);
		if (!choice) return;
		if (choice.authType === 'sql') {
			const user = await input('SQL login (user)', profile.user ?? '', 'e.g. sa');
			if (!user?.trim()) return;
			const password = await promptPassword();
			if (password === undefined) return;
			await this.upsert({ ...profile, authType: 'sql', user: user.trim() }, password);
		} else {
			// Switching to integrated: drop the stored password.
			await this.context.secrets.delete(secretKey(profile.id));
			await this.upsert({ ...profile, authType: 'integrated', user: undefined });
		}
	}

	// --- Database picker + Test connection, via the adapter metadata CLI ----------------------

	private pickDatabase(profile: ConnectionProfile, passwordOverride?: string): Promise<string | undefined> {
		return new Promise<string | undefined>((resolve) => {
			const qp = vscode.window.createQuickPick<vscode.QuickPickItem>();
			qp.title = `T-SQL Debug: Database for "${profile.name}"`;
			qp.ignoreFocusOut = true;
			qp.value = profile.database;
			qp.placeholder = 'Type a database name — loading the list…';
			qp.busy = true;

			let outcome: string | undefined;
			let open = true;

			qp.onDidAccept(() => {
				const picked = qp.selectedItems[0]?.label;
				const typed = qp.value.trim();
				outcome = picked ?? (typed.length > 0 ? typed : profile.database);
				qp.hide();
			});
			qp.onDidHide(() => {
				open = false;
				qp.dispose();
				resolve(outcome);
			});
			qp.show();

			void this.listDatabases(profile, passwordOverride).then((names) => {
				if (!open) return;
				qp.busy = false;
				if (names.length === 0) {
					qp.placeholder = 'Type the database name (could not load the list)';
					return;
				}
				qp.items = names.map<vscode.QuickPickItem>((name) => ({
					label: name,
					description: name === profile.database ? '(current)' : undefined,
				}));
			});
		});
	}

	private async listDatabases(profile: ConnectionProfile, passwordOverride?: string): Promise<string[]> {
		const result = await this.runMetadata('--list-databases', profile, passwordOverride);
		return Array.isArray(result?.databases) ? (result.databases as string[]) : [];
	}

	private async testConnection(profile: ConnectionProfile): Promise<void> {
		const result = await this.runMetadata('--test-connection', profile);
		if (result?.ok === true) {
			void vscode.window.showInformationMessage(`T-SQL Debug: connected to ${describeTarget(profile)} / ${profile.database}.`);
		} else {
			void vscode.window.showErrorMessage(`T-SQL Debug: connection failed — ${result?.error ?? 'unknown error'}`);
		}
	}

	/** Spawn the bundled adapter's metadata CLI; the SQL password (if any) goes via env. */
	private async runMetadata(mode: '--list-databases' | '--test-connection', profile: ConnectionProfile, passwordOverride?: string): Promise<any> {
		const args = [mode, effectiveServer(profile), '--database', profile.database];
		if (profile.authType === 'sql' && profile.user) {
			args.push('--sql-user', profile.user);
		}
		const fragment = connectionOptionsFragment(profile);
		if (fragment) {
			args.push('--options', fragment);
		}
		if (profile.encrypt) {
			args.push('--encrypt');
		}

		const env: NodeJS.ProcessEnv = { ...process.env };
		if (profile.authType === 'sql') {
			const password = passwordOverride ?? (await this.getPassword(profile.id));
			if (password) {
				env.TSQLDBG_SQL_PASSWORD = password;
			}
		}

		return new Promise<any>((resolve) => {
			execFile(this.adapterPath(), args, { env, timeout: 20000, windowsHide: true }, (err, stdout) => {
				try {
					resolve(JSON.parse((stdout ?? '').toString().trim()));
				} catch {
					resolve({ ok: false, error: err?.message ?? 'no response from the adapter' });
				}
			});
		});
	}

	// --- Add / delete ------------------------------------------------------------------------

	private async addConnection(): Promise<ConnectionProfile | undefined> {
		const name = await input('Connection name', '', 'e.g. Dev SQL 2022');
		if (!name?.trim()) return undefined;
		const server = await input('Server host', '', 'e.g. localhost or 10.0.0.5');
		if (!server?.trim()) return undefined;

		const authChoice = await vscode.window.showQuickPick(
			[
				{ label: 'Windows (integrated)', authType: 'integrated' as AuthType },
				{ label: 'SQL login', authType: 'sql' as AuthType },
			],
			{ title: 'Authentication', placeHolder: 'How should the debugger connect?', ignoreFocusOut: true },
		);
		if (!authChoice) return undefined;

		let user: string | undefined;
		let password: string | undefined;
		if (authChoice.authType === 'sql') {
			const u = await input('SQL login (user)', '', 'e.g. sa');
			if (!u?.trim()) return undefined;
			user = u.trim();
			password = await promptPassword();
			if (password === undefined) return undefined;
		}

		const profile: ConnectionProfile = {
			id: cryptoId(),
			name: name.trim(),
			server: server.trim(),
			port: 1433,
			instanceName: undefined,
			database: 'master',
			authType: authChoice.authType,
			user,
			encrypt: false,
			trustServerCertificate: true, // dev/test-friendly default; editable
			options: undefined,
		};

		// Load the database list with the same picker the editor uses. The profile isn't saved
		// yet, so the just-entered SQL password is passed straight through (not via secrets).
		const database = await this.pickDatabase(profile, password);
		if (database === undefined) return undefined;
		profile.database = database.trim() || 'master';

		await this.upsert(profile, password);
		void vscode.window.showInformationMessage(`T-SQL Debug: added connection "${profile.name}".`);
		// Everything else (instance/port, encrypt, options) is editable in place.
		await this.editProfile(profile.id);
		return this.getProfiles().find((p) => p.id === profile.id);
	}

	private async confirmAndDelete(id: string, name: string): Promise<void> {
		const DELETE = 'Delete';
		const choice = await vscode.window.showWarningMessage(
			`Delete connection "${name}"?${''} This also removes its saved password.`,
			{ modal: true },
			DELETE,
		);
		if (choice === DELETE) {
			await this.remove(id);
			void vscode.window.showInformationMessage(`T-SQL Debug: removed "${name}".`);
		}
	}

	private async offerToCreate(): Promise<void> {
		const CREATE = 'Add Connection…';
		const choice = await vscode.window.showInformationMessage(
			'T-SQL Debug: no connection is configured. Add one to start debugging.',
			CREATE,
		);
		if (choice === CREATE) await this.addConnection();
	}
}

interface ManageItem extends vscode.QuickPickItem {
	action?: 'activate' | 'add';
	id?: string;
	profileName?: string;
}
type ManageOutcome =
	| { kind: 'activate'; id: string }
	| { kind: 'edit'; id: string }
	| { kind: 'delete'; id: string; name: string }
	| { kind: 'add' }
	| { kind: 'close' };

interface LaunchItem extends vscode.QuickPickItem {
	action?: 'accept' | 'add' | 'manage';
	id?: string;
	profileName?: string;
}
type LaunchOutcome =
	| { kind: 'accept'; id: string }
	| { kind: 'edit'; id: string }
	| { kind: 'delete'; id: string; name: string }
	| { kind: 'add' }
	| { kind: 'manage' }
	| { kind: 'cancel' };

type FieldId =
	| 'name'
	| 'server'
	| 'target'
	| 'database'
	| 'auth'
	| 'user'
	| 'password'
	| 'encrypt'
	| 'trust'
	| 'options'
	| 'test';

function stripSecrets(profile: ConnectionProfile): ConnectionProfile {
	const clone = { ...profile } as ConnectionProfile & { password?: unknown };
	delete clone.password;
	return clone;
}

/** Server as the adapter expects it: host, host\instance, or host,port. */
function effectiveServer(p: ConnectionProfile): string {
	if (p.instanceName) return `${p.server}\\${p.instanceName}`;
	if (p.port && p.port !== 1433) return `${p.server},${p.port}`;
	return p.server;
}

/** Raw connection-string fragment from a profile's trust + extra options (NOT encrypt). */
function connectionOptionsFragment(p: ConnectionProfile): string | undefined {
	const parts: string[] = [];
	if (p.trustServerCertificate) parts.push('TrustServerCertificate=True');
	if (p.options && p.options.trim()) parts.push(p.options.trim());
	return parts.length ? parts.join(';') : undefined;
}

function describeTarget(p: ConnectionProfile): string {
	return effectiveServer(p);
}

function describeAuth(p: ConnectionProfile): string {
	const auth = p.authType === 'sql' ? `SQL: ${p.user ?? '?'}` : 'Windows';
	return `${auth}  ·  ${p.database}`;
}

async function input(prompt: string, value?: string, placeHolder?: string): Promise<string | undefined> {
	return vscode.window.showInputBox({ prompt, value, placeHolder, ignoreFocusOut: true });
}

async function promptPassword(): Promise<string | undefined> {
	return vscode.window.showInputBox({ prompt: 'Password', password: true, ignoreFocusOut: true });
}

async function pickBool(title: string, current: boolean): Promise<boolean | undefined> {
	const picked = await vscode.window.showQuickPick(
		[
			{ label: `Yes${current ? ' (current)' : ''}`, value: true },
			{ label: `No${!current ? ' (current)' : ''}`, value: false },
		],
		{ title },
	);
	return picked?.value;
}

function cryptoId(): string {
	return `c${Date.now().toString(36)}${Math.random().toString(36).slice(2, 8)}`;
}
