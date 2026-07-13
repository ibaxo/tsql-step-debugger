using Microsoft.Data.SqlClient;

namespace TsqlDbg.Core.Execution;

// DESIGN §3/§4: the one real connection the session owns. §4 step 2 registers the
// InfoMessage handler with FireInfoMessageEventOnUserErrors = false and routes
// messages to the Debug Console; here they are surfaced via BatchResult.Messages and
// it is the caller's (Session's) job to forward them onward (console/trace).
//
// DESIGN §4 step 5 sends "BEGIN TRANSACTION;" as literal T-SQL text rather than using
// SqlTransaction/BeginTransaction(): the debugger's transaction is a genuine T-SQL
// session transaction that composed batches (§7.1) and the debuggee's own
// BEGIN/COMMIT/ROLLBACK statements share and observe via @@TRANCOUNT/XACT_STATE()
// (§10.4 watchdog). Wrapping it in ADO.NET's SqlTransaction object would fight that
// model, so this executor never sets SqlCommand.Transaction.
//
// DESIGN §20.5 / C20: MARS off, one command in flight at a time — this type does not
// need to guard against concurrent ExecuteAsync calls.
public sealed class SqlStatementExecutor : IStatementExecutor
{
    private readonly SqlConnection _connection;
    private readonly int _commandTimeoutSeconds;
    private List<string>? _pendingMessages;
    private List<BatchTrailingError>? _pendingAbsorbed;   // non-null only inside ExecuteAbsorbingAsync (D5/A13)

    public SqlStatementExecutor(SqlConnection connection, int commandTimeoutSeconds)
    {
        _connection = connection;
        _commandTimeoutSeconds = commandTimeoutSeconds;
        _connection.FireInfoMessageEventOnUserErrors = false;
        _connection.InfoMessage += OnInfoMessage;
    }

    public Task<BatchResult> ExecuteAsync(string batchText, CancellationToken cancellationToken = default)
        => ExecuteCoreAsync(batchText, null, cancellationToken);

    public Task<BatchResult> ExecuteAsync(string batchText, IReadOnlyList<BatchParameter> parameters, CancellationToken cancellationToken = default)
        => ExecuteCoreAsync(batchText, parameters, cancellationToken);

    // D5/A13 (§10.1, fact 24 Group A): oracle-free stepped-over EXEC. Severity ≤ 16
    // errors arrive through InfoMessage instead of throwing (FireInfoMessageEventOnUserErrors
    // is a CONNECTION-level switch — safe to toggle around one command under the §3/C20
    // one-command-at-a-time contract); each is recorded structurally on AbsorbedErrors
    // AND as native-client error text in Messages, preserving stream order relative to
    // PRINT output. Severity ≥ 17, attention/timeout, and transport failures keep the
    // standard throwing contract (§10.1 classes are unchanged).
    public async Task<BatchResult> ExecuteAbsorbingAsync(string batchText, CancellationToken cancellationToken = default)
    {
        var absorbed = new List<BatchTrailingError>();
        _pendingAbsorbed = absorbed;
        _connection.FireInfoMessageEventOnUserErrors = true;
        try
        {
            var result = await ExecuteCoreAsync(batchText, null, cancellationToken).ConfigureAwait(false);
            return absorbed.Count > 0 ? result with { AbsorbedErrors = absorbed } : result;
        }
        finally
        {
            _connection.FireInfoMessageEventOnUserErrors = false;
            _pendingAbsorbed = null;
        }
    }

