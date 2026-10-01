using System.Data;
using System.Data.Common;
using LocalDbSharp.Data;

namespace LocalDbSharp.Tests;

public class AdoProviderTests
{
    static LocalDbSharpConnection Open(LocalDbEngine? engine = null)
    {
        var conn = new LocalDbSharpConnection(engine ?? new LocalDbEngine());
        conn.Open();
        return conn;
    }

    static int Exec(DbConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteNonQuery();
    }

    static object? Scalar(DbConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar();
    }

    [Fact]
    public void Connection_reports_state_and_database()
    {
        using var conn = new LocalDbSharpConnection("Data Source=state-test;Initial Catalog=master");
        Assert.Equal(ConnectionState.Closed, conn.State);
        conn.Open();
        Assert.Equal(ConnectionState.Open, conn.State);
        Assert.Equal("master", conn.Database);
        Assert.Equal("state-test", conn.DataSource);
        conn.Close();
        Assert.Equal(ConnectionState.Closed, conn.State);
        LocalDbEngine.DropNamed("state-test");
    }

    [Fact]
    public void Connections_with_same_data_source_share_data()
    {
        string name = "shared-" + Guid.NewGuid().ToString("N");
        try
        {
            using var a = new LocalDbSharpConnection($"Data Source={name}");
            using var b = new LocalDbSharpConnection($"Data Source={name}");
            using var other = new LocalDbSharpConnection($"Data Source={name}-other");
            a.Open();
            b.Open();
            other.Open();
            Exec(a, "CREATE TABLE T (x INT); INSERT INTO T VALUES (1), (2)");
            Assert.Equal(2, Scalar(b, "SELECT COUNT(*) FROM T"));
            Assert.Equal(DBNull.Value, Scalar(other, "SELECT OBJECT_ID('T')"));
        }
        finally
        {
            LocalDbEngine.DropNamed(name);
            LocalDbEngine.DropNamed(name + "-other");
        }
    }

    [Fact]
    public void ExecuteNonQuery_returns_rows_affected()
    {
        using var conn = Open();
        Assert.Equal(-1, Exec(conn, "CREATE TABLE T (x INT)"));
        Assert.Equal(3, Exec(conn, "INSERT INTO T VALUES (1), (2), (3)"));
        Assert.Equal(2, Exec(conn, "UPDATE T SET x = x + 1 WHERE x >= 2"));
        Assert.Equal(4, Exec(conn, "DELETE FROM T WHERE x = 1; INSERT INTO T VALUES (7), (8), (9)"));
        Assert.Equal(-1, Exec(conn, "SET NOCOUNT ON; DELETE FROM T"));
        Assert.Equal(-1, Exec(conn, "SELECT 1"));
    }

    [Fact]
    public void ExecuteScalar_returns_first_cell_dbnull_or_null()
    {
        using var conn = Open();
        Assert.Equal(42, Scalar(conn, "SELECT 42, 'ignored'"));
        Assert.Equal(DBNull.Value, Scalar(conn, "SELECT CAST(NULL AS INT)"));
        Assert.Null(Scalar(conn, "SELECT 1 WHERE 1 = 0"));
        Assert.Null(Scalar(conn, "DECLARE @x INT"));
    }

