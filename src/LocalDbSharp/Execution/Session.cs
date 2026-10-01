using LocalDbSharp.Execution;
using LocalDbSharp.Parsing;
using LocalDbSharp.Storage;

namespace LocalDbSharp;

/// <summary>
/// A connection-like execution context: current database, variables, temp tables and transaction state.
/// </summary>
public sealed partial class Session : IDisposable
{
    internal readonly LocalDbEngine Engine;
    internal Database Db;
    readonly int _spid;
    internal readonly Dictionary<string, Table> TempTables = new(StringComparer.OrdinalIgnoreCase);
    readonly List<Frame> _frames = [];
    ExecutionResult _sink = new();
    int _rowCount;
    decimal? _identity;          // @@IDENTITY: last identity value generated in the session, in any scope
    decimal? _rootScopeIdentity; // SCOPE_IDENTITY() of the ad-hoc batch scope, kept across batches
    bool _doomed;                // the open transaction can only be rolled back (XACT_STATE() = -1)
    int _triggerDepth;
    bool _noCount;
    int _tranCount, _atomicDepth, _nestLevel;
    int _tryDepth, _functionDepth, _lastError;
    bool _xactAbort;
    TSqlException? _firstError;
    bool _dayFirst;
    int _rowCountLimit;
    /// <summary>SET DATEFIRST: 1 = Monday ... 7 = Sunday (the us_english default).</summary>
    internal int DateFirst = 7;
    string? _tranName;
    readonly List<UndoEntry> _undo = [];
    readonly List<(string Name, int Mark)> _savepoints = [];
    readonly List<TSqlException> _errors = [];
    readonly Dictionary<string, CursorState> _cursors = new(StringComparer.OrdinalIgnoreCase);
    readonly List<Trigger> _activeTriggers = [];
    int _fetchStatus = -1;
    object? _returnValue;
    readonly Dictionary<object, (Frame Frame, int Version, RowSet Rows)> _stmtCache = [];
    readonly Dictionary<Expr, SqlType?> _typeCache = [];
    TableRef? _rowRefTarget;
    internal readonly Random Random = new();

    /// <summary>Raised for PRINT output and informational RAISERROR messages.</summary>
    public event Action<string>? InfoMessage;

    public string Database => Db.Name;
    public bool InTransaction => _tranCount > 0;

    internal Session(LocalDbEngine engine, Database db, int spid)
    {
        Engine = engine;
        Db = db;
        _spid = spid;
    }

    Frame Frame => _frames[^1];

    public void Dispose()
    {
        lock (Engine.Sync)
        {
            if (_tranCount > 0) RollbackAll();
            TempTables.Clear();
            _cursors.Clear();
        }
    }

    // ------------------------------------------------------------ public API

    public ExecutionResult Execute(string sql) => Execute(sql, (IList<SessionParameter>?)null);

    public ExecutionResult Execute(string sql, params (string Name, object? Value)[] parameters)
        => Execute(sql, parameters.Select(p => new SessionParameter(p.Name, p.Value)).ToList());

    /// <summary>Executes a script (one or more batches separated by GO) and returns all result sets.</summary>
    public ExecutionResult Execute(string sql, IList<SessionParameter>? parameters)
    {
        lock (Engine.Sync)
        {
            var batches = Engine.Parse(sql);
            TypeSystem.DayFirst = _dayFirst; // conversions read the session's date format from the executing thread
            var result = new ExecutionResult();
            var savedSink = _sink;
            _sink = result;
            try
            {
                foreach (var batch in batches)
                {
                    if (batch.Error != null) throw batch.Error;
                    var frame = new Frame { ScopeIdentity = _rootScopeIdentity };
                    if (parameters != null)
                    {
                        foreach (var p in parameters)
                        {
                            string name = p.Name.StartsWith('@') ? p.Name : "@" + p.Name;
                            if (p.Value is System.Data.DataTable tvp)
                            {
                                // table-valued parameter
                                _frames.Add(frame);
                                try { frame.Vars[name] = new Variable { Table = TableFromDataTable(tvp, p.TypeName, name) }; }
                                finally { _frames.RemoveAt(_frames.Count - 1); }
                                continue;
                            }
                            object? value = FromClr(p.Value);
                            var type = p.Type ?? (value == null ? null : ParameterType(value));
                            frame.Vars[name] = new Variable { Type = type, Value = type == null ? value : TypeSystem.ConvertTo(value, type) };
                        }
                    }
                    _frames.Add(frame);
                    try
                    {
                        ExecBlock(batch.Statements);
                        if (_doomed && _tranCount > 0)
                        {
                            RollbackAll();
                            _firstError ??= Err.New(3998, "Uncommittable transaction is detected at the end of the batch. The transaction is rolled back.");
                        }
                    }
                    finally
                    {
                        _rootScopeIdentity = frame.ScopeIdentity;
                        _frames.RemoveAt(_frames.Count - 1);
                        DropFrameTempTables(frame);
                        if (parameters != null)
                        {
                            foreach (var p in parameters)
                            {
                                if (!p.IsOutput) continue;
                                string name = p.Name.StartsWith('@') ? p.Name : "@" + p.Name;
                                if (frame.Vars[name].Table == null) p.Value = frame.Vars[name].Value;
                            }
                        }
                    }
                }
            }
            catch (TSqlException) when (_firstError != null)
            {
                // the batch was aborted, but an earlier statement had already failed: that error comes first
                var first = _firstError;
                _firstError = null;
                throw first;
            }
            catch
            {
                _firstError = null;
                throw;
            }
            finally
            {
                _sink = savedSink;
                _tryDepth = 0;
                _functionDepth = 0;
                _triggerDepth = 0;
                _nestLevel = 0;
                _errors.Clear();
                _rowRefTarget = null;
            }
            if (_firstError != null)
            {
                var error = _firstError;
                _firstError = null;
                throw error;
            }
            return result;
        }
    }