    // DESIGN §10.5 / engine-facts fact 30, ruling on m6-boosted-attention-triage-fable.md
    // §5 (Ivan, 2026-07-07, candidate (d)): commands execute on the driver's SYNCHRONOUS
    // path, on a worker thread. The async path (ExecuteReaderAsync) cannot deliver an
    // attention to a batch that is not streaming results — SqlCommand.Cancel() blocks
    // until ≈ the batch's natural end (fact 30b) — which made a pause into a
    // compute-bound boosted region inert for the batch's whole runtime. On the sync
    // path the engine kills even a pure-compute loop within milliseconds (fact 30a,
    // native parity 30d). Cost: one blocked worker per in-flight batch, bounded at one
    // by the §3/C20 one-command-at-a-time contract. Cancellation now surfaces as the
    // wrapped SqlException Number 0 ("Operation cancelled by user"), the shape both
    // HandleExecutorFailureAsync (§10.5 attention) and HandleBoostedBatchDeathAsync
    // (B7) classify.
    private Task<BatchResult> ExecuteCoreAsync(
        string batchText, IReadOnlyList<BatchParameter>? parameters, CancellationToken cancellationToken)
        => Task.Run(() => ExecuteCore(batchText, parameters, cancellationToken));

    private BatchResult ExecuteCore(
        string batchText, IReadOnlyList<BatchParameter>? parameters, CancellationToken cancellationToken)
    {
        var messages = new List<string>();
        // Sets are appended EAGERLY (rows fill in place) so that a SqlException raised
        // while crossing the batch's trailing TDS tokens — 3998 may surface from either
        // the last set's final Read or the NextResult after it, depending on where
        // SqlClient meets the error token — cannot discard rows that had already
        // streamed. §10.4/fact 22 depends on the control row surviving that throw.
        var resultSets = new List<ResultSet>();
        _pendingMessages = messages;
        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText = batchText;
            command.CommandTimeout = _commandTimeoutSeconds;
            command.CommandType = System.Data.CommandType.Text;
            if (parameters is not null)
            {
                foreach (var p in parameters)
                {
                    var sqlParameter = command.Parameters.AddWithValue(p.Name, p.Value ?? DBNull.Value);
                    // AddWithValue infers datetime (3.33ms precision) for DateTime —
                    // force datetime2 so snapshot round trips don't lose precision; the
                    // batch text CONVERTs to the declared type on top (§10.4).
                    if (p.Value is DateTime)
                    {
                        sqlParameter.SqlDbType = System.Data.SqlDbType.DateTime2;
                    }
                }
            }

            // A pre-cancelled token never reaches the server (OCE, nothing executed —
            // the §10.5 nothing-ran shape). After this check there is a sub-ms window
            // where a cancel fires before ExecuteReader enters the cancelable state and
            // Cancel() no-ops: the batch then runs to its natural end and the pause
            // lands at the next step arrival — the pre-fact-30 behavior, safe via B7.
            cancellationToken.ThrowIfCancellationRequested();
            using var attention = cancellationToken.Register(() =>
            {
                try
                {
                    command.Cancel();
                }
                catch
                {
                    // Cancel on a completed/disposed command is a benign race — the
                    // outcome is already decided; nothing to deliver the attention to.
                }
            });

            using (var reader = command.ExecuteReader())
            {
                var moreResults = true;
                while (moreResults)
                {
                    var columns = new string[reader.FieldCount];
                    for (var i = 0; i < reader.FieldCount; i++)
                    {
                        columns[i] = reader.GetName(i);
                    }

                    var rows = new List<IReadOnlyList<object?>>();
                    resultSets.Add(new ResultSet(columns, rows));
                    while (reader.Read())
                    {
                        var row = new object?[columns.Length];
                        for (var i = 0; i < columns.Length; i++)
                        {
                            var value = reader.GetValue(i);
                            row[i] = value is DBNull ? null : value;
                        }

                        rows.Add(row);
                    }

                    moreResults = reader.NextResult();
                }
            }

            return new BatchResult(resultSets, messages);
        }
        catch (SqlException ex) when (IsBatchEndEpilogue(ex))
        {
            // DESIGN §10.4 / engine fact 22: a batch that ends with a doomed transaction
            // raises 3998 as a SEPARATE trailing message in the same TDS response, after
            // every result set (including the §7.3 control row) has streamed, and the
            // engine force-rolls the transaction back. That is not a §10.1
            // no-control-row failure — everything Session needs already materialized —
            // so return it, flagged; Session's watchdog verifies the control row really
            // was doomed (xact_state = -1) and treats anything else as a genuine fault.
            var trailing = new List<BatchTrailingError>(ex.Errors.Count);
            foreach (SqlError error in ex.Errors)
            {
                trailing.Add(new BatchTrailingError(error.Number, error.Class, error.Message));
            }

            return new BatchResult(resultSets, messages, trailing);
        }
        catch (SqlException ex)
        {
            // §10.1: no control row came back — compile/deferred-resolution class,
            // severity >= 20, broken connection, or timeout (Number -2). Session
            // classifies; the wrap keeps that classification fake-testable (§20.2).
            // Procedure/LineNumber ride along for the M4 cross-frame route (§10.2).
            // M8 (§8.3): a severed/reset connection that SqlClient did not raise as
            // severity ≥ 20 still leaves the connection non-Open — flag it so Session's
            // multi-batch continuation ends the session instead of advancing onto a dead
            // connection (design note §8.3).
            var firstError = ex.Errors.Count > 0 ? ex.Errors[0] : null;
            throw new StatementExecutionException(
                ex.Message, ex.Class, ex.Number, ex,
                firstError?.Procedure, firstError?.LineNumber ?? 0,
                connectionBroken: _connection.State != System.Data.ConnectionState.Open);
        }
        finally
        {
            _pendingMessages = null;
        }
    }

    // Fact 22: the doomed batch-end epilogue is 3998 — definitionally an END-of-batch
    // error, so it cannot be the reason a batch produced nothing, which makes this a
    // safe executor-level test needing no knowledge of the §7.3 control-row contract.
    // 266 rides with it: a parameterized composed batch travels via sp_executesql, and
    // the §10.4 redoom preamble's BEGIN TRANSACTION nets a trancount change across
    // that module boundary, so the server appends 266 ("Transaction count after
    // EXECUTE…") at module exit — BEFORE the batch-end 3998. SqlClient may throw as it
    // crosses the 266 token with the 3998 still unread, so the exception can carry
    // {266} alone, {3998} alone, or both. All three are the same doomed-batch-end
    // shape: 266 only ever trails our own scaffolding's net trancount change (a
    // debuggee-caused 266 raises inside the oracle TRY and is caught there), and a
    // doomed-mode batch that exits doom in-batch (the debuggee's ROLLBACK) nets
    // trancount 0 and raises neither. Session still verifies the control row reported
    // xact_state = -1 before accepting the result; anything else in the collection
    // stays on the §10.1 propagate path.
    private static bool IsBatchEndEpilogue(SqlException ex)
    {
        if (ex.Errors.Count == 0)
        {
            return false;
        }

        foreach (SqlError error in ex.Errors)
        {
            if (error.Number is not (3998 or 266))
            {
                return false;
            }
        }

        return true;
    }

    private void OnInfoMessage(object sender, SqlInfoMessageEventArgs e)
    {
        if (_pendingAbsorbed is { } absorbed)
        {
            // Absorbing mode (D5/A13): split the event per error. Class ≥ 11 entries
            // are real errors the flag downgraded to messages — record them
            // structurally and as the text a native client would print; Class ≤ 10
            // stays the plain PRINT/info surface.
            foreach (SqlError error in e.Errors)
            {
                if (error.Class >= 11)
                {
                    absorbed.Add(new BatchTrailingError(error.Number, error.Class, error.Message));
                    _pendingMessages?.Add(
                        $"Msg {error.Number}, Level {error.Class}, State {error.State}" +
                        (string.IsNullOrEmpty(error.Procedure) ? string.Empty : $", Procedure {error.Procedure}") +
                        $", Line {error.LineNumber}\n{error.Message}");
                }
                else
                {
                    _pendingMessages?.Add(error.Message);
                }
            }

            return;
        }

        _pendingMessages?.Add(e.Message);
    }

    public ValueTask DisposeAsync()
    {
        _connection.InfoMessage -= OnInfoMessage;
        return ValueTask.CompletedTask;
    }
}