    [Fact]
    public void Parameters_are_bound_by_name_with_or_without_at_sign()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT @a + @b AS s, @name AS n, @when AS w, @missing AS m, @flag AS f, @id AS g, @bytes AS b";
        cmd.Parameters.AddWithValue("@a", 2);
        cmd.Parameters.AddWithValue("b", 3.5m);
        cmd.Parameters.AddWithValue("@name", "héllo");
        cmd.Parameters.AddWithValue("@when", new DateTime(2024, 3, 5, 10, 11, 12));
        cmd.Parameters.AddWithValue("@missing", DBNull.Value);
        cmd.Parameters.AddWithValue("@flag", true);
        var guid = Guid.NewGuid();
        cmd.Parameters.AddWithValue("@id", guid);
        cmd.Parameters.AddWithValue("@bytes", new byte[] { 1, 2, 3 });
        using var r = cmd.ExecuteReader();
        Assert.True(r.Read());
        Assert.Equal(5.5m, r.GetDecimal(0));
        Assert.Equal("héllo", r.GetString(1));
        Assert.Equal(new DateTime(2024, 3, 5, 10, 11, 12), r.GetDateTime(2));
        Assert.True(r.IsDBNull(3));
        Assert.True(r.GetBoolean(4));
        Assert.Equal(guid, r.GetGuid(5));
        Assert.Equal(new byte[] { 1, 2, 3 }, (byte[])r[6]);
        Assert.False(r.Read());
    }

    [Fact]
    public void Parameters_prevent_injection_and_keep_long_strings()
    {
        using var conn = Open();
        Exec(conn, "CREATE TABLE Notes (Id INT IDENTITY PRIMARY KEY, Body NVARCHAR(MAX))");
        string body = "Robert'); DROP TABLE Notes;--" + new string('x', 10_000);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO Notes (Body) VALUES (@body); SELECT SCOPE_IDENTITY()";
        cmd.Parameters.AddWithValue("@body", body);
        Assert.Equal(1m, cmd.ExecuteScalar());
        Assert.Equal(body, Scalar(conn, "SELECT Body FROM Notes WHERE Id = 1"));
    }

    [Fact]
    public void Typed_parameters_convert_values()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT @d AS d, @s AS s, @i AS i";
        cmd.Parameters.Add(new LocalDbSharpParameter("@d", "2024-02-29") { DbType = DbType.Date });
        cmd.Parameters.Add(new LocalDbSharpParameter("@s", "abcdef") { DbType = DbType.AnsiString, Size = 3 });
        cmd.Parameters.Add(new LocalDbSharpParameter("@i", "17") { DbType = DbType.Int32 });
        using var r = cmd.ExecuteReader();
        Assert.True(r.Read());
        Assert.Equal(new DateTime(2024, 2, 29), r.GetDateTime(0));
        Assert.Equal("date", r.GetDataTypeName(0));
        Assert.Equal("abc", r.GetString(1));
        Assert.Equal(17, r.GetInt32(2));
        Assert.Equal(typeof(int), r.GetFieldType(2));
    }

    [Fact]
    public void Output_parameters_are_written_back()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SET @total = @a * 2; SET @text = 'done'";
        cmd.Parameters.AddWithValue("@a", 21);
        var total = new LocalDbSharpParameter("@total", null) { DbType = DbType.Int32, Direction = ParameterDirection.Output };
        var text = new LocalDbSharpParameter("@text", null) { DbType = DbType.String, Size = 20, Direction = ParameterDirection.Output };
        cmd.Parameters.Add(total);
        cmd.Parameters.Add(text);
        cmd.ExecuteNonQuery();
        Assert.Equal(42, total.Value);
        Assert.Equal("done", text.Value);
    }

    [Fact]
    public void Stored_procedure_command_type_supports_output_and_return_value()
    {
        using var conn = Open();
        Exec(conn, """
            CREATE PROCEDURE dbo.AddNumbers @a INT, @b INT = 10, @sum INT OUTPUT AS
            BEGIN
                SET @sum = @a + @b;
                SELECT @a AS a, @b AS b;
                RETURN 7;
            END
            """);
        using var cmd = conn.CreateCommand();
        cmd.CommandType = CommandType.StoredProcedure;
        cmd.CommandText = "dbo.AddNumbers";
        cmd.Parameters.AddWithValue("@a", 5);
        var sum = new LocalDbSharpParameter("@sum", null) { DbType = DbType.Int32, Direction = ParameterDirection.Output };
        var ret = new LocalDbSharpParameter("@RETURN_VALUE", null) { Direction = ParameterDirection.ReturnValue };
        cmd.Parameters.Add(sum);
        cmd.Parameters.Add(ret);
        using (var r = cmd.ExecuteReader())
        {
            Assert.True(r.Read());
            Assert.Equal(5, r["a"]);
            Assert.Equal(10, r["b"]);
        }
        Assert.Equal(15, sum.Value);
        Assert.Equal(7, ret.Value);
    }

    [Fact]
    public void Data_table_parameters_act_as_table_valued_parameters()
    {
        using var conn = Open();
        Exec(conn, "CREATE TYPE dbo.IdList AS TABLE (Id INT PRIMARY KEY, Label NVARCHAR(10))");
        Exec(conn, "CREATE PROCEDURE dbo.Labels @ids dbo.IdList READONLY AS SELECT STRING_AGG(Label, ',') WITHIN GROUP (ORDER BY Id) FROM @ids");
        var ids = new DataTable();
        ids.Columns.Add("Id", typeof(int));
        ids.Columns.Add("Label", typeof(string));
        ids.Rows.Add(2, "two");
        ids.Rows.Add(1, "one");

        using var typed = conn.CreateCommand();
        typed.CommandType = CommandType.StoredProcedure;
        typed.CommandText = "dbo.Labels";
        typed.Parameters.Add(new LocalDbSharpParameter("@ids", ids) { TypeName = "dbo.IdList" });
        Assert.Equal("one,two", typed.ExecuteScalar());

        using var untyped = conn.CreateCommand();
        untyped.CommandText = "SELECT SUM(t.Id) FROM @rows t WHERE t.Label LIKE 't%'";
        untyped.Parameters.AddWithValue("@rows", ids);
        Assert.Equal(2, untyped.ExecuteScalar());

        ids.Rows.Add(1, "dup");
        var ex = Assert.Throws<TSqlException>(() => typed.ExecuteScalar());
        Assert.Equal(2627, ex.Number);
    }

    [Fact]
    public void Reader_walks_multiple_result_sets()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT 1 AS a, 'x' AS b UNION ALL SELECT 2, 'y'; SELECT CAST(NULL AS DATE) AS d WHERE 1 = 0; SELECT 3.5 AS c";
        using var r = cmd.ExecuteReader();
        Assert.True(r.HasRows);
        Assert.Equal(2, r.FieldCount);
        Assert.Equal("a", r.GetName(0));
        Assert.Equal(1, r.GetOrdinal("B"));
        Assert.Throws<IndexOutOfRangeException>(() => r.GetOrdinal("nope"));
        Assert.Throws<InvalidOperationException>(() => r.GetValue(0));
        int rows = 0;
        while (r.Read()) rows++;
        Assert.Equal(2, rows);

        Assert.True(r.NextResult());
        Assert.False(r.HasRows);
        Assert.Equal(typeof(DateTime), r.GetFieldType(0));
        Assert.False(r.Read());

        Assert.True(r.NextResult());
        Assert.True(r.Read());
        Assert.Equal(3.5m, r.GetFieldValue<decimal>(0));
        Assert.Equal(3.5, r.GetDouble(0));
        Assert.False(r.NextResult());
    }

    [Fact]
    public void Reader_loads_into_data_table()
    {
        using var conn = Open();
        Exec(conn, "CREATE TABLE P (Id INT PRIMARY KEY, Name NVARCHAR(20), Price DECIMAL(8,2) NULL); INSERT INTO P VALUES (1, N'a', 1.5), (2, N'b', NULL)");
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Id, Name, Price FROM P ORDER BY Id";
        using var r = cmd.ExecuteReader();
        var schema = r.GetSchemaTable()!;
        Assert.Equal(3, schema.Rows.Count);
        Assert.Equal("Price", schema.Rows[2]["ColumnName"]);
        var table = new DataTable();
        table.Load(r);
        Assert.Equal(2, table.Rows.Count);
        Assert.Equal(typeof(decimal), table.Columns["Price"]!.DataType);
        Assert.Equal(DBNull.Value, table.Rows[1]["Price"]);
        Assert.Equal("a", table.Rows[0]["Name"]);
    }

    [Fact]
    public void Transaction_commit_and_rollback()
    {
        using var conn = Open();
        Exec(conn, "CREATE TABLE T (x INT PRIMARY KEY)");
        using (var tx = conn.BeginTransaction())
        {
            Exec(conn, "INSERT INTO T VALUES (1)");
            tx.Commit();
        }
        using (var tx = conn.BeginTransaction())
        {
            Exec(conn, "INSERT INTO T VALUES (2); DELETE FROM T WHERE x = 1");
            Assert.Equal(1, Scalar(conn, "SELECT @@TRANCOUNT"));
            tx.Rollback();
        }
        using (conn.BeginTransaction())
        {
            Exec(conn, "INSERT INTO T VALUES (3)");
            // disposed without commit: rolled back
        }
        Assert.Equal(1, Scalar(conn, "SELECT COUNT(*) FROM T"));
        Assert.Equal(1, Scalar(conn, "SELECT x FROM T"));
        Assert.Equal(0, Scalar(conn, "SELECT @@TRANCOUNT"));
    }

    [Fact]
    public void Closing_a_connection_rolls_back_and_drops_temp_tables()
    {
        var engine = new LocalDbEngine();
        using (var conn = Open(engine))
        {
            Exec(conn, "CREATE TABLE T (x INT); CREATE TABLE #tmp (x INT); BEGIN TRAN; INSERT INTO T VALUES (1)");
        }
        using var again = Open(engine);
        Assert.Equal(0, Scalar(again, "SELECT COUNT(*) FROM T"));
        Assert.Equal(DBNull.Value, Scalar(again, "SELECT OBJECT_ID('tempdb..#tmp')"));
    }

    [Fact]
    public void Errors_surface_as_TSqlException_with_sql_server_numbers()
    {
        using var conn = Open();
        Exec(conn, "CREATE TABLE T (x INT PRIMARY KEY, y INT NOT NULL)");
        Exec(conn, "INSERT INTO T VALUES (1, 1)");
        Assert.Equal(2627, Assert.Throws<TSqlException>(() => Exec(conn, "INSERT INTO T VALUES (1, 2)")).Number);
        Assert.Equal(515, Assert.Throws<TSqlException>(() => Exec(conn, "INSERT INTO T VALUES (2, NULL)")).Number);
        Assert.Equal(208, Assert.Throws<TSqlException>(() => Exec(conn, "SELECT * FROM Missing")).Number);
        Assert.Equal(8134, Assert.Throws<TSqlException>(() => Scalar(conn, "SELECT 1 / 0")).Number);
        var syntax = Assert.Throws<TSqlException>(() => Exec(conn, "SELECT 1;\nSELEC 2"));
        Assert.Equal(15, syntax.Class);
        Assert.Equal(2, syntax.LineNumber);
        var thrown = Assert.Throws<TSqlException>(() => Exec(conn, "THROW 50001, 'custom', 3"));
        Assert.Equal((50001, "custom", (byte)3, (byte)16), (thrown.Number, thrown.Message, thrown.State, thrown.Class));
        Assert.IsAssignableFrom<DbException>(thrown);
    }

    [Fact]
    public void Statement_errors_do_not_abort_the_batch_but_are_reported()
    {
        using var conn = Open();
        Exec(conn, "CREATE TABLE T (x INT PRIMARY KEY)");
        var ex = Assert.Throws<TSqlException>(() => Exec(conn, "INSERT INTO T VALUES (1); INSERT INTO T VALUES (1); INSERT INTO T VALUES (2)"));
        Assert.Equal(2627, ex.Number);
        Assert.Equal(2, Scalar(conn, "SELECT COUNT(*) FROM T"));
    }

    [Fact]
    public void Print_raises_info_messages()
    {
        using var conn = Open();
        var messages = new List<string>();
        conn.InfoMessage += messages.Add;
        Exec(conn, "PRINT 'hello'; PRINT 40 + 2; RAISERROR('note %d', 5, 1, 9)");
        Assert.Equal(["hello", "42", "note 9"], messages);
    }

    [Fact]
    public void Change_database_and_three_part_names()
    {
        using var conn = Open();
        Exec(conn, "CREATE DATABASE Shop");
        conn.ChangeDatabase("Shop");
        Assert.Equal("Shop", conn.Database);
        Exec(conn, "CREATE TABLE Items (Id INT)");
        Exec(conn, "USE master");
        Assert.Equal(0, Scalar(conn, "SELECT COUNT(*) FROM Shop.dbo.Items"));
        Assert.Equal(DBNull.Value, Scalar(conn, "SELECT OBJECT_ID('Items')"));
        Assert.Equal(911, Assert.Throws<TSqlException>(() => conn.ChangeDatabase("Nowhere")).Number);
    }

    [Fact]
    public void Provider_factory_creates_usable_objects()
    {
        DbProviderFactory factory = LocalDbSharpFactory.Instance;
        using var conn = factory.CreateConnection()!;
        conn.ConnectionString = "Data Source=factory-" + Guid.NewGuid().ToString("N");
        conn.Open();
        using var cmd = factory.CreateCommand()!;
        cmd.Connection = conn;
        cmd.CommandText = "SELECT @p * 2";
        var p = factory.CreateParameter()!;
        p.ParameterName = "@p";
        p.Value = 21;
        cmd.Parameters.Add(p);
        Assert.Equal(42, cmd.ExecuteScalar());
        LocalDbEngine.DropNamed(conn.DataSource);
    }

    [Fact]
    public async Task Async_api_works_through_the_base_classes()
    {
        await using var conn = Open();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT value FROM STRING_SPLIT('a,b,c', ',') ORDER BY value";
        var values = new List<string>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync()) values.Add(r.GetString(0));
        Assert.Equal(["a", "b", "c"], values);
    }
}