    public void BeginTransaction()
    {
        lock (Engine.Sync) _tranCount++;
    }

    public void Commit()
    {
        lock (Engine.Sync) CommitTran();
    }

    public void Rollback()
    {
        lock (Engine.Sync) RollbackAll();
    }

    static SqlType ParameterType(object value) => value switch
    {
        string s => s.Length <= 4000 ? SqlType.NVarChar(4000) : SqlType.NVarCharMax,
        byte[] b => b.Length <= 8000 ? new SqlType(SqlTypeKind.VarBinary, 8000) : SqlType.VarBinaryMax,
        decimal d => SqlType.Decimal(38, d.Scale),
        DateTime => SqlType.DateTime2,
        _ => TypeSystem.TypeOf(value),
    };

    internal static object? FromClr(object? v) => v switch
    {
        null or DBNull => null,
        bool or byte or short or int or long or decimal or float or double or string or DateTime or DateTimeOffset or TimeSpan or Guid or byte[] => v,
        char c => c.ToString(),
        sbyte x => (short)x,
        ushort x => (int)x,
        uint x => (long)x,
        ulong x => (decimal)x,
        DateOnly d => d.ToDateTime(TimeOnly.MinValue),
        TimeOnly t => t.ToTimeSpan(),
        Enum e => Convert.ToInt32(e),
        char[] cs => new string(cs),
        _ => throw new ArgumentException($"Unsupported parameter value type {v.GetType().Name}."),
    };

    // ------------------------------------------------------------ transactions

    void Log(UndoEntry e) => _undo.Add(e);

    /// <summary>
    /// Undoes logged changes back to 'mark'. Table variables take part in statement-level rollback
    /// (a failed statement leaves no partial changes) but are not affected by transaction rollback.
    /// </summary>
    void RollbackTo(int mark, bool statementLevel = false)
    {
        HashSet<Table>? dirty = null;
        for (int i = _undo.Count - 1; i >= mark; i--)
        {
            var u = _undo[i];
            if (!statementLevel && u.Table is { IsVariable: true }) continue;
            if (u.Custom != null) u.Custom();
            else if (u.InsertedRow != null) u.Table!.UndoInsert(u.InsertedRow, dirty == null || !dirty.Contains(u.Table));
            else if (u.UpdatedRow != null)
            {
                Array.Copy(u.OldValues!, u.UpdatedRow, Math.Min(u.OldValues!.Length, u.UpdatedRow.Length));
                (dirty ??= []).Add(u.Table!);
            }
            else if (u.OldRows != null)
            {
                u.Table!.Rows = u.OldRows;
                (dirty ??= []).Add(u.Table);
            }
        }
        _undo.RemoveRange(mark, _undo.Count - mark);
        if (dirty != null)
        {
            foreach (var t in dirty)
            {
                t.RebuildIndexes();
                t.Resort();
            }
        }
    }

    void RollbackAll()
    {
        RollbackTo(0);
        _doomed = false;
        _tranCount = 0;
        _savepoints.Clear();
    }

