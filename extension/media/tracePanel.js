// DESIGN §17 (A74): the T-SQL Trace panel renderer — a dumb projection of the adapter's
// tsqldbg_trace* custom events, relayed by the extension as {type: start|step|summary}
// messages. Step payloads are the §24.8 JSONL step lines verbatim (nested keys
// PascalCase by that contract: resultSets[].Columns/Rows/Truncated, error.Number/…),
// plus the adapter-added `source` for navigation. All DOM is built with textContent —
// statement text, values, and messages are debuggee data and must never be innerHTML.
(function () {
	'use strict';

	const vscode = acquireVsCodeApi();
	const rowsEl = document.getElementById('rows');
	const metaEl = document.getElementById('meta');
	const summaryEl = document.getElementById('summary');
	const filterEl = document.getElementById('filter');
	const emptyEl = document.getElementById('empty');

	let stepCount = 0;
	let lastErrorKey = null;
	let statusEl = null;

	// A74 rider (Ivan's first-use feedback): each step event carries its real pre-step
	// call stack (`stack`, bottom → top, callers at their parked call-site line) — the
	// chain and depth render straight from it. frame.id is the §24.5 MONOTONIC frame
	// ordinal (identity, never depth — each GO batch / call gets the next number), which
	// makes it the perfect key for per-frame last-known variable values: a fresh frame
	// (sibling call, next batch) has a fresh id and so never inherits stale values.
	let valuesByFrame = {};

	function el(tag, className, text) {
		const node = document.createElement(tag);
		if (className) {
			node.className = className;
		}
		if (text !== undefined) {
			node.textContent = text;
		}
		return node;
	}

	// ---- header ----------------------------------------------------------

	function reset(payload) {
		rowsEl.textContent = '';
		summaryEl.textContent = '';
		summaryEl.classList.remove('visible');
		stepCount = 0;
		lastErrorKey = null;
		valuesByFrame = {};
		emptyEl.style.display = 'none';

		metaEl.textContent = '';
		metaEl.appendChild(el('span', 'target', payload.server + '/' + payload.database));
		const capture = el('span', 'dim', payload.mode + ' · step ' + payload.stepMode + ' · variables: ' + payload.variableCapture + ' (post-statement)');
		capture.title = 'Variable chips show values AFTER each statement.\nBright chip = changed by that statement (old → new); dim chip = an initial value (frame\'s first line) or a value referenced but unchanged.';
		metaEl.appendChild(capture);
		metaEl.appendChild(filterEl);
		statusEl = el('span', 'status', 'running…');
		metaEl.appendChild(statusEl);
	}

	// ---- steps -----------------------------------------------------------

	function firstLine(text) {
		const idx = text.search(/[\r\n]/);
		return idx < 0 ? text : text.slice(0, idx);
	}

	function addStep(step) {
		stepCount++;
		const block = el('div', 'step');
		block.dataset.seq = String(step.seq);

		// Call chain: rendered straight from the event's pre-step stack. The row indents
		// by depth; the first row of a newly-entered nested frame announces the chain
		// (with call-site line numbers, e.g. `↳ <script>:4 → dbo.p10_recurse:5`).
		const frame = step.frame || {};
		const stack = Array.isArray(step.stack) ? step.stack : [];
		const depth = stack.length > 0 ? stack.length - 1 : 0;
		const chain = stack.length > 0
			? stack.map(function (e) { return (e.module || '?') + ':' + e.line; }).join(' → ')
			: null;
		const frameKey = typeof frame.id === 'number' ? frame.id : null;
		const enteredFrame = frameKey !== null && !(frameKey in valuesByFrame);
		// Depth indents the row CONTENT (location/statement/extras) via the --indent CSS
		// variable; the seq number stays pinned to a fixed left column.
		if (depth > 0) {
			block.style.setProperty('--indent', Math.min(depth, 12) * 18 + 'px');
		}
		if (enteredFrame && depth > 0 && chain) {
			block.appendChild(el('div', 'frame-enter', '↳ ' + chain));
		}

		const line = el('div', 'step-line');
		line.appendChild(el('span', 'seq', String(step.seq)));

		const locText = (frame.module || '?') + ':' + (frame.line != null ? frame.line : '?');
		const chainTitle = chain && stack.length > 1 ? 'Call chain: ' + chain : '';
		if (step.source && step.source.path) {
			const link = el('a', 'loc', locText);
			link.title = (chainTitle ? chainTitle + '\n' : '') + step.source.path + ':' + step.source.line;
			link.addEventListener('click', function () {
				vscode.postMessage({ type: 'navigate', path: step.source.path, line: step.source.line });
			});
			line.appendChild(link);
		} else {
			const span = el('span', 'loc', locText);
			span.title = (chainTitle ? chainTitle + '\n' : '')
				+ 'No openable source for this module (e.g. dynamic SQL) — see the statement text.';
			line.appendChild(span);
		}

		const statement = step.statement || '';
		const head = firstLine(statement);
		const multiline = head.length < statement.length;
		// The filter matches rendered text; a collapsed multi-line statement only renders
		// its first line, so stash the full text where applyFilterTo can see it.
		if (multiline) {
			block.dataset.fullStatement = statement.toLowerCase();
		}
		const stmt = el('code', 'stmt', head);
		if (multiline) {
			stmt.classList.add('expandable');
			stmt.appendChild(el('span', 'more', '  ⋯'));
			stmt.title = 'Click to expand the full statement';
			let full = null;
			stmt.addEventListener('click', function () {
				if (full) {
					full.remove();
					full = null;
				} else {
					full = el('div', 'stmt-full', statement);
					line.insertAdjacentElement('afterend', full);
				}
			});
		}
		line.appendChild(stmt);

		// Variable chips — values are POST-statement. Bright chip = changed by this
		// statement, rendered old → new (only when the panel knows the prior value, i.e. not
		// on the frame's baseline step). Dim chip = a value the statement did NOT change: an
		// initial/baseline value (frame's first line) or a referenced-but-unchanged variable,
		// so a row that only READS variables still shows what it saw instead of looking broken.
		const known = frameKey !== null ? (valuesByFrame[frameKey] = valuesByFrame[frameKey] || {}) : null;
		const changedMap = step.variablesChanged || null;
		const fullMap = step.variablesAfter || null;
		// Variable names this statement's TEXT mentions — drives which baseline values are worth
		// surfacing (below) and the referenced-but-unchanged context chips (further down).
		// Best-effort §8.2 name match; does not exclude names inside string/comment literals.
		const referencedNames = new Set((step.statement || '').match(/(?<!@)@[A-Za-z_][\w$#]*/g) || []);
		// Variables this statement ASSIGNS. A baseline row has no prior snapshot to diff against,
		// but the statement TEXT still says whether a variable was SET here or merely read — so a
		// declared-and-initialized var (@next = @n + 1, NULL → 2) isn't mislabelled "not changed".
		// Best-effort, covering the two forms that dominate a frame's first line: DECLARE and SET.
		const stmtText = step.statement || '';
		const setTargets = new Set();
		const setRe = /\bSET\s+(@[A-Za-z_][\w$#]*)\s*(?:[+\-*/%&|^])?=(?!=)/gi;
		for (let sm; (sm = setRe.exec(stmtText)) !== null;) {
			setTargets.add(sm[1]);
		}
		const declaredTargets = new Set();
		if (/^\s*DECLARE\b/i.test(stmtText)) {
			// The variable right after DECLARE or a top-level comma. Commas inside a type's
			// (precision, scale) or a table type aren't followed by @, so they don't match.
			const declRe = /(?:\bDECLARE\b|,)\s*(@[A-Za-z_][\w$#]*)/gi;
			for (let dm; (dm = declRe.exec(stmtText)) !== null;) {
				declaredTargets.add(dm[1]);
			}
		}
		const deltas = el('span', 'deltas');
		for (const key of Object.keys(changedMap || fullMap || {})) {
			const value = (changedMap || fullMap)[key];
			if (key === '__capture_error') {
				const warn = el('span', 'chip warn', '⚠ variables unreadable this step');
				warn.title = value;
				deltas.appendChild(warn);
				continue;
			}
			const old = known && Object.prototype.hasOwnProperty.call(known, key) ? known[key] : undefined;
			const isChange = old !== undefined && old !== value;
			// old === undefined ⇒ the panel has no prior value for this variable in this frame.
			// Because §8.2 hoisting fixes the catalog at frame init, that only happens on the
			// frame's BASELINE step, where the "changed" diff returns the whole in-scope set at
			// once (TraceRunner.DiffVariables, previous == null). These are initial values, not
			// deltas: the statement may merely READ them (e.g. a parameter @n on the frame's
			// first line), so they must not be styled or described as "changed by this statement".
			const isBaseline = old === undefined;
			// Always record the value so later rows can diff and show context chips. But on a
			// "changed"-mode baseline row, only SURFACE the variables this statement mentions —
			// otherwise the frame's first line dumps every in-scope variable (all params, unrelated
			// locals) onto a statement that touches one of them. Full-capture mode is exempt:
			// emitting the complete per-row snapshot is exactly its contract.
			if (known) {
				known[key] = value;
			}
			if (isBaseline && changedMap && !referencedNames.has(key)) {
				continue;
			}
			const chip = el('span', 'chip' + (isChange ? '' : ' ctx'),
				isChange ? key + '  ' + old + ' → ' + value : key + ' = ' + value);
			chip.title = isChange
				? key + ' changed by this statement: ' + old + ' → ' + value
				: isBaseline
					? setTargets.has(key)
						? key + ' = ' + value + ' (set by this statement)'
						: declaredTargets.has(key)
							? key + ' = ' + value + ' (declared here)'
							: key + ' = ' + value + ' (initial value on entering this frame; not changed here)'
					: key + ' = ' + value + ' (unchanged by this statement)';
			deltas.appendChild(chip);
		}

		// Context chips (changed mode): variables the statement TEXT references that did not
		// change and weren't already surfaced above — display their current value dimmed.
		if (changedMap && known) {
			for (const name of referencedNames) {
				if (Object.prototype.hasOwnProperty.call(changedMap, name)
					|| !Object.prototype.hasOwnProperty.call(known, name)) {
					continue;
				}
				const chip = el('span', 'chip ctx', name + ' = ' + known[name]);
				chip.title = name + ' = ' + known[name] + ' (referenced, unchanged by this statement)';
				deltas.appendChild(chip);
			}
		}

		if (deltas.childNodes.length > 0) {
			line.appendChild(deltas);
		}

		block.appendChild(line);

		if (step.tempRowCounts) {
			const counts = Object.keys(step.tempRowCounts)
				.map(function (k) { return k + ': ' + step.tempRowCounts[k]; })
				.join('   ');
			if (counts) {
				block.appendChild(el('div', 'indent note', 'temp rows — ' + counts));
			}
		}

		(step.output || []).forEach(function (msg) {
			block.appendChild(el('div', 'indent print', msg));
		});

		(step.notes || []).forEach(function (note) {
			block.appendChild(el('div', 'indent note', note));
		});

		(step.resultSets || []).forEach(function (rs) {
			block.appendChild(renderResultSet(rs));
		});

		// A73 console error hygiene mirrored: the file (and so the event stream) carries the
		// active error context on every CATCH-transit step — render it once per distinct error.
		if (step.error) {
			const e = step.error;
			const key = [e.Number, e.Severity, e.State, e.Line, e.Procedure, e.Message, e.RoutedTo].join(' ');
			if (key !== lastErrorKey) {
				const where = (e.Procedure ? ', Procedure ' + e.Procedure : '') + (e.Line != null ? ', Line ' + e.Line : '');
				block.appendChild(el('div', 'indent error-line',
					'Msg ' + e.Number + ', Level ' + e.Severity + ', State ' + e.State + where + ': ' + e.Message + ' (→ ' + e.RoutedTo + ')'));
			}
			block.classList.add('has-error');
			lastErrorKey = key;
		} else {
			lastErrorKey = null;
		}

		applyFilterTo(block);

		const nearBottom = rowsEl.scrollHeight - rowsEl.scrollTop - rowsEl.clientHeight < 48;
		rowsEl.appendChild(block);
		if (nearBottom) {
			rowsEl.scrollTop = rowsEl.scrollHeight;
		}
	}

	function renderResultSet(rs) {
		const cols = rs.Columns || [];
		const rows = rs.Rows || [];
		const details = el('details', 'rs');
		details.open = true;
		details.appendChild(el('summary', null,
			'Result set — ' + cols.length + ' column' + (cols.length === 1 ? '' : 's') + ' × ' +
			rows.length + ' row' + (rows.length === 1 ? '' : 's') + (rs.Truncated ? ' (truncated)' : '')));

		const wrap = el('div', 'indent');
		const table = document.createElement('table');
		const headRow = document.createElement('tr');
		cols.forEach(function (c) {
			headRow.appendChild(el('th', null, c));
		});
		table.appendChild(headRow);
		rows.forEach(function (r) {
			const tr = document.createElement('tr');
			r.forEach(function (cell) {
				const td = el('td', null, cell);
				td.title = cell;
				tr.appendChild(td);
			});
			table.appendChild(tr);
		});
		wrap.appendChild(table);
		if (rs.Truncated) {
			wrap.appendChild(el('div', 'truncated', 'Row cap reached (maxConsoleRows) — the full set ran in the session; see the trace file.'));
		}
		details.appendChild(wrap);
		return details;
	}

	// ---- summary ---------------------------------------------------------

	function showSummary(payload) {
		if (statusEl) {
			statusEl.textContent = payload.finalState;
			statusEl.classList.add(payload.finalState);
		}

		summaryEl.textContent = '';
		const headline = el('div', 'headline ' + payload.finalState,
			'Trace ' + payload.finalState + ' — ' + payload.steps + ' statements, return code ' + payload.returnCode +
			', ' + (payload.committed ? 'committed' : 'rolled back'));
		summaryEl.appendChild(headline);

		if (payload.finalState === 'incomplete') {
			summaryEl.appendChild(el('div', 'line', 'Run was cancelled before completion (Pause/Stop) — partial trace, rolled back.'));
		}

		if (payload.outputParams) {
			const parts = Object.keys(payload.outputParams).map(function (k) {
				return k + ' = ' + payload.outputParams[k];
			});
			summaryEl.appendChild(el('div', 'line', 'OUTPUT parameters: ' + parts.join(', ')));
		}

		(payload.messages || []).forEach(function (m) {
			summaryEl.appendChild(el('div', 'line', m));
		});

		if (payload.filePath) {
			const fileLine = el('div', 'line', 'Trace file: ');
			const link = el('a', null, payload.filePath);
			link.addEventListener('click', function () {
				vscode.postMessage({ type: 'openTraceFile', path: payload.filePath });
			});
			fileLine.appendChild(link);
			summaryEl.appendChild(fileLine);
		}

		summaryEl.classList.add('visible');
		rowsEl.scrollTop = rowsEl.scrollHeight;
	}

	// ---- filter ----------------------------------------------------------

	function applyFilterTo(block) {
		const needle = filterEl.value.trim().toLowerCase();
		const haystack = block.textContent.toLowerCase() + (block.dataset.fullStatement || '');
		block.style.display = !needle || haystack.includes(needle) ? '' : 'none';
	}

	filterEl.addEventListener('input', function () {
		rowsEl.querySelectorAll('.step').forEach(applyFilterTo);
	});

	// ---- wire ------------------------------------------------------------

	window.addEventListener('message', function (event) {
		const m = event.data;
		if (m.type === 'start') {
			reset(m.payload);
		} else if (m.type === 'step') {
			addStep(m.payload);
		} else if (m.type === 'summary') {
			showSummary(m.payload);
		} else if (m.type === 'ended') {
			// A74 review MED-2: the session died without a summary event (infrastructure
			// fault) — stop claiming the run is live; the Debug Console has the details.
			if (statusEl) {
				statusEl.textContent = 'ended — see Debug Console';
				statusEl.classList.add('incomplete');
			}
		}
	});

	// Handshake: the extension buffers events until the webview script is actually
	// listening (messages posted into a still-loading webview are dropped).
	vscode.postMessage({ type: 'ready' });
})();
