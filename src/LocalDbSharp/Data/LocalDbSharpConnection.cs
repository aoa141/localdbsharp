using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;

namespace LocalDbSharp.Data;

/// <summary>
/// ADO.NET connection to an in-memory <see cref="LocalDbEngine"/>.
/// Connection string keys: "Data Source" (name of a process-wide engine, default "(default)") and
/// "Initial Catalog" / "Database" (default "master"). Connections with the same Data Source share data.
/// </summary>
public sealed class LocalDbSharpConnection : DbConnection
{
    string _connectionString = "";
    LocalDbEngine? _engine;
    Session? _session;
    string _dataSource = "(default)";
    string _database = "master";
    internal LocalDbSharpTransaction? CurrentTransaction;

    public LocalDbSharpConnection() { }

    public LocalDbSharpConnection(string connectionString) => ConnectionString = connectionString;

    /// <summary>Creates a connection bound to a specific engine instance (not registered by name).</summary>
    public LocalDbSharpConnection(LocalDbEngine engine, string database = "master")
    {
        _engine = engine;
        _database = database;
    }

    [AllowNull]
    public override string ConnectionString
    {
        get => _connectionString;
        set
        {
            if (_session != null) throw new InvalidOperationException("The connection string cannot be changed while the connection is open.");
            _connectionString = value ?? "";
            var b = new DbConnectionStringBuilder { ConnectionString = _connectionString };
            foreach (string key in new[] { "Data Source", "Server", "DataSource" })
                if (b.TryGetValue(key, out var v)) _dataSource = Convert.ToString(v) ?? _dataSource;
            foreach (string key in new[] { "Initial Catalog", "Database" })
                if (b.TryGetValue(key, out var v)) _database = Convert.ToString(v) ?? _database;
        }
    }

    public override string Database => _session?.Database ?? _database;
    public override string DataSource => _dataSource;
    public override string ServerVersion => "1.0.0";
    public override ConnectionState State => _session != null ? ConnectionState.Open : ConnectionState.Closed;

    /// <summary>The engine this connection talks to (available after Open, or when constructed with an engine).</summary>
    public LocalDbEngine? Engine => _engine;

    /// <summary>Raised for PRINT output and informational RAISERROR messages.</summary>
    public event Action<string>? InfoMessage;

    internal Session Session => _session ?? throw new InvalidOperationException("The connection is not open.");

    public override void Open()
    {
        if (_session != null) throw new InvalidOperationException("The connection is already open.");
        _engine ??= LocalDbEngine.GetNamed(_dataSource);
        _session = _engine.CreateSession(_database);
        _session.InfoMessage += m => InfoMessage?.Invoke(m);
        OnStateChange(new StateChangeEventArgs(ConnectionState.Closed, ConnectionState.Open));
    }

    public override void Close()
    {
        if (_session == null) return;
        _session.Dispose();
        _session = null;
        CurrentTransaction = null;
        OnStateChange(new StateChangeEventArgs(ConnectionState.Open, ConnectionState.Closed));
    }

    public override void ChangeDatabase(string databaseName) => Session.Execute("USE [" + databaseName.Replace("]", "]]") + "]");

    protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel)
    {
        if (CurrentTransaction != null) throw new InvalidOperationException("LocalDbSharpConnection does not support parallel transactions.");
        Session.BeginTransaction();
        return CurrentTransaction = new LocalDbSharpTransaction(this, isolationLevel);
    }

    public new LocalDbSharpTransaction BeginTransaction() => (LocalDbSharpTransaction)base.BeginTransaction();

    protected override DbCommand CreateDbCommand() => new LocalDbSharpCommand { Connection = this };

    public new LocalDbSharpCommand CreateCommand() => (LocalDbSharpCommand)base.CreateCommand();

    protected override void Dispose(bool disposing)
    {
        if (disposing) Close();
        base.Dispose(disposing);
    }
}

public sealed class LocalDbSharpTransaction : DbTransaction
{
    LocalDbSharpConnection? _connection;

    internal LocalDbSharpTransaction(LocalDbSharpConnection connection, IsolationLevel level)
    {
        _connection = connection;
        IsolationLevel = level == IsolationLevel.Unspecified ? IsolationLevel.ReadCommitted : level;
    }

    public override IsolationLevel IsolationLevel { get; }
    protected override DbConnection? DbConnection => _connection;

    LocalDbSharpConnection Active()
        => _connection ?? throw new InvalidOperationException("This transaction has completed; it is no longer usable.");

    public override void Commit()
    {
        var c = Active();
        if (c.Session.InTransaction) c.Session.Commit();
        Finish();
    }

    public override void Rollback()
    {
        var c = Active();
        if (c.Session.InTransaction) c.Session.Rollback();
        Finish();
    }

    void Finish()
    {
        if (_connection != null) _connection.CurrentTransaction = null;
        _connection = null;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && _connection != null && _connection.State == ConnectionState.Open) Rollback();
        base.Dispose(disposing);
    }
}

public sealed class LocalDbSharpFactory : DbProviderFactory
{
    public static readonly LocalDbSharpFactory Instance = new();
    public override DbConnection CreateConnection() => new LocalDbSharpConnection();
    public override DbCommand CreateCommand() => new LocalDbSharpCommand();
    public override DbParameter CreateParameter() => new LocalDbSharpParameter();
    public override DbConnectionStringBuilder CreateConnectionStringBuilder() => new();
}