    void CommitTran()
    {
        if (_tranCount == 0) throw Err.New(3902, "The COMMIT TRANSACTION request has no corresponding BEGIN TRANSACTION.");
        if (_doomed) throw Err.New(3930, "The current transaction cannot be committed and cannot support operations that write to the log file. Roll back the transaction.");
        if (--_tranCount == 0)
        {
            _undo.Clear();
            _savepoints.Clear();
        }
    }

    /// <summary>Runs a data-modifying statement so that it either completes or leaves no trace.</summary>
    void Atomic(Action action)
    {
        int mark = _undo.Count;
        _atomicDepth++;
        try
        {
            action();
        }
        catch (TSqlException ex) when (ex.Number == 3609)
        {
            // a trigger rolled the transaction back: everything up to that point is already undone, and
            // whatever the trigger did afterwards stays
            if (_tranCount == 0) _undo.Clear();
            throw;
        }
        catch
        {
            RollbackTo(Math.Min(mark, _undo.Count), statementLevel: true);
            throw;
        }
        finally
        {
            _atomicDepth--;
        }
        if (_tranCount == 0 && _atomicDepth == 0) _undo.Clear();
    }

    void ExecTran(TranStmt s)
    {
        switch (s.Kind)
        {
            case TranKind.Begin:
                if (_tranCount++ == 0) _tranName = s.Name != null ? TranName(s.Name) : null;
                break;
            case TranKind.Commit:
                CommitTran();
                break;
            case TranKind.Save:
                if (_tranCount == 0) throw Err.New(628, "Cannot issue SAVE TRANSACTION when there is no active transaction.");
                _savepoints.Add((TranName(s.Name!), _undo.Count));
                break;
            case TranKind.Rollback:
                if (_tranCount == 0) throw Err.New(3903, "The ROLLBACK TRANSACTION request has no corresponding BEGIN TRANSACTION.");
                if (s.Name != null)
                {
                    string name = TranName(s.Name);
                    int ix = _savepoints.FindLastIndex(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                    if (ix >= 0)
                    {
                        // rolling back to a savepoint also releases it, together with any later ones
                        RollbackTo(_savepoints[ix].Mark);
                        _savepoints.RemoveRange(ix, _savepoints.Count - ix);
                        break;
                    }
                    // otherwise the name must be that of the outermost transaction
                    if (!name.Equals(_tranName, StringComparison.OrdinalIgnoreCase))
                        throw Err.New(6401, $"Cannot roll back {name}. No transaction or savepoint of that name was found.");
                }
                RollbackAll();
                break;
        }
    }

    string TranName(string name) => name.StartsWith('@') ? TypeSystem.ToStr(GetVar(name) ?? "") : name;

    // ------------------------------------------------------------ variables

    internal object? GetVar(string name)
    {
        if (name.StartsWith("@@", StringComparison.Ordinal)) return GetGlobal(name);
        if (_frames.Count > 0 && Frame.Vars.TryGetValue(name, out var v))
        {
            if (v.Table != null) throw Err.New(137, $"Must declare the scalar variable \"{name}\".", 15);
            return v.Value;
        }
        throw Err.New(137, $"Must declare the scalar variable \"{name}\".", 15);
    }

    Variable FindVar(string name)
    {
        if (_frames.Count > 0 && Frame.Vars.TryGetValue(name, out var v) && v.Table == null) return v;
        throw Err.New(137, $"Must declare the scalar variable \"{name}\".", 15);
    }

    void SetVar(string name, object? value)
    {
        var v = FindVar(name);
        v.Value = v.Type == null ? value : TypeSystem.ConvertTo(value, v.Type);
        Frame.Version++;
    }

    void SetVar(string name, string op, object? value)
    {
        if (op != "=") value = TypeSystem.Arith(op[..1], GetVar(name), value);
        SetVar(name, value);
    }

    object? GetGlobal(string name)
    {
        switch (name.ToUpperInvariant())
        {
            case "@@ROWCOUNT": return _rowCount;
            case "@@IDENTITY": return _identity;
            case "@@TRANCOUNT": return _tranCount;
            case "@@ERROR": return _lastError;
            case "@@FETCH_STATUS": return _fetchStatus;
            case "@@VERSION": return "LocalDbSharp 1.0 (in-memory T-SQL engine) - Microsoft SQL Server compatible dialect";
            case "@@SERVERNAME": return "LOCALDBSHARP";
            case "@@SERVICENAME": return "LOCALDBSHARP";
            case "@@SPID": return (short)_spid;
            case "@@NESTLEVEL": return _nestLevel;
            case "@@LANGUAGE": return "us_english";
            case "@@DATEFIRST": return (byte)DateFirst;
            case "@@MAX_PRECISION": return (byte)38;
            case "@@LOCK_TIMEOUT": return -1;
            case "@@TEXTSIZE": return int.MaxValue;
            case "@@OPTIONS": return 5496;
            case "@@PROCID": return 0;
            case "@@DBTS": return RowVersionBytes(Db.RowVersion);
            default: throw Err.New(137, $"Must declare the scalar variable \"{name}\".", 15);
        }
    }

    // ------------------------------------------------------------ statement dispatch

    Flow ExecBlock(List<Statement> statements)
    {
        foreach (var s in statements)
        {
            var flow = ExecStatement(s);
            if (flow != Flow.Next) return flow;
        }
        return Flow.Next;
    }

    Flow ExecStatement(Statement s)
    {
        _stmtCache.Clear();
        _typeCache.Clear();
        _randCache.Clear();
        bool leaf = s is not (BlockStmt or IfStmt or WhileStmt or TryCatchStmt);
        try
        {
            var flow = ExecCore(s);
            if (leaf && s is not (DeclareStmt or ReturnStmt or BreakStmt or ContinueStmt)) _lastError = 0;
            return flow;
        }
        catch (TSqlException ex)
        {
            if (ex.LineNumber == 0) ex.LineNumber = s.Line;
            _lastError = ex.Number;
            if (!leaf) throw;
            // XACT_ABORT (always in effect inside triggers) and conversion failures abort the transaction;
            // RAISERROR never does
            bool fatal = !ex.FromRaiserror && (_xactAbort || _triggerDepth > 0 || ex.RollsBackTransaction);
            if (_tryDepth > 0)
            {
                // control goes to the CATCH block; the transaction survives but can only be rolled back
                // (under XACT_ABORT that holds for every caught error, RAISERROR included)
                if ((fatal || _xactAbort) && _tranCount > 0) _doomed = true;
                throw;
            }
            if (_functionDepth > 0) throw;
            if (fatal)
            {
                if (_tranCount > 0) RollbackAll();
                ex.ForceBatchAbort = true;
                throw;
            }
            if (ex.AbortsBatch) throw;
            // statement-terminating error: remember it and carry on with the next statement
            _firstError ??= ex;
            return Flow.Next;
        }
        catch (InsufficientExecutionStackException)
        {
            throw new TSqlException(217, "Maximum stored procedure, function, trigger, or view nesting level exceeded (limit 32).") { LineNumber = s.Line };
        }
    }

    void AddAffected(int count)
    {
        _rowCount = count;
        if (_noCount) return;
        _sink.RecordsAffected = _sink.RecordsAffected < 0 ? count : _sink.RecordsAffected + count;
    }

    Flow ExecCore(Statement stmt)
    {
        switch (stmt)
        {
            case SelectStmt s: ExecSelectStatement(s.Q); return Flow.Next;
            case InsertStmt s: Atomic(() => ExecInsert(s)); return Flow.Next;
            case UpdateStmt s: Atomic(() => ExecUpdate(s)); return Flow.Next;
            case DeleteStmt s: Atomic(() => ExecDelete(s)); return Flow.Next;
            case MergeStmt s: Atomic(() => ExecMerge(s)); return Flow.Next;
            case TruncateStmt s: Atomic(() => ExecTruncate(s)); return Flow.Next;
            case CreateTableStmt s: Atomic(() => ExecCreateTable(s)); return Flow.Next;
            case AlterTableStmt s: Atomic(() => ExecAlterTable(s)); return Flow.Next;
            case DropStmt s: Atomic(() => ExecDrop(s)); return Flow.Next;
            case CreateIndexStmt s: ExecCreateIndex(s); return Flow.Next;
            case CreateViewStmt s: ExecCreateView(s); return Flow.Next;
            case CreateProcStmt s: ExecCreateProc(s); return Flow.Next;
            case CreateFunctionStmt s: ExecCreateFunction(s); return Flow.Next;
            case CreateSequenceStmt s: ExecCreateSequence(s); return Flow.Next;
            case CreateTypeStmt s: ExecCreateType(s); return Flow.Next;
            case AlterSequenceStmt s: ExecAlterSequence(s); return Flow.Next;
            case CreateTriggerStmt s: ExecCreateTrigger(s); return Flow.Next;
            case EnableTriggerStmt s: ExecEnableTrigger(s); return Flow.Next;
            case DbccStmt s: ExecDbcc(s); return Flow.Next;
            case CreateSchemaStmt s:
                if (Db.HasSchema(s.Name)) throw Err.New(2714, $"There is already an object named '{s.Name}' in the database.");
                Db.Schemas.Add(s.Name);
                return Flow.Next;
            case CreateDatabaseStmt s:
                if (Engine.Databases.ContainsKey(s.Name)) throw Err.New(1801, $"Database '{s.Name}' already exists. Choose a different database name.");
                Engine.AddDatabase(s.Name);
                return Flow.Next;
            case UseStmt s:
                Db = Engine.Databases.GetValueOrDefault(s.Name)
                     ?? throw Err.New(911, $"Database '{s.Name}' does not exist. Make sure that the name is entered correctly.");
                return Flow.Next;
            case DeclareStmt s: ExecDeclare(s); return Flow.Next;
            case SetVarStmt s:
                SetVar(s.Name, s.Op, EvalFor(s.Value, null, FindVar(s.Name).Type));
                _rowCount = 1;
                return Flow.Next;
            case SetOptionStmt s: ExecSetOption(s); return Flow.Next;
            case IfStmt s:
                if (IsTrue(Eval(s.Cond, null))) return ExecStatement(s.Then);
                return s.Else != null ? ExecStatement(s.Else) : Flow.Next;
            case WhileStmt s:
                while (true)
                {
                    _stmtCache.Clear();
                    if (!IsTrue(Eval(s.Cond, null))) break;
                    var flow = ExecStatement(s.Body);
                    if (flow == Flow.Break) break;
                    if (flow == Flow.Return) return flow;
                }
                return Flow.Next;
            case BlockStmt s: return ExecBlock(s.Body);
            case TryCatchStmt s: return ExecTryCatch(s);
            case BreakStmt: return Flow.Break;
            case ContinueStmt: return Flow.Continue;
            case ReturnStmt s:
                _returnValue = s.Value != null ? Eval(s.Value, null) : null;
                return Flow.Return;
            case PrintStmt s:
            {
                object? v = Eval(s.Value, null);
                Message(v == null ? "" : TypeSystem.ToStr(v, 0, InferType(s.Value, null)));
                return Flow.Next;
            }
            case ThrowStmt s: throw BuildThrow(s);
            case RaiserrorStmt s: ExecRaiserror(s); return Flow.Next;
            case ExecStmt s: ExecExec(s); return Flow.Next;
            case TranStmt s: ExecTran(s); return Flow.Next;
            case DeclareCursorStmt s:
                if (_cursors.ContainsKey(s.Name)) throw Err.New(16915, $"A cursor with the name '{s.Name}' already exists.");
                _cursors[s.Name] = new CursorState { Query = s.Query };
                return Flow.Next;
            case CursorStmt s: ExecCursor(s); return Flow.Next;
            case FetchStmt s: ExecFetch(s); return Flow.Next;
            default: throw Err.NotSupported(stmt.GetType().Name);
        }
    }

    void Message(string text)
    {
        _sink.Messages.Add(text);
        InfoMessage?.Invoke(text);
    }

    // ------------------------------------------------------------ declarations & options

    void ExecDeclare(DeclareStmt s)
    {
        foreach (var item in s.Items)
        {
            if (Frame.Vars.TryGetValue(item.Name, out var existing))
            {
                // A declaration is not an executable statement in T-SQL: reaching it again (inside a loop)
                // keeps the variable and only re-runs its initializer.
                if (existing.Table == null && item.Init != null) SetVar(item.Name, EvalFor(item.Init, null, existing.Type));
                continue;
            }
            if (item.Table != null)
            {
                var t = BuildTable(item.Table, "dbo", item.Name, Db);
                t.IsVariable = true;
                Frame.Vars[item.Name] = new Variable { Table = t };
                continue;
            }
            var type = item.Type;
            if (item.UserType != null)
            {
                var userType = ResolveUserType(item.UserType);
                if (userType.TableDef != null)
                {
                    Frame.Vars[item.Name] = new Variable { Table = NewTableOfType(userType, item.Name) };
                    continue;
                }
                type = userType.Base;
            }
            var v = new Variable { Type = type };
            Frame.Vars[item.Name] = v;
            if (item.Init != null)
            {
                v.Value = TypeSystem.ConvertTo(EvalFor(item.Init, null, type), type!);
                Frame.Version++;
            }
        }
    }

    void ExecSetOption(SetOptionStmt s)
    {
        switch (s.Option)
        {
            case "NOCOUNT":
                _noCount = string.Equals(s.Value, "ON", StringComparison.OrdinalIgnoreCase);
                break;
            case "XACT_ABORT":
                _xactAbort = string.Equals(s.Value, "ON", StringComparison.OrdinalIgnoreCase);
                break;
            case "DATEFIRST":
                if (!int.TryParse(s.Value, out int first) || first < 1 || first > 7)
                    throw Err.New(8105, $"'{s.Value}' is not a valid value for the SET DATEFIRST option.");
                DateFirst = first;
                break;
            case "DATEFORMAT":
                _dayFirst = string.Equals(s.Value, "dmy", StringComparison.OrdinalIgnoreCase);
                TypeSystem.DayFirst = _dayFirst;
                break;
            case "LANGUAGE":
            {
                // only the settings that change how dates are read are modelled
                bool british = s.Value != null && (s.Value.Contains("brit", StringComparison.OrdinalIgnoreCase) || s.Value.Equals("French", StringComparison.OrdinalIgnoreCase)
                                                    || s.Value.Equals("German", StringComparison.OrdinalIgnoreCase) || s.Value.Equals("Italian", StringComparison.OrdinalIgnoreCase)
                                                    || s.Value.Equals("Spanish", StringComparison.OrdinalIgnoreCase));
                _dayFirst = british;
                DateFirst = british ? 1 : 7;
                TypeSystem.DayFirst = _dayFirst;
                break;
            }
            case "ROWCOUNT":
                _rowCountLimit = int.TryParse(s.Value, out int limit) && limit > 0 ? limit : 0;
                break;
            case "CONCAT_NULL_YIELDS_NULL" or "ANSI_NULLS" or "ANSI_WARNINGS" or "ARITHABORT" or "ANSI_PADDING" or "QUOTED_IDENTIFIER" when string.Equals(s.Value, "OFF", StringComparison.OrdinalIgnoreCase):
                throw Err.NotSupported($"SET {s.Option} OFF");
            case "IDENTITY_INSERT":
            {
                var t = ResolveTable(s.Table!);
                if (t.IdentityColumn < 0) throw Err.New(8106, $"Table '{t.FullName}' does not have the identity property. Cannot perform SET operation.");
                t.IdentityInsert = s.Value == "ON";
                break;
            }
            // every other option (ANSI_NULLS, XACT_ABORT, isolation level, ...) is accepted and ignored
        }
    }

    // ------------------------------------------------------------ errors

    Flow ExecTryCatch(TryCatchStmt s)
    {
        int frames = _frames.Count, nest = _nestLevel, tryDepth = _tryDepth, functionDepth = _functionDepth;
        var sink = _sink;
        var db = Db;
        _tryDepth++;
        try
        {
            return ExecBlock(s.Try);
        }
        catch (TSqlException ex) when (ex.Class > 10 && ex.Class < 20)
        {
            // unwind anything the failing statement left behind
            while (_frames.Count > frames) _frames.RemoveAt(_frames.Count - 1);
            _nestLevel = nest;
            _functionDepth = functionDepth;
            _sink = sink;
            Db = db;
            _tryDepth = tryDepth;
            _errors.Add(ex);
            try
            {
                return ExecBlock(s.Catch);
            }
            finally
            {
                _errors.RemoveAt(_errors.Count - 1);
            }
        }
        finally
        {
            _tryDepth = tryDepth;
        }
    }

    internal TSqlException? CurrentError => _errors.Count > 0 ? _errors[^1] : null;

    TSqlException BuildThrow(ThrowStmt s)
    {
        if (s.Number == null)
            return Rethrow(CurrentError) ?? Err.New(10704, "To rethrow an error, a THROW statement must be used inside a CATCH block. Insert the THROW statement inside a CATCH block, or add error parameters to the THROW statement.", 15);
        int number = (int)TypeSystem.ToLong(Eval(s.Number, null) ?? 50000);
        string msg = TypeSystem.ToStr(Eval(s.Message!, null) ?? "");
        byte state = (byte)TypeSystem.ToLong(Eval(s.State!, null) ?? 1);
        return new TSqlException(number, msg, 16, state) { Procedure = Frame.Procedure, ForceBatchAbort = true };
    }

    static TSqlException? Rethrow(TSqlException? e)
    {
        if (e != null) e.ForceBatchAbort = true;
        return e;
    }

    void ExecRaiserror(RaiserrorStmt s)
    {
        var args = s.Args.Select(a => Eval(a, null)).ToArray();
        int number = 50000;
        string msg;
        if (args[0] is string text) msg = text;
        else
        {
            number = (int)TypeSystem.ToLong(args[0] ?? 50000);
            msg = $"Error {number}";
        }
        byte severity = args.Length > 1 ? (byte)Math.Clamp(TypeSystem.ToLong(args[1] ?? 0), 0, 25) : (byte)16;
        byte state = args.Length > 2 ? (byte)Math.Clamp(TypeSystem.ToLong(args[2] ?? 1), 0, 255) : (byte)1;
        msg = Functions.FormatMessage(msg, args.Skip(3).ToArray());
        if (severity <= 10)
        {
            Message(msg);
            return;
        }
        throw new TSqlException(number, msg, severity, state) { Procedure = Frame.Procedure, FromRaiserror = true };
    }

    // ------------------------------------------------------------ cursors

    CursorState FindCursor(string name)
        => _cursors.GetValueOrDefault(name) ?? throw Err.New(16916, $"A cursor with the name '{name}' does not exist.");

    void ExecCursor(CursorStmt s)
    {
        var c = FindCursor(s.Name);
        switch (s.Op)
        {
            case CursorOp.Open:
                if (c.Rows != null) throw Err.New(16905, "The cursor is already open.");
                c.Rows = ExecQuery(c.Query, null, null).Rows;
                c.Position = -1;
                break;
            case CursorOp.Close:
                if (c.Rows == null) throw Err.New(16917, "Cursor is not open.");
                c.Rows = null;
                break;
            case CursorOp.Deallocate:
                _cursors.Remove(s.Name);
                break;
        }
    }

    void ExecFetch(FetchStmt s)
    {
        var c = FindCursor(s.Name);
        if (c.Rows == null) throw Err.New(16917, "Cursor is not open.");
        if (c.Position < c.Rows.Count) c.Position++;
        if (c.Position >= c.Rows.Count)
        {
            _fetchStatus = -1;
            return;
        }
        _fetchStatus = 0;
        var row = c.Rows[c.Position];
        if (s.Into.Count == 0)
        {
            var rs = ExecQuery(c.Query, null, null);
            _sink.ResultSets.Add(ToResultSet(new RowSet(rs.Schema, [row])));
            return;
        }
        if (s.Into.Count != row.Length)
            throw Err.New(16924, "Cursorfetch: The number of variables declared in the INTO list must match that of selected columns.");
        for (int i = 0; i < row.Length; i++) SetVar(s.Into[i], row[i]);
    }

    // ------------------------------------------------------------ EXEC

    void EnterNest()
    {
        if (++_nestLevel > 32)
        {
            _nestLevel--;
            throw Err.New(217, "Maximum stored procedure, function, trigger, or view nesting level exceeded (limit 32).");
        }
    }

    void DropFrameTempTables(Frame frame)
    {
        if (frame.TempTables != null)
            foreach (string name in frame.TempTables) TempTables.Remove(name);
        if (frame.ShadowedTempTables != null)
            foreach (var (name, outer) in frame.ShadowedTempTables) TempTables[name] = outer;
    }

    void RunInFrame(Frame frame, Action body)
    {
        EnterNest();
        _frames.Add(frame);
        try
        {
            body();
        }
        finally
        {
            _frames.Remove(frame);
            _nestLevel--;
            DropFrameTempTables(frame);
        }
    }

    void ExecDynamic(string sql, Frame frame)
    {
        var batches = Engine.Parse(sql);
        RunInFrame(frame, () =>
        {
            foreach (var batch in batches)
            {
                if (batch.Error != null) throw batch.Error;
                if (ExecBlock(batch.Statements) == Flow.Return) break;
            }
        });
    }

    object? EvalExecArg(ExecArg a)
        => a.Value is ColumnRef { Table: null } c ? c.Name : EvalArgument(a.Value, null);

    void ExecExec(ExecStmt s)
    {
        if (s.DynamicSql != null)
        {
            object? sql = Eval(s.DynamicSql, null);
            if (sql != null) ExecDynamic(TypeSystem.ToStr(sql), new Frame());
            return;
        }
        var name = s.Proc!;
        if (FindObject(name) is Procedure proc)
        {
            CallProcedure(proc, s);
            return;
        }
        bool systemSchema = name.Schema == null || name.Schema.Equals("sys", StringComparison.OrdinalIgnoreCase) || name.Schema.Equals("dbo", StringComparison.OrdinalIgnoreCase);
        if (systemSchema && TryExecSystemProcedure(s)) return;
        throw Err.New(2812, $"Could not find stored procedure '{name}'.");
    }

    void ExecSpExecuteSql(ExecStmt s)
    {
        if (s.Args.Count == 0) throw Err.New(201, "Procedure or function 'sp_executesql' expects parameter '@statement', which was not supplied.");
        string sql = TypeSystem.ToStr(EvalExecArg(s.Args[0]) ?? "");
        var frame = new Frame();
        var outputs = new List<(string Param, string CallerVar)>();
        if (s.Args.Count > 1)
        {
            var defs = Parser.ParseParamList(TypeSystem.ToStr(EvalExecArg(s.Args[1]) ?? ""));
            foreach (var d in defs) frame.Vars[d.Name] = new Variable { Type = d.Type };
            for (int i = 2; i < s.Args.Count; i++)
            {
                var a = s.Args[i];
                var def = a.Name != null
                    ? defs.FirstOrDefault(d => d.Name.Equals(a.Name, StringComparison.OrdinalIgnoreCase))
                      ?? throw Err.New(8145, $"{a.Name} is not a parameter for procedure sp_executesql.")
                    : i - 2 < defs.Count ? defs[i - 2] : throw Err.New(8144, "Procedure or function has too many arguments specified.");
                frame.Vars[def.Name].Value = TypeSystem.ConvertTo(EvalExecArg(a), def.Type);
                if (a.Output && a.Value is VarRef vr) outputs.Add((def.Name, vr.Name));
            }
        }
        ExecDynamic(sql, frame);
        foreach (var (param, callerVar) in outputs) SetVar(callerVar, frame.Vars[param].Value);
    }

    void CallProcedure(Procedure proc, ExecStmt s)
    {
        var def = proc.Def;
        var frame = new Frame { Procedure = proc.FullName };
        var outputs = new List<(string Param, string CallerVar)>();
        for (int i = 0; i < s.Args.Count; i++)
        {
            var a = s.Args[i];
            ParamDef p;
            if (a.Name != null)
                p = def.Params.FirstOrDefault(d => d.Name.Equals(a.Name, StringComparison.OrdinalIgnoreCase))
                    ?? throw Err.New(8145, $"{a.Name} is not a parameter for procedure {proc.Name}.");
            else if (i < def.Params.Count) p = def.Params[i];
            else throw Err.New(8144, $"Procedure or function {proc.Name} has too many arguments specified.");
            if (a.Value is DefaultExpr) continue;
            BindParameter(frame, p, EvalExecArg(a));
            if (a.Output && a.Value is VarRef vr) outputs.Add((p.Name, vr.Name));
        }
        foreach (var p in def.Params)
        {
            if (frame.Vars.ContainsKey(p.Name)) continue;
            if (p.Default == null && !IsTableParameter(p))
                throw Err.New(201, $"Procedure or function '{proc.Name}' expects parameter '{p.Name}', which was not supplied.");
            // an omitted table-valued parameter is an empty table
            BindParameter(frame, p, p.Default != null ? Eval(p.Default, null) : null);
        }
        var savedDb = Db;
        if (proc.Database != null) Db = proc.Database;
        _returnValue = null;
        int tranCountBefore = _tranCount;
        try
        {
            RunInFrame(frame, () => ExecBlock(def.Body));
        }
        catch (TSqlException ex)
        {
            ex.Procedure ??= proc.FullName;
            throw;
        }
        finally
        {
            Db = savedDb;
        }
        object? ret = _returnValue ?? 0;
        _returnValue = null;
        if (s.ReturnVar != null) SetVar(s.ReturnVar, ret);
        foreach (var (param, callerVar) in outputs) SetVar(callerVar, frame.Vars[param].Value);
        if (_tranCount != tranCountBefore)
            throw Err.New(266, $"Transaction count after EXECUTE indicates a mismatching number of BEGIN and COMMIT statements. Previous count = {tranCountBefore}, current count = {_tranCount}.");
    }

    /// <summary>Invokes a stored procedure by name with bound parameters (used by the ADO.NET provider).</summary>
    internal ExecutionResult ExecuteProcedure(string procName, IList<SessionParameter> parameters, out object? returnValue)
    {
        var sb = new System.Text.StringBuilder("EXEC @__ret = ").Append(procName);
        bool first = true;
        var all = new List<SessionParameter>(parameters) { new("@__ret", 0) { Type = SqlType.Int, IsOutput = true } };
        foreach (var p in parameters)
        {
            string n = p.Name.StartsWith('@') ? p.Name : "@" + p.Name;
            sb.Append(first ? " " : ", ").Append(n).Append(" = ").Append(n);
            if (p.IsOutput) sb.Append(" OUTPUT");
            first = false;
        }
        var result = Execute(sb.ToString(), all);
        returnValue = all[^1].Value;
        return result;
    }
}
