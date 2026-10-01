namespace LocalDbSharp.Tests;

public class EngineTests
{
    [Fact]
    public void Execute_returns_result_sets_with_typed_columns()
    {
        var engine = new LocalDbEngine();
        using var session = engine.CreateSession();
        var result = session.Execute("SELECT 1 AS a, CAST(2.50 AS DECIMAL(5,2)) AS b, N'x' AS c, CAST('2024-01-02' AS DATE) AS d; SELECT NULL AS n");
        Assert.Equal(2, result.ResultSets.Count);
        var first = result.ResultSets[0];
        Assert.Equal(["a", "b", "c", "d"], first.Columns.Select(c => c.Name));
        Assert.Equal([SqlTypeKind.Int, SqlTypeKind.Decimal, SqlTypeKind.NVarChar, SqlTypeKind.Date], first.Columns.Select(c => c.Type.Kind));
        Assert.Equal([1, 2.50m, "x", new DateTime(2024, 1, 2)], first.Rows[0]);
        Assert.Null(result.ResultSets[1].Rows[0][0]);
        Assert.Equal(-1, result.RecordsAffected);
    }

    [Fact]
    public void Parameters_can_be_passed_as_tuples()
    {
        using var session = new LocalDbEngine().CreateSession();
        var result = session.Execute("SELECT @a + @b AS s, LEN(@t) AS l", ("@a", 1), ("@b", 2L), ("@t", "four"));
        Assert.Equal([3L, 4], result.ResultSets[0].Rows[0]);
    }

    [Fact]
    public void Go_separates_batches_and_variables_do_not_cross_them()
    {
        using var session = new LocalDbEngine().CreateSession();
        var result = session.Execute("""
            CREATE TABLE T (x INT);
            GO
            CREATE PROCEDURE P AS INSERT INTO T VALUES (1);
            GO
            EXEC P;
            EXEC P;
            GO
            DECLARE @v INT = 5;
            SELECT COUNT(*) AS n FROM T;
            """);
        Assert.Equal(2, result.ResultSets[0].Rows[0][0]);
        var ex = Assert.Throws<TSqlException>(() => session.Execute("DECLARE @v INT = 1;\nGO\nSELECT @v"));
        Assert.Equal(137, ex.Number);
    }

    [Fact]
    public void A_syntax_error_in_a_later_batch_does_not_stop_earlier_batches()
    {
        using var session = new LocalDbEngine().CreateSession();
        var ex = Assert.Throws<TSqlException>(() => session.Execute("CREATE TABLE T (x INT)\nGO\nSELECT FROM WHERE\nGO"));
        Assert.Equal(15, ex.Class);
        Assert.Equal(3, ex.LineNumber);
        Assert.NotNull(session.Execute("SELECT OBJECT_ID('T')").ResultSets[0].Rows[0][0]);
    }

    [Fact]
    public void Sessions_share_tables_but_not_temp_tables_or_variables()
    {
        var engine = new LocalDbEngine();
        using var a = engine.CreateSession();
        using var b = engine.CreateSession();
        a.Execute("CREATE TABLE Shared (x INT); INSERT INTO Shared VALUES (1); CREATE TABLE #mine (x INT)");
        Assert.Equal(1, b.Execute("SELECT COUNT(*) FROM Shared").ResultSets[0].Rows[0][0]);
        Assert.Equal(208, Assert.Throws<TSqlException>(() => b.Execute("SELECT * FROM #mine")).Number);
        b.Execute("CREATE TABLE #mine (y VARCHAR(5))"); // same name, different session: no clash
    }

    [Fact]
    public void Separate_engines_are_isolated()
    {
        var one = new LocalDbEngine();
        var two = new LocalDbEngine();
        one.Execute("CREATE TABLE OnlyHere (x INT)");
        Assert.Equal(208, Assert.Throws<TSqlException>(() => two.Execute("SELECT * FROM OnlyHere")).Number);
    }

    [Fact]
    public void Messages_are_collected_on_the_result()
    {
        using var session = new LocalDbEngine().CreateSession();
        var result = session.Execute("PRINT 'a'; PRINT CAST(1.5 AS DECIMAL(3,1)); PRINT NULL; PRINT CAST('2024-01-02' AS DATE)");
        Assert.Equal(["a", "1.5", "", "2024-01-02"], result.Messages);
    }

    [Fact]
    public void Open_transaction_is_rolled_back_when_the_session_is_disposed()
    {
        var engine = new LocalDbEngine();
        engine.Execute("CREATE TABLE T (x INT)");
        using (var session = engine.CreateSession())
        {
            session.Execute("BEGIN TRAN; INSERT INTO T VALUES (1)");
            Assert.True(session.InTransaction);
        }
        Assert.Equal(0, engine.Execute("SELECT COUNT(*) FROM T").ResultSets[0].Rows[0][0]);
    }

    [Fact]
    public void Concurrent_sessions_can_hammer_one_engine()
    {
        var engine = new LocalDbEngine();
        engine.Execute("CREATE TABLE Hits (Id INT IDENTITY PRIMARY KEY, Worker INT NOT NULL)");
        Parallel.For(0, 8, worker =>
        {
            using var session = engine.CreateSession();
            for (int i = 0; i < 200; i++) session.Execute("INSERT INTO Hits (Worker) VALUES (@w)", ("@w", worker));
        });
        var result = engine.Execute("SELECT COUNT(*), COUNT(DISTINCT Id), MAX(Id) FROM Hits");
        Assert.Equal([1600, 1600, 1600], result.ResultSets[0].Rows[0]);
    }

    [Theory]
    [InlineData("SELECT * FORM t", 102)]
    [InlineData("SELECT 'unterminated", 105)]
    [InlineData("INSERT INTO t VALUES (1,", 102)]
    [InlineData("CREATE TABLE t (a NOSUCHTYPE)", 2715)]
    [InlineData("SELECT 1 +", 102)]
    public void Malformed_sql_is_rejected(string sql, int expectedNumber)
    {
        using var session = new LocalDbEngine().CreateSession();
        Assert.Equal(expectedNumber, Assert.Throws<TSqlException>(() => session.Execute(sql)).Number);
    }

    [Fact]
    public void Unsupported_features_fail_loudly()
    {
        using var session = new LocalDbEngine().CreateSession();
        session.Execute("CREATE TABLE T (x INT)");
        foreach (string sql in new[] { "SELECT x FROM T FOR XML AUTO", "SELECT * FROM OPENROWSET('x', 'y', 'z')", "SELECT GETDATE() AT TIME ZONE 'UTC'", "GOTO done" })
            Assert.Contains("not supported", Assert.Throws<TSqlException>(() => session.Execute(sql)).Message);
    }
}
