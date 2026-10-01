using System.Globalization;

namespace LocalDbSharp.Parsing;

/// <summary>Recursive-descent parser for the supported T-SQL dialect.</summary>
internal sealed class Parser
{
    readonly List<Token> _t;
    int _p;

    Parser(string sql) => _t = Lexer.Lex(sql);

    static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "SELECT", "FROM", "WHERE", "GROUP", "HAVING", "ORDER", "BY", "UNION", "EXCEPT", "INTERSECT", "JOIN", "INNER",
        "LEFT", "RIGHT", "FULL", "OUTER", "CROSS", "ON", "AS", "AND", "OR", "NOT", "NULL", "IS", "IN", "LIKE", "BETWEEN",
        "EXISTS", "CASE", "WHEN", "THEN", "ELSE", "END", "INSERT", "INTO", "VALUES", "UPDATE", "SET", "DELETE", "CREATE",
        "ALTER", "DROP", "TABLE", "VIEW", "INDEX", "PROCEDURE", "PROC", "FUNCTION", "TRIGGER", "DECLARE", "IF", "WHILE",
        "BEGIN", "BREAK", "CONTINUE", "RETURN", "PRINT", "EXEC", "EXECUTE", "GO", "USE", "WITH", "TOP", "DISTINCT", "ALL",
        "ANY", "SOME", "PRIMARY", "FOREIGN", "KEY", "REFERENCES", "CONSTRAINT", "DEFAULT", "CHECK", "UNIQUE", "IDENTITY",
        "COMMIT", "ROLLBACK", "TRANSACTION", "TRAN", "TRUNCATE", "MERGE", "OPTION", "FOR", "OFFSET", "FETCH", "OVER",
        "PIVOT", "UNPIVOT", "COLLATE", "DESC", "ASC", "THROW", "RAISERROR", "GOTO", "WAITFOR", "SAVE", "USING", "OUTPUT",
        "OPEN", "CLOSE", "DEALLOCATE", "ESCAPE", "TO", "CURSOR", "WITHIN", "DBCC", "CHECKPOINT",
    };

    /// <summary>
    /// Words this parser treats as clause keywords but that T-SQL does not reserve: they are still accepted
    /// as column names in expressions.
    /// </summary>
    static readonly HashSet<string> NotReallyReserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "OFFSET", "OUTPUT", "USING", "THROW", "GO", "WITHIN", "CURSOR",
    };

    static readonly HashSet<string> TableHints = new(StringComparer.OrdinalIgnoreCase)
    {
        "NOLOCK", "READUNCOMMITTED", "READCOMMITTED", "REPEATABLEREAD", "SERIALIZABLE", "HOLDLOCK", "UPDLOCK", "ROWLOCK", "PAGLOCK",
        "TABLOCK", "TABLOCKX", "XLOCK", "READPAST", "NOWAIT", "SNAPSHOT", "FORCESEEK", "FORCESCAN",
    };

    static readonly HashSet<string> StatementEnders = new(StringComparer.OrdinalIgnoreCase)
    {
        "SELECT", "INSERT", "UPDATE", "DELETE", "MERGE", "DECLARE", "SET", "IF", "WHILE", "BEGIN", "END", "ELSE", "PRINT",
        "EXEC", "EXECUTE", "RETURN", "BREAK", "CONTINUE", "CREATE", "ALTER", "DROP", "TRUNCATE", "WITH", "COMMIT",
        "ROLLBACK", "SAVE", "THROW", "RAISERROR", "GO", "USE", "OPEN", "CLOSE", "FETCH", "DEALLOCATE", "GOTO", "WAITFOR",
        "DBCC", "CHECKPOINT",
    };

    static readonly HashSet<string> DatePartFunctions = new(StringComparer.OrdinalIgnoreCase)
    {
        "DATEADD", "DATEDIFF", "DATEDIFF_BIG", "DATEPART", "DATENAME", "DATETRUNC",
    };

    // ------------------------------------------------------------ entry points

    /// <summary>
    /// Parses a script into batches (separated by GO). A syntax error only poisons its own batch,
    /// so that earlier batches of a script still run - as with sqlcmd.
    /// </summary>
    public static List<Batch> ParseScript(string sql)
    {
        var p = new Parser(sql);
        var batches = new List<Batch>();
        var cur = new Batch();
        while (true)
        {
            while (p.TrySym(";")) { }
            if (p.Cur.Kind == Tk.Eof) break;
            if (p.IsKw("GO"))
            {
                p._p++;
                if (p.Cur.Kind == Tk.Int) p._p++;
                if (cur.Statements.Count > 0 || cur.Error != null) { batches.Add(cur); cur = new Batch(); }
                continue;
            }
            try
            {
                cur.Statements.Add(p.ParseStatement());
            }
            catch (TSqlException ex)
            {
                cur.Error = ex;
                while (p.Cur.Kind != Tk.Eof && !p.IsKw("GO")) p._p++;
            }
        }
        if (cur.Statements.Count > 0 || cur.Error != null) batches.Add(cur);
        return batches;
    }

    /// <summary>Parses a parameter declaration list such as "@a int, @b varchar(10) OUTPUT" (sp_executesql).</summary>
    public static List<ParamDef> ParseParamList(string text)
    {
        var p = new Parser(text);
        var list = new List<ParamDef>();
        while (p.Cur.Kind == Tk.Var)
        {
            list.Add(p.ParseParam());
            if (!p.TrySym(",")) break;
        }
        if (p.Cur.Kind != Tk.Eof) throw p.Error();
        return list;
    }

    // ------------------------------------------------------------ token helpers

    Token Cur => _t[_p];
    Token Peek(int n = 1) => _t[Math.Min(_p + n, _t.Count - 1)];
    bool IsKwAt(int n, string kw) => Peek(n) is { Kind: Tk.Ident } t && t.Text.Equals(kw, StringComparison.OrdinalIgnoreCase);
    bool IsKw(string kw) => IsKwAt(0, kw);
    bool TryKw(string kw) { if (!IsKw(kw)) return false; _p++; return true; }
    void ExpectKw(string kw) { if (!TryKw(kw)) throw Error(); }
    bool IsSymAt(int n, string s) => Peek(n) is { Kind: Tk.Sym } t && t.Text == s;
    bool IsSym(string s) => IsSymAt(0, s);
    bool TrySym(string s) { if (!IsSym(s)) return false; _p++; return true; }
    void ExpectSym(string s) { if (!TrySym(s)) throw Error(); }
    bool IsIdent => Cur.Kind is Tk.Ident or Tk.Quoted;
    bool IsAliasCandidate => Cur.Kind == Tk.Quoted || (Cur.Kind == Tk.Ident && !Reserved.Contains(Cur.Text));

    TSqlException Error()
        => Cur.Kind == Tk.Eof
            ? new TSqlException(102, "Incorrect syntax near the end of the statement.", 15) { LineNumber = Cur.Line }
            : Err.Syntax(Cur.Text, Cur.Line);

    string Ident()
    {
        if (!IsIdent) throw Error();
        return _t[_p++].Text;
    }

    string? OptionalAlias()
    {
        if (TryKw("AS")) return Cur.Kind is Tk.Str or Tk.NStr ? _t[_p++].Text : Ident();
        return IsAliasCandidate ? Ident() : null;
    }

    bool AtStatementEnd()
        => Cur.Kind == Tk.Eof || IsSym(";") || IsSym(")") || (Cur.Kind == Tk.Ident && StatementEnders.Contains(Cur.Text));

    void SkipParens()
    {
        ExpectSym("(");
        int depth = 1;
        while (depth > 0)
        {
            if (Cur.Kind == Tk.Eof) throw Error();
            if (IsSym("(")) depth++;
            else if (IsSym(")")) depth--;
            _p++;
        }
    }

    /// <summary>Skips the rest of a statement that is accepted but has no effect in an in-memory engine.</summary>
    void SkipStatement()
    {
        int depth = 0;
        while (Cur.Kind != Tk.Eof && !(depth == 0 && (IsSym(";") || IsKw("GO"))))
        {
            if (IsSym("(")) depth++;
            else if (IsSym(")")) depth--;
            _p++;
        }
    }

    long SignedLong()
    {
        bool neg = TrySym("-");
        if (!neg) TrySym("+");
        if (Cur.Kind != Tk.Int) throw Error();
        long v = long.Parse(_t[_p++].Text, CultureInfo.InvariantCulture);
        return neg ? -v : v;
    }

    List<string> IdentList()
    {
        var list = new List<string>();
        ExpectSym("(");
        do { list.Add(Ident()); } while (TrySym(","));
        ExpectSym(")");
        return list;
    }

    // ------------------------------------------------------------ statements

    Statement ParseStatement()
    {
        int line = Cur.Line;
        var s = ParseStatementCore();
        s.Line = line;
        TrySym(";");
        return s;
    }

    List<Statement> ParseUntilEnd()
    {
        var list = new List<Statement>();
        while (true)
        {
            while (TrySym(";")) { }
            if (IsKw("END")) break;
            if (Cur.Kind == Tk.Eof) throw Error();
            list.Add(ParseStatement());
        }
        return list;
    }

    Statement ParseStatementCore()
    {
        if (IsSym("(")) return new SelectStmt { Q = ParseQuery() };
        if (Cur.Kind != Tk.Ident) throw Error();
        switch (Cur.Text.ToUpperInvariant())
        {
            case "SELECT": return new SelectStmt { Q = ParseQuery() };
            case "WITH":
            {
                _p++;
                var ctes = ParseCtes();
                if (IsKw("INSERT")) { var s = ParseInsert(); s.Ctes = ctes; return s; }
                if (IsKw("UPDATE")) { var s = ParseUpdate(); s.Ctes = ctes; return s; }
                if (IsKw("DELETE")) { var s = ParseDelete(); s.Ctes = ctes; return s; }
                if (IsKw("MERGE")) { var s = ParseMerge(); s.Ctes = ctes; return s; }
                return new SelectStmt { Q = ParseQuery(ctes) };
            }
            case "INSERT": return ParseInsert();
            case "UPDATE":
                if (IsKwAt(1, "STATISTICS")) { SkipStatement(); return new BlockStmt(); }
                return ParseUpdate();
            case "CHECKPOINT": _p++; return new BlockStmt();
            case "DBCC":
            {
                _p++;
                var s = new DbccStmt { Command = Ident().ToUpperInvariant() };
                if (TrySym("("))
                {
                    do { s.Args.Add(IsIdent && !IsSymAt(1, "(") ? new Literal(Ident(), SqlType.VarChar(128)) : ParseExpr()); } while (TrySym(","));
                    ExpectSym(")");
                }
                if (IsKw("WITH") && Peek().Kind == Tk.Ident) { _p += 2; while (TrySym(",")) Ident(); }
                return s;
            }
            case "ENABLE":
            case "DISABLE":
            {
                bool enable = IsKw("ENABLE");
                _p++;
                ExpectKw("TRIGGER");
                string? name = TryKw("ALL") ? null : Ident();
                while (TrySym(".")) name = Ident();
                ExpectKw("ON");
                return new EnableTriggerStmt { Name = name, Table = ParseTableName(), Enable = enable };
            }
            case "DELETE": return ParseDelete();
            case "MERGE": return ParseMerge();
            case "TRUNCATE":
                _p++;
                ExpectKw("TABLE");
                return new TruncateStmt { Name = ParseTableName() };
            case "CREATE": return ParseCreate();
            case "ALTER": return ParseAlter();
            case "DROP": return ParseDrop();
            case "DECLARE": return ParseDeclare();
            case "SET": return ParseSet();
            case "IF":
            {
                _p++;
                var cond = ParseExpr();
                var then = ParseStatement();
                Statement? els = TryKw("ELSE") ? ParseStatement() : null;
                return new IfStmt { Cond = cond, Then = then, Else = els };
            }
            case "WHILE":
            {
                _p++;
                var cond = ParseExpr();
                return new WhileStmt { Cond = cond, Body = ParseStatement() };
            }
            case "BEGIN": return ParseBegin();
            case "COMMIT":
            case "ROLLBACK":
            {
                bool commit = IsKw("COMMIT");
                _p++;
                if (!TryKw("TRAN") && !TryKw("TRANSACTION")) TryKw("WORK");
                string? name = null;
                if (IsAliasCandidate || Cur.Kind == Tk.Var) name = _t[_p++].Text;
                return new TranStmt { Kind = commit ? TranKind.Commit : TranKind.Rollback, Name = name };
            }
            case "SAVE":
            {
                _p++;
                if (!TryKw("TRAN")) ExpectKw("TRANSACTION");
                if (!IsIdent && Cur.Kind != Tk.Var) throw Error();
                return new TranStmt { Kind = TranKind.Save, Name = _t[_p++].Text };
            }
            case "PRINT":
                _p++;
                return new PrintStmt { Value = ParseExpr() };
            case "RETURN":
                _p++;
                return new ReturnStmt { Value = AtStatementEnd() ? null : ParseExpr() };
            case "BREAK": _p++; return new BreakStmt();
            case "CONTINUE": _p++; return new ContinueStmt();
            case "THROW":
            {
                _p++;
                var s = new ThrowStmt();
                if (!AtStatementEnd())
                {
                    s.Number = ParseAdditive();
                    ExpectSym(",");
                    s.Message = ParseAdditive();
                    ExpectSym(",");
                    s.State = ParseAdditive();
                }
                return s;
            }
            case "RAISERROR":
            {
                _p++;
                var s = new RaiserrorStmt();
                ExpectSym("(");
                do { s.Args.Add(ParseExpr()); } while (TrySym(","));
                ExpectSym(")");
                if (IsKw("WITH") && (IsKwAt(1, "NOWAIT") || IsKwAt(1, "LOG") || IsKwAt(1, "SETERROR")))
                {
                    _p += 2;
                    while (TrySym(",")) Ident();
                }
                return s;
            }
            case "EXEC":
            case "EXECUTE": return ParseExec();
            case "USE":
                _p++;
                return new UseStmt { Name = Ident() };
            case "OPEN": _p++; return new CursorStmt { Op = CursorOp.Open, Name = Ident() };
            case "CLOSE": _p++; return new CursorStmt { Op = CursorOp.Close, Name = Ident() };
            case "DEALLOCATE": _p++; return new CursorStmt { Op = CursorOp.Deallocate, Name = Ident() };
            case "FETCH":
            {
                _p++;
                if (TryKw("NEXT")) { }
                else if (IsKw("PRIOR") || IsKw("FIRST") || IsKw("LAST") || IsKw("ABSOLUTE") || IsKw("RELATIVE"))
                    throw Err.NotSupported("FETCH " + Cur.Text.ToUpperInvariant());
                TryKw("FROM");
                var s = new FetchStmt { Name = Ident() };
                if (TryKw("INTO"))
                {
                    do
                    {
                        if (Cur.Kind != Tk.Var) throw Error();
                        s.Into.Add(_t[_p++].Text);
                    } while (TrySym(","));
                }
                return s;
            }
            case "WAITFOR":
                _p++;
                if (!TryKw("DELAY")) ExpectKw("TIME");
                ParseExpr();
                return new BlockStmt();
            case "GOTO":
                throw Err.NotSupported("GOTO");
            default:
                throw Error();
        }
    }

    Statement ParseBegin()
    {
        _p++;
        if (TryKw("TRAN") || TryKw("TRANSACTION"))
        {
            string? name = null;
            if (IsAliasCandidate || Cur.Kind == Tk.Var) name = _t[_p++].Text;
            return new TranStmt { Kind = TranKind.Begin, Name = name };
        }
        if (TryKw("TRY"))
        {
            var s = new TryCatchStmt { Try = ParseUntilEnd() };
            ExpectKw("END");
            ExpectKw("TRY");
            TrySym(";");
            ExpectKw("BEGIN");
            ExpectKw("CATCH");
            s.Catch = ParseUntilEnd();
            ExpectKw("END");
            ExpectKw("CATCH");
            return s;
        }
        var block = new BlockStmt { Body = ParseUntilEnd() };
        ExpectKw("END");
        return block;
    }

    Statement ParseDeclare()
    {
        _p++;
        if (Cur.Kind != Tk.Var)
        {
            string name = Ident();
            ExpectKw("CURSOR");
            while (!IsKw("FOR"))
            {
                if (Cur.Kind == Tk.Eof) throw Error();
                _p++;
            }
            _p++;
            var q = ParseQuery(allowFor: true);
            if (TryKw("FOR"))
            {
                if (TryKw("READ")) ExpectKw("ONLY");
                else { ExpectKw("UPDATE"); }
            }
            return new DeclareCursorStmt { Name = name, Query = q };
        }
        var s = new DeclareStmt();
        do
        {
            if (Cur.Kind != Tk.Var) throw Error();
            var item = new DeclareItem { Name = _t[_p++].Text };
            TryKw("AS");
            if (TryKw("TABLE")) item.Table = ParseTableBody(new TableName { Name = item.Name });
            else
            {
                item.Type = ParseType(allowUserType: true);
                item.UserType = _lastUserType;
                if (TrySym("=")) item.Init = ParseExpr();
            }
            s.Items.Add(item);
        } while (TrySym(","));
        return s;
    }

    Statement ParseSet()
    {
        _p++;
        if (Cur.Kind == Tk.Var)
        {
            string name = _t[_p++].Text;
            if (Cur.Kind != Tk.Sym || !(Cur.Text is "=" or "+=" or "-=" or "*=" or "/=" or "%=" or "&=" or "|=" or "^=")) throw Error();
            string op = _t[_p++].Text;
            return new SetVarStmt { Name = name, Op = op, Value = ParseExpr() };
        }
        string option = Ident().ToUpperInvariant();
        var s = new SetOptionStmt { Option = option };
        if (option == "IDENTITY_INSERT")
        {
            s.Table = ParseTableName();
            s.Value = Ident().ToUpperInvariant();
        }
        else if (option == "TRANSACTION")
        {
            ExpectKw("ISOLATION");
            ExpectKw("LEVEL");
            s.Value = Ident();
            if (IsKw("COMMITTED") || IsKw("UNCOMMITTED") || IsKw("READ")) _p++;
        }
        else
        {
            if (option == "STATISTICS") Ident();
            while (TrySym(",")) Ident();
            if (Cur.Kind is Tk.Ident or Tk.Int or Tk.Str or Tk.NStr or Tk.Var or Tk.Quoted) s.Value = _t[_p++].Text;
            else throw Error();
        }
        return s;
    }

    ExecStmt ParseExec()
    {
        _p++;
        var s = new ExecStmt();
        if (TrySym("("))
        {
            s.DynamicSql = ParseExpr();
            ExpectSym(")");
            return s;
        }
        if (Cur.Kind == Tk.Var && IsSymAt(1, "="))
        {
            s.ReturnVar = Cur.Text;
            _p += 2;
        }
        if (Cur.Kind == Tk.Var) throw Err.NotSupported("EXECUTE with a procedure name variable");
        s.Proc = ParseTableName();
        while (!AtStatementEnd())
        {
            string? name = null;
            if (Cur.Kind == Tk.Var && IsSymAt(1, "="))
            {
                name = Cur.Text;
                _p += 2;
            }
            var arg = new ExecArg { Name = name, Value = ParseExpr() };
            if (TryKw("OUTPUT") || TryKw("OUT")) arg.Output = true;
            s.Args.Add(arg);
            if (!TrySym(",")) break;
        }
        if (IsKw("WITH") && IsKwAt(1, "RECOMPILE")) _p += 2;
        return s;
    }

    // ------------------------------------------------------------ DML

    TableName ParseTargetName()
    {
        TableName name = Cur.Kind == Tk.Var ? new TableName { Name = _t[_p++].Text } : ParseTableName();
        if (IsKw("WITH") && IsSymAt(1, "(")) { _p++; SkipParens(); }
        return name;
    }

    TableName ParseTableName()
    {
        var parts = new List<string> { Ident() };
        while (IsSym("."))
        {
            _p++;
            if (IsSym(".")) { parts.Add(""); continue; }
            parts.Add(Ident());
        }
        string? Part(int fromEnd) => parts.Count >= fromEnd && parts[^fromEnd].Length > 0 ? parts[^fromEnd] : null;
        return new TableName { Name = parts[^1], Schema = Part(2), Database = Part(3) };
    }

    Expr? ParseTop()
    {
        if (!TryKw("TOP")) return null;
        if (TrySym("("))
        {
            var e = ParseExpr();
            ExpectSym(")");
            return e;
        }
        return ParsePrimary();
    }

    InsertStmt ParseInsert()
    {
        _p++;
        ParseTop();
        TryKw("INTO");
        var s = new InsertStmt { Target = ParseTargetName() };
        if (IsSym("(") && !IsKwAt(1, "SELECT") && !IsKwAt(1, "WITH")) s.Columns = IdentList();
        if (IsKw("OUTPUT")) s.Output = ParseOutput();
        if (TryKw("VALUES")) s.Values = ParseValuesRows();
        else if (IsKw("DEFAULT")) { _p++; ExpectKw("VALUES"); s.DefaultValues = true; }
        else if (IsKw("EXEC") || IsKw("EXECUTE")) s.Exec = ParseExec();
        else s.Select = ParseQuery();
        return s;
    }

    List<List<Expr>> ParseValuesRows()
    {
        var rows = new List<List<Expr>>();
        do
        {
            ExpectSym("(");
            var row = new List<Expr>();
            do { row.Add(ParseExpr()); } while (TrySym(","));
            ExpectSym(")");
            rows.Add(row);
        } while (TrySym(","));
        return rows;
    }

    List<SetClause> ParseSetClauses()
    {
        var list = new List<SetClause>();
        do
        {
            string? column = null, variable = null;
            if (Cur.Kind == Tk.Var) variable = _t[_p++].Text;
            else
            {
                column = Ident();
                while (TrySym(".")) column = Ident();
            }
            if (Cur.Kind != Tk.Sym || !(Cur.Text is "=" or "+=" or "-=" or "*=" or "/=" or "%=" or "&=" or "|=" or "^=")) throw Error();
            string op = _t[_p++].Text;
            if (variable != null && op == "=" && IsIdent && IsSymAt(1, "="))
            {
                // SET @v = col = expr
                column = Ident();
                _p++;
                var value = ParseExpr();
                list.Add(new SetClause { Column = column, Value = value });
                list.Add(new SetClause { Variable = variable, Value = new ColumnRef("inserted", column) });
                continue;
            }
            list.Add(new SetClause { Column = column, Variable = variable, Op = op, Value = ParseExpr() });
        } while (TrySym(","));
        return list;
    }

    UpdateStmt ParseUpdate()
    {
        _p++;
        var top = ParseTop();
        var s = new UpdateStmt { Target = ParseTargetName(), Top = top };
        ExpectKw("SET");
        s.Sets = ParseSetClauses();
        if (IsKw("OUTPUT")) s.Output = ParseOutput();
        if (TryKw("FROM")) s.From = ParseFrom();
        if (TryKw("WHERE")) s.Where = ParseExpr();
        SkipOption();
        return s;
    }

    DeleteStmt ParseDelete()
    {
        _p++;
        var top = ParseTop();
        TryKw("FROM");
        var s = new DeleteStmt { Target = ParseTargetName(), Top = top };
        if (IsKw("OUTPUT")) s.Output = ParseOutput();
        if (TryKw("FROM")) s.From = ParseFrom();
        if (TryKw("WHERE")) s.Where = ParseExpr();
        SkipOption();
        return s;
    }

    void SkipOption()
    {
        if (IsKw("OPTION") && IsSymAt(1, "(")) { _p++; SkipParens(); }
    }

    OutputClause ParseOutput()
    {
        _p++;
        var o = new OutputClause();
        do { o.Items.Add(ParseSelectItem()); } while (TrySym(","));
        if (TryKw("INTO"))
        {
            o.Into = ParseTargetName();
            if (IsSym("(")) o.IntoColumns = IdentList();
        }
        return o;
    }

    MergeStmt ParseMerge()
    {
        _p++;
        ParseTop();
        TryKw("INTO");
        var target = ParseTargetName();
        string? alias = null;
        if (TryKw("AS")) alias = Ident();
        else if (IsAliasCandidate) alias = Ident();
        ExpectKw("USING");
        var source = ParseTablePrimary();
        ExpectKw("ON");
        var s = new MergeStmt { Target = target, TargetAlias = alias, Source = source, On = ParseExpr() };
        while (TryKw("WHEN"))
        {
            var c = new MergeClause();
            if (TryKw("MATCHED")) c.Match = MergeMatch.Matched;
            else
            {
                ExpectKw("NOT");
                ExpectKw("MATCHED");
                c.Match = MergeMatch.NotMatchedByTarget;
                if (TryKw("BY"))
                {
                    if (TryKw("SOURCE")) c.Match = MergeMatch.NotMatchedBySource;
                    else ExpectKw("TARGET");
                }
            }
            if (TryKw("AND")) c.Condition = ParseExpr();
            ExpectKw("THEN");
            if (TryKw("UPDATE"))
            {
                ExpectKw("SET");
                c.Action = MergeAction.Update;
                c.Sets = ParseSetClauses();
            }
            else if (TryKw("DELETE")) c.Action = MergeAction.Delete;
            else
            {
                ExpectKw("INSERT");
                c.Action = MergeAction.Insert;
                if (IsSym("(")) c.InsertColumns = IdentList();
                if (TryKw("DEFAULT")) { ExpectKw("VALUES"); c.InsertDefaultValues = true; }
                else
                {
                    ExpectKw("VALUES");
                    ExpectSym("(");
                    c.InsertValues = [];
                    do { c.InsertValues.Add(ParseExpr()); } while (TrySym(","));
                    ExpectSym(")");
                }
            }
            s.Clauses.Add(c);
        }
        if (IsKw("OUTPUT")) s.Output = ParseOutput();
        SkipOption();
        return s;
    }

    // ------------------------------------------------------------ DDL

    Statement ParseCreate()
    {
        _p++;
        bool orAlter = false;
        if (IsKw("OR") && IsKwAt(1, "ALTER")) { _p += 2; orAlter = true; }
        return ParseCreateBody(orAlter);
    }

    Statement ParseCreateBody(bool orAlter)
    {
        if (TryKw("TABLE")) return ParseTableBody(ParseTableName());
        if (TryKw("VIEW"))
        {
            var name = ParseTableName();
            List<string>? cols = IsSym("(") ? IdentList() : null;
            if (TryKw("WITH")) { do { Ident(); } while (TrySym(",")); }
            ExpectKw("AS");
            var q = ParseQuery();
            if (IsKw("WITH") && IsKwAt(1, "CHECK")) { _p += 2; ExpectKw("OPTION"); }
            return new CreateViewStmt { Name = name, Columns = cols, Query = q, OrAlter = orAlter };
        }
        if (TryKw("PROC") || TryKw("PROCEDURE"))
        {
            var s = new CreateProcStmt { Name = ParseTableName(), OrAlter = orAlter };
            bool paren = TrySym("(");
            while (Cur.Kind == Tk.Var)
            {
                s.Params.Add(ParseParam());
                if (!TrySym(",")) break;
            }
            if (paren) ExpectSym(")");
            if (TryKw("WITH"))
            {
                // RECOMPILE, ENCRYPTION, EXECUTE AS { CALLER | OWNER | SELF | 'user' }: accepted and ignored
                do
                {
                    bool executeAs = IsKw("EXECUTE") || IsKw("EXEC");
                    _p++;
                    if (executeAs) { ExpectKw("AS"); _p++; }
                } while (TrySym(","));
            }
            if (IsKw("FOR") && IsKwAt(1, "REPLICATION")) _p += 2;
            ExpectKw("AS");
            while (true)
            {
                while (TrySym(";")) { }
                if (Cur.Kind == Tk.Eof || IsKw("GO")) break;
                s.Body.Add(ParseStatement());
            }
            return s;
        }
        if (TryKw("FUNCTION"))
        {
            var s = new CreateFunctionStmt { Name = ParseTableName(), OrAlter = orAlter };
            ExpectSym("(");
            while (Cur.Kind == Tk.Var)
            {
                s.Params.Add(ParseParam());
                if (!TrySym(",")) break;
            }
            ExpectSym(")");
            ExpectKw("RETURNS");
            bool inline = false;
            if (Cur.Kind == Tk.Var)
            {
                s.TableVar = _t[_p++].Text;
                ExpectKw("TABLE");
                s.TableDef = ParseTableBody(new TableName { Name = s.TableVar });
            }
            else if (TryKw("TABLE")) inline = true;
            else s.ReturnType = ParseType();
            if (TryKw("WITH"))
            {
                // SCHEMABINDING, RETURNS NULL ON NULL INPUT, CALLED ON NULL INPUT, EXECUTE AS ...: accepted and ignored
                do
                {
                    if (IsKw("EXECUTE") || IsKw("EXEC")) { _p++; ExpectKw("AS"); _p++; }
                    else
                    {
                        while (Cur.Kind == Tk.Ident && !IsKw("AS") && !IsKw("BEGIN") && !IsKw("RETURN")) _p++;
                    }
                } while (TrySym(","));
            }
            TryKw("AS");
            if (inline)
            {
                ExpectKw("RETURN");
                s.InlineQuery = ParseQuery();
            }
            else
            {
                ExpectKw("BEGIN");
                s.Body = ParseUntilEnd();
                ExpectKw("END");
            }
            return s;
        }
        if (TryKw("TRIGGER"))
        {
            var name = ParseTableName();
            ExpectKw("ON");
            var s = new CreateTriggerStmt { Name = name, Table = ParseTableName(), OrAlter = orAlter };
            if (TryKw("INSTEAD")) { ExpectKw("OF"); s.InsteadOf = true; }
            else if (!TryKw("AFTER")) ExpectKw("FOR");
            do
            {
                if (TryKw("INSERT")) s.OnInsert = true;
                else if (TryKw("UPDATE")) s.OnUpdate = true;
                else if (TryKw("DELETE")) s.OnDelete = true;
                else throw Error();
            } while (TrySym(","));
            if (IsKw("NOT") && IsKwAt(1, "FOR")) _p += 3;
            ExpectKw("AS");
            while (true)
            {
                while (TrySym(";")) { }
                if (Cur.Kind == Tk.Eof || IsKw("GO")) break;
                s.Body.Add(ParseStatement());
            }
            return s;
        }
        if (TryKw("TYPE"))
        {
            var s = new CreateTypeStmt { Name = ParseTableName() };
            if (TryKw("FROM"))
            {
                s.BaseType = ParseType();
                if (IsKw("NOT") && IsKwAt(1, "NULL")) { _p += 2; s.Nullable = false; }
                else if (TryKw("NULL")) s.Nullable = true;
            }
            else
            {
                ExpectKw("AS");
                ExpectKw("TABLE");
                s.TableDef = ParseTableBody(s.Name);
            }
            return s;
        }
        if (TryKw("SEQUENCE"))
        {
            var s = new CreateSequenceStmt { Name = ParseTableName() };
            while (true)
            {
                if (TryKw("AS")) s.Type = ParseType();
                else if (TryKw("START")) { ExpectKw("WITH"); s.Start = SignedLong(); }
                else if (TryKw("INCREMENT")) { ExpectKw("BY"); s.Increment = SignedLong(); }
                else if (TryKw("MINVALUE")) s.Min = SignedLong();
                else if (TryKw("MAXVALUE")) s.Max = SignedLong();
                else if (TryKw("CYCLE")) s.Cycle = true;
                else if (TryKw("CACHE")) { if (Cur.Kind == Tk.Int) _p++; }
                else if (IsKw("NO") && (IsKwAt(1, "MINVALUE") || IsKwAt(1, "MAXVALUE") || IsKwAt(1, "CYCLE") || IsKwAt(1, "CACHE"))) _p += 2;
                else break;
            }
            return s;
        }
        if (TryKw("SCHEMA"))
        {
            var s = new CreateSchemaStmt { Name = Ident() };
            if (TryKw("AUTHORIZATION")) Ident();
            return s;
        }
        if (TryKw("DATABASE")) return new CreateDatabaseStmt { Name = Ident() };
        if (IsKw("UNIQUE") || IsKw("CLUSTERED") || IsKw("NONCLUSTERED") || IsKw("INDEX") || IsKw("COLUMNSTORE"))
        {
            bool unique = TryKw("UNIQUE");
            bool clustered = TryKw("CLUSTERED");
            if (!clustered) TryKw("NONCLUSTERED");
            if (TryKw("COLUMNSTORE")) clustered = false;
            ExpectKw("INDEX");
            string name = Ident();
            ExpectKw("ON");
            var s = new CreateIndexStmt { Name = name, Table = ParseTableName(), Unique = unique, Clustered = clustered };
            if (IsSym("("))
            {
                s.Columns = ParseIndexColumns();
                s.Descending = _lastDescending;
            }
            if (TryKw("INCLUDE")) SkipParens();
            if (TryKw("WHERE")) ParseExpr();
            SkipStorageOptions();
            return s;
        }
        throw Err.NotSupported("CREATE " + Cur.Text.ToUpperInvariant());
    }

    Statement ParseAlter()
    {
        _p++;
        if (IsKw("VIEW") || IsKw("PROC") || IsKw("PROCEDURE") || IsKw("FUNCTION") || IsKw("TRIGGER")) return ParseCreateBody(orAlter: true);
        if (IsKw("DATABASE") || IsKw("INDEX"))
        {
            SkipStatement();
            return new BlockStmt();
        }
        if (TryKw("SEQUENCE"))
        {
            var seq = new AlterSequenceStmt { Name = ParseTableName() };
            while (true)
            {
                if (TryKw("RESTART")) { seq.Restart = true; if (TryKw("WITH")) seq.RestartWith = SignedLong(); }
                else if (TryKw("INCREMENT")) { ExpectKw("BY"); seq.Increment = SignedLong(); }
                else if (TryKw("MINVALUE") || TryKw("MAXVALUE") || TryKw("CACHE")) SignedLong();
                else if (TryKw("CYCLE")) { }
                else if (TryKw("NO")) _p++;
                else break;
            }
            return seq;
        }
        ExpectKw("TABLE");
        var s = new AlterTableStmt { Name = ParseTableName() };
        if (IsKw("WITH") && (IsKwAt(1, "CHECK") || IsKwAt(1, "NOCHECK"))) _p += 2;
        if (TryKw("ADD"))
        {
            do
            {
                if (IsConstraintStart) s.AddConstraints.Add(ParseTableConstraint());
                else ParseColumnDef(s.AddColumns, s.AddConstraints);
            } while (TrySym(","));
        }
        else if (TryKw("DROP"))
        {
            do
            {
                bool column = TryKw("COLUMN");
                if (!column) TryKw("CONSTRAINT");
                if (IsKw("IF") && IsKwAt(1, "EXISTS")) { _p += 2; s.IfExists = true; }
                (column ? s.DropColumns : s.DropConstraints).Add(Ident());
            } while (TrySym(","));
        }
        else if (TryKw("ALTER"))
        {
            ExpectKw("COLUMN");
            var c = new ColumnDef { Name = Ident(), Type = ParseType() };
            if (TryKw("COLLATE")) Ident();
            if (TryKw("NOT")) { ExpectKw("NULL"); c.Nullable = false; }
            else if (TryKw("NULL")) c.Nullable = true;
            s.AlterColumn = c;
        }
        else if (TryKw("CHECK") || TryKw("NOCHECK"))
        {
            ExpectKw("CONSTRAINT");
            if (!TryKw("ALL")) Ident();
        }
        else throw Error();
        return s;
    }

    Statement ParseDrop()
    {
        _p++;
        var s = new DropStmt();
        if (TryKw("TABLE")) s.Kind = ObjectKind.Table;
        else if (TryKw("VIEW")) s.Kind = ObjectKind.View;
        else if (TryKw("PROC") || TryKw("PROCEDURE")) s.Kind = ObjectKind.Procedure;
        else if (TryKw("FUNCTION")) s.Kind = ObjectKind.Function;
        else if (TryKw("INDEX")) s.Kind = ObjectKind.Index;
        else if (TryKw("SCHEMA")) s.Kind = ObjectKind.Schema;
        else if (TryKw("DATABASE")) s.Kind = ObjectKind.Database;
        else if (TryKw("TRIGGER")) s.Kind = ObjectKind.Trigger;
        else if (TryKw("SEQUENCE")) s.Kind = ObjectKind.Sequence;
        else if (TryKw("TYPE")) s.Kind = ObjectKind.Type;
        else throw Err.NotSupported("DROP " + Cur.Text.ToUpperInvariant());
        if (IsKw("IF") && IsKwAt(1, "EXISTS")) { _p += 2; s.IfExists = true; }
        do
        {
            var name = ParseTableName();
            if (s.Kind == ObjectKind.Index)
            {
                if (TryKw("ON")) s.OnTable = ParseTableName();
                else if (name.Schema != null)
                {
                    s.OnTable = new TableName { Name = name.Schema, Schema = name.Database };
                    name = new TableName { Name = name.Name };
                }
            }
            s.Names.Add(name);
        } while (TrySym(","));
        return s;
    }

    bool IsConstraintStart => IsKw("CONSTRAINT") || IsKw("PRIMARY") || IsKw("UNIQUE") || IsKw("FOREIGN") || IsKw("CHECK")
                              || IsKw("DEFAULT");

    CreateTableStmt ParseTableBody(TableName name)
    {
        var s = new CreateTableStmt { Name = name };
        ExpectSym("(");
        do
        {
            if (IsSym(")")) break;
            if (IsKw("INDEX"))
            {
                // inline index definition: INDEX name [UNIQUE] [CLUSTERED|NONCLUSTERED] (cols)
                _p++;
                string ixName = Ident();
                bool unique = TryKw("UNIQUE");
                bool? clustered = ParseClustered();
                var cols = ParseIndexColumns();
                if (unique) s.Constraints.Add(new ConstraintDef { Kind = ConstraintKind.Unique, Name = ixName, Columns = cols, Clustered = clustered, Descending = _lastDescending });
            }
            else if (IsConstraintStart) s.Constraints.Add(ParseTableConstraint());
            else ParseColumnDef(s.Columns, s.Constraints);
        } while (TrySym(","));
        ExpectSym(")");
        SkipStorageOptions();
        return s;
    }

    void SkipStorageOptions()
    {
        while (true)
        {
            if (IsKw("WITH") && IsSymAt(1, "(")) { _p++; SkipParens(); }
            else if (IsKw("ON") && Peek().Kind is Tk.Ident or Tk.Quoted && !IsKwAt(1, "DELETE") && !IsKwAt(1, "UPDATE")) _p += 2;
            else if (IsKw("TEXTIMAGE_ON")) _p += 2;
            else break;
        }
    }

    /// <summary>The ASC / DESC flags of the key column list parsed last (null when all ascending).</summary>
    bool[]? _lastDescending;

    List<string> ParseIndexColumns()
    {
        var cols = new List<string>();
        var desc = new List<bool>();
        ExpectSym("(");
        do
        {
            cols.Add(Ident());
            bool d = TryKw("DESC");
            if (!d) TryKw("ASC");
            desc.Add(d);
        } while (TrySym(","));
        ExpectSym(")");
        _lastDescending = desc.Contains(true) ? desc.ToArray() : null;
        return cols;
    }

    /// <summary>Optional CLUSTERED / NONCLUSTERED: true, false, or null when not specified.</summary>
    bool? ParseClustered()
    {
        if (TryKw("CLUSTERED")) return true;
        if (TryKw("NONCLUSTERED")) return false;
        return null;
    }

    void ParseFkTail(ConstraintDef d)
    {
        d.RefTable = ParseTableName();
        if (IsSym("(")) d.RefColumns = IdentList();
        while (IsKw("ON") && (IsKwAt(1, "DELETE") || IsKwAt(1, "UPDATE")))
        {
            bool del = IsKwAt(1, "DELETE");
            _p += 2;
            if (TryKw("CASCADE")) { if (del) d.CascadeDelete = true; else d.CascadeUpdate = true; }
            else if (TryKw("NO")) ExpectKw("ACTION");
            else
            {
                ExpectKw("SET");
                if (TryKw("NULL")) { if (del) d.SetNullOnDelete = true; }
                else ExpectKw("DEFAULT");
            }
        }
    }

    ConstraintDef ParseTableConstraint()
    {
        var d = new ConstraintDef();
        if (TryKw("CONSTRAINT")) d.Name = Ident();
        if (TryKw("PRIMARY"))
        {
            ExpectKw("KEY");
            d.Clustered = ParseClustered();
            d.Kind = ConstraintKind.PrimaryKey;
            d.Columns = ParseIndexColumns();
            d.Descending = _lastDescending;
            SkipStorageOptions();
        }
        else if (TryKw("UNIQUE"))
        {
            d.Clustered = ParseClustered();
            d.Kind = ConstraintKind.Unique;
            d.Columns = ParseIndexColumns();
            d.Descending = _lastDescending;
            SkipStorageOptions();
        }
        else if (TryKw("FOREIGN"))
        {
            ExpectKw("KEY");
            d.Kind = ConstraintKind.ForeignKey;
            d.Columns = IdentList();
            ExpectKw("REFERENCES");
            ParseFkTail(d);
        }
        else if (TryKw("CHECK"))
        {
            d.Kind = ConstraintKind.Check;
            ExpectSym("(");
            d.Expr = ParseExpr();
            ExpectSym(")");
        }
        else if (TryKw("DEFAULT"))
        {
            d.Kind = ConstraintKind.Default;
            d.Expr = ParseAdditive();
            ExpectKw("FOR");
            d.Columns.Add(Ident());
        }
        else throw Error();
        return d;
    }

    void ParseColumnDef(List<ColumnDef> cols, List<ConstraintDef> cons)
    {
        var c = new ColumnDef { Name = Ident() };
        if (TryKw("AS"))
        {
            c.Computed = ParseExpr();
            TryKw("PERSISTED");
        }
        else
        {
            c.RowVersion = IsKw("ROWVERSION") || IsKw("TIMESTAMP");
            c.Type = ParseType(allowUserType: true);
            c.UserType = _lastUserType;
        }
        while (true)
        {
            string? cname = null;
            if (TryKw("CONSTRAINT")) cname = Ident();
            if (IsKw("NOT") && IsKwAt(1, "NULL")) { _p += 2; c.Nullable = false; }
            else if (IsKw("NOT") && IsKwAt(1, "FOR")) { _p += 3; }
            else if (TryKw("NULL")) c.Nullable = true;
            else if (TryKw("IDENTITY"))
            {
                c.Identity = true;
                if (TrySym("("))
                {
                    c.Seed = SignedLong();
                    ExpectSym(",");
                    c.Increment = SignedLong();
                    ExpectSym(")");
                }
            }
            else if (TryKw("PRIMARY"))
            {
                ExpectKw("KEY");
                bool? clustered = ParseClustered();
                SkipStorageOptions();
                cons.Add(new ConstraintDef { Kind = ConstraintKind.PrimaryKey, Name = cname, Columns = [c.Name], Clustered = clustered });
            }
            else if (TryKw("UNIQUE"))
            {
                bool? clustered = ParseClustered();
                SkipStorageOptions();
                cons.Add(new ConstraintDef { Kind = ConstraintKind.Unique, Name = cname, Columns = [c.Name], Clustered = clustered });
            }
            else if (TryKw("DEFAULT"))
            {
                c.Default = ParseAdditive();
                c.DefaultName = cname;
                if (IsKw("WITH") && IsKwAt(1, "VALUES")) { _p += 2; c.WithValues = true; }
            }
            else if (IsKw("REFERENCES") || IsKw("FOREIGN"))
            {
                if (TryKw("FOREIGN")) ExpectKw("KEY");
                ExpectKw("REFERENCES");
                var d = new ConstraintDef { Kind = ConstraintKind.ForeignKey, Name = cname, Columns = [c.Name] };
                ParseFkTail(d);
                cons.Add(d);
            }
            else if (TryKw("CHECK"))
            {
                ExpectSym("(");
                cons.Add(new ConstraintDef { Kind = ConstraintKind.Check, Name = cname, Expr = ParseExpr() });
                ExpectSym(")");
            }
            else if (TryKw("COLLATE")) Ident();
            else if (TryKw("ROWGUIDCOL") || TryKw("SPARSE") || TryKw("PERSISTED")) { }
            else if (cname != null) throw Error();
            else break;
        }
        cols.Add(c);
    }

    ParamDef ParseParam()
    {
        string name = _t[_p++].Text;
        TryKw("AS");
        var p = new ParamDef { Name = name, Type = ParseType(allowUserType: true), UserType = _lastUserType };
        if (TrySym("=")) p.Default = ParseUnary();
        if (TryKw("OUTPUT") || TryKw("OUT")) p.Output = true;
        TryKw("READONLY");
        return p;
    }

    /// <summary>Set by ParseType when the name was not a built-in type (and user types are allowed there).</summary>
    TableName? _lastUserType;

    SqlType ParseType(int defaultLength = 1, bool allowUserType = false)
    {
        _lastUserType = null;
        string name = Ident();
        string? schema = null;
        if (IsSym(".") && Peek().Kind is Tk.Ident or Tk.Quoted) { _p++; schema = name; name = Ident(); }
        if (allowUserType && !IsSym("(") && SqlType.FromName(name) == null)
        {
            // resolved against CREATE TYPE definitions when the statement runs
            _lastUserType = new TableName { Name = name, Schema = schema };
            return SqlType.Variant;
        }
        if (name.Equals("double", StringComparison.OrdinalIgnoreCase)) TryKw("PRECISION");
        if (TryKw("VARYING")) name = name.StartsWith("n", StringComparison.OrdinalIgnoreCase) ? "nvarchar" : "varchar";
        int? a1 = null, a2 = null;
        bool max = false;
        if (TrySym("("))
        {
            if (TryKw("MAX")) max = true;
            else
            {
                a1 = (int)SignedLong();
                if (TrySym(",")) a2 = (int)SignedLong();
            }
            ExpectSym(")");
        }
        return SqlType.FromName(name, a1, a2, max, defaultLength)
               ?? throw Err.New(2715, $"Column, parameter, or variable #1: Cannot find data type {name}.");
    }

    // ------------------------------------------------------------ queries

    List<Cte> ParseCtes()
    {
        var list = new List<Cte>();
        do
        {
            string name = Ident();
            List<string>? cols = IsSym("(") ? IdentList() : null;
            ExpectKw("AS");
            ExpectSym("(");
            var q = ParseQuery();
            ExpectSym(")");
            list.Add(new Cte { Name = name, Columns = cols, Query = q });
        } while (TrySym(","));
        return list;
    }

    Query ParseQuery(List<Cte>? ctes = null, bool allowFor = false)
    {
        if (ctes == null && IsKw("WITH")) { _p++; ctes = ParseCtes(); }
        var q = new Query { Ctes = ctes, Body = ParseQueryExpr() };
        if (IsKw("ORDER"))
        {
            _p++;
            ExpectKw("BY");
            q.OrderBy = ParseOrderList();
            if (TryKw("OFFSET"))
            {
                q.Offset = ParseAdditive();
                if (!TryKw("ROWS")) ExpectKw("ROW");
                if (TryKw("FETCH"))
                {
                    if (!TryKw("NEXT")) ExpectKw("FIRST");
                    q.Fetch = ParseAdditive();
                    if (!TryKw("ROWS")) ExpectKw("ROW");
                    ExpectKw("ONLY");
                }
            }
        }
        if (IsKw("OPTION") && IsSymAt(1, "("))
        {
            // the only query hint with a semantic effect here is MAXRECURSION
            for (int k = _p + 2; _t[k].Kind != Tk.Eof && !(_t[k].Kind == Tk.Sym && _t[k].Text == ")"); k++)
                if (_t[k].Kind == Tk.Ident && _t[k].Text.Equals("MAXRECURSION", StringComparison.OrdinalIgnoreCase) && _t[k + 1].Kind == Tk.Int)
                    q.MaxRecursion = int.Parse(_t[k + 1].Text, CultureInfo.InvariantCulture);
            SkipOption();
        }
        if (IsKw("FOR") && IsKwAt(1, "JSON"))
        {
            _p += 2;
            var fj = new ForJsonClause();
            if (TryKw("AUTO")) fj.Auto = true;
            else ExpectKw("PATH");
            while (TrySym(","))
            {
                if (TryKw("ROOT"))
                {
                    fj.Root = "root";
                    if (TrySym("("))
                    {
                        if (Cur.Kind is not (Tk.Str or Tk.NStr)) throw Error();
                        fj.Root = _t[_p++].Text;
                        ExpectSym(")");
                    }
                }
                else if (TryKw("INCLUDE_NULL_VALUES")) fj.IncludeNulls = true;
                else if (TryKw("WITHOUT_ARRAY_WRAPPER")) fj.WithoutArrayWrapper = true;
                else throw Error();
            }
            q.ForJson = fj;
        }
        if (IsKw("FOR") && !allowFor) throw Err.NotSupported("FOR " + Peek().Text.ToUpperInvariant());
        return q;
    }

    List<OrderItem> ParseOrderList()
    {
        var list = new List<OrderItem>();
        do
        {
            var e = ParseExpr();
            bool desc = false;
            if (TryKw("DESC")) desc = true;
            else TryKw("ASC");
            list.Add(new OrderItem(e, desc));
        } while (TrySym(","));
        return list;
    }

    QueryExpr ParseQueryExpr()
    {
        var left = ParseQueryTerm();
        while (IsKw("UNION") || IsKw("EXCEPT"))
        {
            string op = _t[_p++].Text.ToUpperInvariant();
            bool all = TryKw("ALL");
            if (!all) TryKw("DISTINCT");
            left = new SetOp { Op = op, All = all, Left = left, Right = ParseQueryTerm() };
        }
        return left;
    }

    QueryExpr ParseQueryTerm()
    {
        var left = ParseQueryPrimary();
        while (TryKw("INTERSECT"))
        {
            bool all = TryKw("ALL");
            left = new SetOp { Op = "INTERSECT", All = all, Left = left, Right = ParseQueryPrimary() };
        }
        return left;
    }

    QueryExpr ParseQueryPrimary()
    {
        if (TrySym("("))
        {
            var q = ParseQuery();
            ExpectSym(")");
            return q.Ctes == null && q.OrderBy == null ? q.Body : new NestedQuery(q);
        }
        return ParseSelectSpec();
    }

    SelectSpec ParseSelectSpec()
    {
        ExpectKw("SELECT");
        var s = new SelectSpec();
        if (TryKw("DISTINCT")) s.Distinct = true;
        else TryKw("ALL");
        if (IsKw("TOP"))
        {
            s.Top = ParseTop();
            if (TryKw("PERCENT")) s.TopPercent = true;
            if (IsKw("WITH") && IsKwAt(1, "TIES")) { _p += 2; s.TopWithTies = true; }
        }
        do { s.Items.Add(ParseSelectItem()); } while (TrySym(","));
        if (TryKw("INTO")) s.Into = ParseTargetName();
        if (TryKw("FROM")) s.From = ParseFrom();
        if (TryKw("WHERE")) s.Where = ParseExpr();
        if (TryKw("GROUP"))
        {
            ExpectKw("BY");
            ParseGroupBy(s);
        }
        if (TryKw("HAVING")) s.Having = ParseExpr();
        return s;
    }

    static List<List<Expr>> Rollup(List<Expr> exprs)
    {
        var sets = new List<List<Expr>>();
        for (int n = exprs.Count; n >= 0; n--) sets.Add(exprs.GetRange(0, n));
        return sets;
    }

    static List<List<Expr>> Cube(List<Expr> exprs)
    {
        var sets = new List<List<Expr>>();
        for (int mask = (1 << exprs.Count) - 1; mask >= 0; mask--)
        {
            var set = new List<Expr>();
            for (int i = 0; i < exprs.Count; i++)
                if ((mask & (1 << (exprs.Count - 1 - i))) != 0) set.Add(exprs[i]);
            sets.Add(set);
        }
        return sets;
    }

    List<Expr> ParseParenExprList()
    {
        var list = new List<Expr>();
        ExpectSym("(");
        if (!IsSym(")"))
        {
            do { list.Add(ParseExpr()); } while (TrySym(","));
        }
        ExpectSym(")");
        return list;
    }

    /// <summary>One GROUP BY element as the list of grouping sets it stands for.</summary>
    List<List<Expr>> ParseGroupingElement()
    {
        if (IsKw("ROLLUP") && IsSymAt(1, "(")) { _p++; return Rollup(ParseParenExprList()); }
        if (IsKw("CUBE") && IsSymAt(1, "(")) { _p++; return Cube(ParseParenExprList()); }
        if (IsKw("GROUPING") && IsKwAt(1, "SETS"))
        {
            _p += 2;
            var sets = new List<List<Expr>>();
            ExpectSym("(");
            do
            {
                if (IsSym("(") && IsSymAt(1, ")")) { _p += 2; sets.Add([]); }
                else if ((IsKw("ROLLUP") || IsKw("CUBE")) && IsSymAt(1, "(")) sets.AddRange(ParseGroupingElement());
                else if (IsSym("(")) sets.Add(ParseParenExprList());
                else sets.Add([ParseExpr()]);
            } while (TrySym(","));
            ExpectSym(")");
            return sets;
        }
        if (IsSym("(") && IsSymAt(1, ")")) { _p += 2; return [[]]; }
        return [[ParseExpr()]];
    }

    void ParseGroupBy(SelectSpec s)
    {
        var elements = new List<List<List<Expr>>>();
        bool plain = true;
        do
        {
            var el = ParseGroupingElement();
            if (el.Count != 1 || el[0].Count != 1) plain = false;
            elements.Add(el);
        } while (TrySym(","));
        if (IsKw("WITH") && (IsKwAt(1, "ROLLUP") || IsKwAt(1, "CUBE")))
        {
            bool cube = IsKwAt(1, "CUBE");
            _p += 2;
            var all = elements.SelectMany(e => e[0]).ToList();
            elements = [cube ? Cube(all) : Rollup(all)];
            plain = false;
        }
        // the grouping sets of the whole clause are the cross product of its elements
        var sets = new List<List<Expr>> { new() };
        foreach (var el in elements)
            sets = sets.SelectMany(prefix => el.Select(set => prefix.Concat(set).ToList())).ToList();
        foreach (var set in sets)
            foreach (var e in set)
                if (!s.GroupBy.Contains(e)) s.GroupBy.Add(e);
        if (!plain) s.GroupingSets = sets;
    }

    SelectItem ParseSelectItem()
    {
        if (TrySym("*")) return new SelectItem { IsStar = true };
        // qualified star: a.*, schema.a.*
        for (int k = _p; _t[k].Kind is Tk.Ident or Tk.Quoted && _t[k + 1] is { Kind: Tk.Sym, Text: "." }; k += 2)
        {
            if (_t[k + 2] is { Kind: Tk.Sym, Text: "*" })
            {
                string table = _t[k].Text;
                _p = k + 3;
                return new SelectItem { IsStar = true, StarTable = table };
            }
        }
        if (Cur.Kind == Tk.Var && Peek() is { Kind: Tk.Sym } op && op.Text is "=" or "+=" or "-=" or "*=" or "/=" or "%=" or "&=" or "|=" or "^=")
        {
            string name = Cur.Text;
            _p += 2;
            return new SelectItem { Variable = name, VarOp = op.Text, E = ParseExpr() };
        }
        if ((IsAliasCandidate || Cur.Kind is Tk.Str or Tk.NStr) && IsSymAt(1, "="))
        {
            string alias = Cur.Text;
            _p += 2;
            return new SelectItem { Alias = alias, E = ParseExpr() };
        }
        var item = new SelectItem { E = ParseExpr() };
        if (TryKw("AS"))
        {
            if (Cur.Kind is Tk.Str or Tk.NStr or Tk.Ident or Tk.Quoted) item.Alias = _t[_p++].Text;
            else throw Error();
        }
        else if (IsAliasCandidate || Cur.Kind is Tk.Str or Tk.NStr) item.Alias = _t[_p++].Text;
        return item;
    }

    TableSource ParseFrom()
    {
        var src = ParseJoined();
        while (TrySym(",")) src = new JoinSource { Left = src, Right = ParseJoined(), Kind = JoinKind.Cross };
        return src;
    }

    void SkipJoinHint()
    {
        if (IsKw("LOOP") || IsKw("HASH") || (IsKw("MERGE") && IsKwAt(1, "JOIN")) || IsKw("REMOTE")) _p++;
    }

    TableSource ParseJoined()
    {
        var left = ParseTablePrimary();
        while (true)
        {
            JoinKind kind;
            if (TryKw("JOIN")) kind = JoinKind.Inner;
            else if (IsKw("INNER")) { _p++; SkipJoinHint(); ExpectKw("JOIN"); kind = JoinKind.Inner; }
            else if (IsKw("LEFT") && !IsSymAt(1, "(")) { _p++; TryKw("OUTER"); SkipJoinHint(); ExpectKw("JOIN"); kind = JoinKind.Left; }
            else if (IsKw("RIGHT") && !IsSymAt(1, "(")) { _p++; TryKw("OUTER"); SkipJoinHint(); ExpectKw("JOIN"); kind = JoinKind.Right; }
            else if (IsKw("FULL")) { _p++; TryKw("OUTER"); SkipJoinHint(); ExpectKw("JOIN"); kind = JoinKind.Full; }
            else if (IsKw("CROSS"))
            {
                _p++;
                if (TryKw("JOIN")) kind = JoinKind.Cross;
                else { ExpectKw("APPLY"); kind = JoinKind.CrossApply; }
            }
            else if (IsKw("OUTER") && IsKwAt(1, "APPLY")) { _p += 2; kind = JoinKind.OuterApply; }
            else break;
            var right = ParseTablePrimary();
            Expr? on = null;
            if (kind is JoinKind.Inner or JoinKind.Left or JoinKind.Right or JoinKind.Full)
            {
                // support "a JOIN b JOIN c ON ... ON ..." by letting the right side absorb nested joins
                if (!IsKw("ON")) right = ContinueJoin(right);
                ExpectKw("ON");
                on = ParseExpr();
            }
            left = new JoinSource { Left = left, Right = right, Kind = kind, On = on };
        }
        return left;
    }

    TableSource ContinueJoin(TableSource left)
    {
        if (!(IsKw("JOIN") || IsKw("INNER") || IsKw("LEFT") || IsKw("RIGHT") || IsKw("FULL"))) throw Error();
        JoinKind kind = JoinKind.Inner;
        if (TryKw("JOIN")) { }
        else if (TryKw("INNER")) ExpectKw("JOIN");
        else
        {
            kind = IsKw("LEFT") ? JoinKind.Left : IsKw("RIGHT") ? JoinKind.Right : JoinKind.Full;
            _p++;
            TryKw("OUTER");
            ExpectKw("JOIN");
        }
        var right = ParseTablePrimary();
        if (!IsKw("ON")) right = ContinueJoin(right);
        ExpectKw("ON");
        return new JoinSource { Left = left, Right = right, Kind = kind, On = ParseExpr() };
    }

    TableSource ParseTablePrimary()
    {
        var src = ParseTablePrimaryCore();
        while (true)
        {
            if (IsKw("PIVOT") && IsSymAt(1, "("))
            {
                _p += 2;
                string fn = Ident().ToUpperInvariant();
                if (ParseFunctionCall(fn, null) is not FuncCall agg) throw Error();
                ExpectKw("FOR");
                string forColumn = Ident();
                ExpectKw("IN");
                ExpectSym("(");
                var values = new List<string>();
                do
                {
                    if (Cur.Kind is Tk.Ident or Tk.Quoted or Tk.Int or Tk.Str or Tk.NStr) values.Add(_t[_p++].Text);
                    else throw Error();
                } while (TrySym(","));
                ExpectSym(")");
                ExpectSym(")");
                src = new PivotSource { Source = src, Aggregate = agg, ForColumn = forColumn, InValues = values, Alias = OptionalAlias() };
            }
            else if (IsKw("UNPIVOT") && IsSymAt(1, "("))
            {
                _p += 2;
                string valueColumn = Ident();
                ExpectKw("FOR");
                string nameColumn = Ident();
                ExpectKw("IN");
                var columns = IdentList();
                ExpectSym(")");
                src = new UnpivotSource { Source = src, ValueColumn = valueColumn, NameColumn = nameColumn, Columns = columns, Alias = OptionalAlias() };
            }
            else return src;
        }
    }

    TableSource ParseTablePrimaryCore()
    {
        if (IsSym("("))
        {
            int k = 0;
            while (IsSymAt(k, "(")) k++;
            if (IsKwAt(k, "SELECT") || IsKwAt(k, "WITH"))
            {
                _p++;
                var q = ParseQuery();
                ExpectSym(")");
                var d = new DerivedTable { Query = q, Alias = OptionalAlias() };
                if (d.Alias != null && IsSym("(")) d.Columns = IdentList();
                return d;
            }
            if (IsKwAt(1, "VALUES"))
            {
                _p += 2;
                var rows = ParseValuesRows();
                ExpectSym(")");
                var v = new ValuesTable { Rows = rows, Alias = OptionalAlias() };
                if (v.Alias != null && IsSym("(")) v.Columns = IdentList();
                return v;
            }
            _p++;
            var j = ParseFrom();
            ExpectSym(")");
            return j;
        }
        if (Cur.Kind == Tk.Var)
        {
            var tv = new TableRef { Name = new TableName { Name = _t[_p++].Text } };
            tv.Alias = OptionalAlias();
            return tv;
        }
        var name = ParseTableName();
        if (IsSym("(") && Peek() is { Kind: Tk.Ident } hint && TableHints.Contains(hint.Text))
        {
            SkipParens();
            return new TableRef { Name = name, Alias = OptionalAlias() };
        }
        if (TrySym("("))
        {
            var args = new List<Expr>();
            if (!IsSym(")"))
            {
                do { args.Add(ParseExpr()); } while (TrySym(","));
            }
            ExpectSym(")");
            var f = new FunctionTable { Name = name, Args = args };
            if (name.Name.Equals("OPENJSON", StringComparison.OrdinalIgnoreCase) && IsKw("WITH") && IsSymAt(1, "("))
            {
                _p += 2;
                f.JsonColumns = [];
                do
                {
                    var col = new OpenJsonColumn { Name = Ident(), Type = ParseType() };
                    if (Cur.Kind is Tk.Str or Tk.NStr) col.Path = _t[_p++].Text;
                    if (TryKw("AS")) { ExpectKw("JSON"); col.AsJson = true; }
                    f.JsonColumns.Add(col);
                } while (TrySym(","));
                ExpectSym(")");
            }
            f.Alias = OptionalAlias();
            if (f.Alias != null && IsSym("(")) f.Columns = IdentList();
            return f;
        }
        var t = new TableRef { Name = name, Alias = OptionalAlias() };
        if (IsKw("WITH") && IsSymAt(1, "(")) { _p++; SkipParens(); }
        else if (IsSym("(") && Peek() is { Kind: Tk.Ident } hint2 && TableHints.Contains(hint2.Text)) SkipParens();
        return t;
    }

    // ------------------------------------------------------------ expressions

    public Expr ParseExpr()
    {
        var left = ParseAnd();
        while (TryKw("OR")) left = new Binary("OR", left, ParseAnd());
        return left;
    }

    Expr ParseAnd()
    {
        var left = ParseNot();
        while (TryKw("AND")) left = new Binary("AND", left, ParseNot());
        return left;
    }

    Expr ParseNot()
    {
        if (TryKw("NOT")) return new Unary("NOT", ParseNot());
        return ParsePredicate();
    }

    Expr ParsePredicate()
    {
        var left = ParseAdditive();
        while (true)
        {
            if (Cur.Kind == Tk.Sym)
            {
                string op = Cur.Text;
                if (!(op is "=" or "<>" or "!=" or "<" or "<=" or ">" or ">=" or "!<" or "!>" or "!")) break;
                _p++;
                if (op is "<" or ">" or "!" && Cur.Kind == Tk.Sym && Cur.Text is "=" or ">" or "<" && (op + Cur.Text) is "<=" or ">=" or "<>" or "!=" or "!<" or "!>")
                    op += _t[_p++].Text;
                if (op == "!") throw Error();
                op = op switch { "!=" => "<>", "!<" => ">=", "!>" => "<=", _ => op };
                if ((IsKw("ANY") || IsKw("SOME") || IsKw("ALL")) && IsSymAt(1, "("))
                {
                    bool all = IsKw("ALL");
                    _p += 2;
                    var q = ParseQuery();
                    ExpectSym(")");
                    left = new QuantifiedExpr(op, left, q, all);
                }
                else left = new Binary(op, left, ParseAdditive());
                continue;
            }
            if (IsKw("IS"))
            {
                _p++;
                bool not = TryKw("NOT");
                ExpectKw("NULL");
                left = new IsNullExpr(left, not);
                continue;
            }
            bool neg = false;
            if (IsKw("NOT") && (IsKwAt(1, "IN") || IsKwAt(1, "LIKE") || IsKwAt(1, "BETWEEN"))) { _p++; neg = true; }
            if (TryKw("IN"))
            {
                ExpectSym("(");
                if (IsKw("SELECT") || IsKw("WITH"))
                {
                    var q = ParseQuery();
                    left = new InExpr(left, null, q, neg);
                }
                else
                {
                    var list = new List<Expr>();
                    do { list.Add(ParseExpr()); } while (TrySym(","));
                    left = new InExpr(left, list, null, neg);
                }
                ExpectSym(")");
                continue;
            }
            if (TryKw("LIKE"))
            {
                var pattern = ParseAdditive();
                Expr? escape = TryKw("ESCAPE") ? ParseAdditive() : null;
                left = new LikeExpr(left, pattern, escape, neg);
                continue;
            }
            if (TryKw("BETWEEN"))
            {
                var lo = ParseAdditive();
                ExpectKw("AND");
                left = new BetweenExpr(left, lo, ParseAdditive(), neg);
                continue;
            }
            break;
        }
        return left;
    }

    Expr ParseAdditive()
    {
        var left = ParseMultiplicative();
        while (Cur.Kind == Tk.Sym && Cur.Text is "+" or "-" or "&" or "|" or "^")
        {
            string op = _t[_p++].Text;
            left = new Binary(op, left, ParseMultiplicative());
        }
        return left;
    }

    Expr ParseMultiplicative()
    {
        var left = ParseUnary();
        while (Cur.Kind == Tk.Sym && Cur.Text is "*" or "/" or "%")
        {
            string op = _t[_p++].Text;
            left = new Binary(op, left, ParseUnary());
        }
        return left;
    }

    Expr ParseUnary()
    {
        if (TrySym("-"))
        {
            var e = ParseUnary();
            if (e is Literal { Value: not null and not string and not byte[] } l)
            {
                object v = TypeSystem.Negate(l.Value)!;
                return new Literal(v, v is decimal && l.Type?.Kind == SqlTypeKind.Decimal ? TypeSystem.TypeOf(v) : l.Type);
            }
            return new Unary("-", e);
        }
        if (TrySym("+")) return ParseUnary();
        if (TrySym("~")) return new Unary("~", ParseUnary());
        var p = ParsePrimary();
        while (IsKw("COLLATE")) _p += 2;
        if (IsKw("AT") && IsKwAt(1, "TIME")) throw Err.NotSupported("AT TIME ZONE");
        return p;
    }

    Expr ParsePrimary()
    {
        var t = Cur;
        switch (t.Kind)
        {
            case Tk.Int:
                _p++;
                if (int.TryParse(t.Text, NumberStyles.None, CultureInfo.InvariantCulture, out int i)) return new Literal(i, SqlType.Int);
                if (decimal.TryParse(t.Text, NumberStyles.None, CultureInfo.InvariantCulture, out decimal big))
                    return new Literal(big, SqlType.Decimal(t.Text.Length, 0));
                throw Err.New(1007, $"The number '{t.Text}' is out of the range for numeric representation (maximum precision 38).", 15);
            case Tk.Dec:
            {
                _p++;
                if (!decimal.TryParse(t.Text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out decimal d))
                    throw Err.New(1007, $"The number '{t.Text}' is out of the range for numeric representation (maximum precision 38).", 15);
                return new Literal(d, TypeSystem.TypeOf(d));
            }
            case Tk.Float:
                _p++;
                return new Literal(double.Parse(t.Text, NumberStyles.Float, CultureInfo.InvariantCulture), SqlType.Float);
            case Tk.Money:
                _p++;
                return new Literal(TypeSystem.ConvertTo(decimal.Parse(t.Text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture), SqlType.Money), SqlType.Money);
            case Tk.Str:
                _p++;
                return new Literal(t.Text, t.Text.Length > 8000 ? SqlType.VarCharMax : SqlType.VarChar(Math.Max(1, t.Text.Length)));
            case Tk.NStr:
                _p++;
                return new Literal(t.Text, t.Text.Length > 4000 ? SqlType.NVarCharMax : SqlType.NVarChar(Math.Max(1, t.Text.Length)));
            case Tk.Bin:
            {
                _p++;
                string hex = t.Text.Length % 2 == 1 ? "0" + t.Text : t.Text;
                byte[] bytes = Convert.FromHexString(hex);
                return new Literal(bytes, new SqlType(SqlTypeKind.VarBinary, Math.Max(1, bytes.Length)));
            }
            case Tk.Var:
                _p++;
                return new VarRef(t.Text);
            case Tk.Sym when t.Text == "{":
            {
                // ODBC escapes: {d '2020-01-02'}, {t '10:00:00'}, {ts '2020-01-02 10:00:00'}, {fn FUNC(...)}
                _p++;
                string kind = Ident().ToLowerInvariant();
                Expr value;
                if (kind == "fn")
                {
                    value = ParseExpr();
                    // ODBC scalar function names that differ from T-SQL's
                    if (value is FuncCall { Schema: null } odbc)
                    {
                        string? native = odbc.Name switch
                        {
                            "NOW" => "GETDATE", "UCASE" => "UPPER", "LCASE" => "LOWER", "LENGTH" => "LEN", "IFNULL" => "ISNULL",
                            "CURDATE" or "CURRENT_DATE" => "GETDATE", "CEILING" => "CEILING", "TRUNCATE" => "ROUND", _ => null,
                        };
                        if (native != null) value = new FuncCall(native, odbc.Name == "TRUNCATE" ? [.. odbc.Args, new Literal(1, SqlType.Int)] : odbc.Args);
                    }
                }
                else
                {
                    if (Cur.Kind is not (Tk.Str or Tk.NStr)) throw Error();
                    var text = new Literal(_t[_p++].Text, SqlType.VarChar(30));
                    value = kind switch
                    {
                        "d" => new CastExpr(text, SqlType.DateTime, false, null),
                        "t" => new CastExpr(text, SqlType.DateTime, false, null),
                        "ts" => new CastExpr(text, SqlType.DateTime, false, null),
                        _ => throw Error(),
                    };
                }
                ExpectSym("}");
                return value;
            }
            case Tk.Sym when t.Text == "(":
            {
                _p++;
                if (IsKw("SELECT") || IsKw("WITH"))
                {
                    var q = ParseQuery();
                    ExpectSym(")");
                    return new SubqueryExpr(q);
                }
                var e = ParseExpr();
                ExpectSym(")");
                return e;
            }
            case Tk.Ident:
                switch (t.Text.ToUpperInvariant())
                {
                    case "NULL": _p++; return new Literal(null, null);
                    case "DEFAULT": _p++; return new DefaultExpr();
                    case "CASE": return ParseCase();
                    case "EXISTS" when IsSymAt(1, "("):
                    {
                        _p += 2;
                        var q = ParseQuery();
                        ExpectSym(")");
                        return new ExistsExpr(q);
                    }
                    case "CAST" or "TRY_CAST" when IsSymAt(1, "("):
                    {
                        bool isTry = t.Text.Length > 4;
                        _p += 2;
                        var e = ParseExpr();
                        ExpectKw("AS");
                        var type = ParseType(30);
                        ExpectSym(")");
                        return new CastExpr(e, type, isTry, null);
                    }
                    case "CONVERT" or "TRY_CONVERT" when IsSymAt(1, "("):
                    {
                        bool isTry = t.Text.Length > 7;
                        _p += 2;
                        var type = ParseType(30);
                        ExpectSym(",");
                        var e = ParseExpr();
                        Expr? style = TrySym(",") ? ParseExpr() : null;
                        ExpectSym(")");
                        return new CastExpr(e, type, isTry, style);
                    }
                    case "CURRENT_TIMESTAMP" or "CURRENT_USER" or "SESSION_USER" or "SYSTEM_USER" or "USER" when !IsSymAt(1, "(") && !IsSymAt(1, "."):
                        _p++;
                        return new FuncCall(t.Text.ToUpperInvariant(), []);
                    case "NEXT" when IsKwAt(1, "VALUE") && IsKwAt(2, "FOR"):
                        _p += 3;
                        return new NextValueExpr(ParseTableName());
                    case "UPDATE" when IsSymAt(1, "("):
                    {
                        // UPDATE(column) inside a trigger
                        _p += 2;
                        var f = new FuncCall("UPDATE", [new Literal(Ident(), SqlType.VarChar(128))]);
                        ExpectSym(")");
                        return f;
                    }
                }
                if (Reserved.Contains(t.Text) && !NotReallyReserved.Contains(t.Text) && !((IsKw("LEFT") || IsKw("RIGHT")) && IsSymAt(1, "("))) throw Error();
                break;
            case Tk.Quoted:
                break;
            default:
                throw Error();
        }

        var parts = new List<string> { _t[_p++].Text };
        while (IsSym(".") && Peek().Kind is Tk.Ident or Tk.Quoted)
        {
            _p++;
            parts.Add(_t[_p++].Text);
        }
        if (IsSym("(")) return ParseFunctionCall(parts[^1].ToUpperInvariant(), parts.Count > 1 ? parts[^2] : null);
        return new ColumnRef(parts.Count > 1 ? parts[^2] : null, parts[^1]);
    }

    Expr ParseCase()
    {
        _p++;
        Expr? operand = IsKw("WHEN") ? null : ParseExpr();
        var whens = new List<(Expr, Expr)>();
        while (TryKw("WHEN"))
        {
            var w = ParseExpr();
            ExpectKw("THEN");
            whens.Add((w, ParseExpr()));
        }
        if (whens.Count == 0) throw Error();
        Expr? els = TryKw("ELSE") ? ParseExpr() : null;
        ExpectKw("END");
        return new CaseExpr(operand, whens, els);
    }

    Expr ParseFunctionCall(string name, string? schema)
    {
        ExpectSym("(");
        var args = new List<Expr>();
        var f = new FuncCall(name, args) { Schema = schema };
        bool done = false;
        if (schema == null && DatePartFunctions.Contains(name) && Cur.Kind == Tk.Ident)
        {
            args.Add(new Literal(_t[_p++].Text.ToLowerInvariant(), SqlType.VarChar(20)));
            if (!TrySym(",")) done = true;
        }
        if (!done)
        {
            if (TrySym("*")) f.Star = true;
            else if (!IsSym(")"))
            {
                if (TryKw("DISTINCT")) f.Distinct = true;
                else if (IsKw("ALL") && !IsSymAt(1, "(")) _p++;
                do { args.Add(ParseExpr()); } while (TrySym(","));
                if (name == "TRIM" && TryKw("FROM")) args.Add(ParseExpr());
            }
        }
        ExpectSym(")");
        if (IsKw("WITHIN") && IsKwAt(1, "GROUP"))
        {
            _p += 2;
            ExpectSym("(");
            ExpectKw("ORDER");
            ExpectKw("BY");
            f.WithinGroup = ParseOrderList();
            ExpectSym(")");
        }
        if (IsKw("OVER") && IsSymAt(1, "(")) f.Over = ParseOver();
        return f;
    }

    OverClause ParseOver()
    {
        _p += 2;
        var o = new OverClause();
        if (TryKw("PARTITION"))
        {
            ExpectKw("BY");
            do { o.Partition.Add(ParseExpr()); } while (TrySym(","));
        }
        if (TryKw("ORDER"))
        {
            ExpectKw("BY");
            o.Order = ParseOrderList();
        }
        if (IsKw("ROWS") || IsKw("RANGE"))
        {
            o.HasFrame = true;
            o.Rows = IsKw("ROWS");
            _p++;
            if (TryKw("BETWEEN"))
            {
                (o.StartKind, o.StartOffset) = ParseBound();
                ExpectKw("AND");
                (o.EndKind, o.EndOffset) = ParseBound();
            }
            else (o.StartKind, o.StartOffset) = ParseBound();
        }
        ExpectSym(")");
        return o;
    }

    (BoundKind, int) ParseBound()
    {
        if (TryKw("UNBOUNDED"))
        {
            if (TryKw("PRECEDING")) return (BoundKind.UnboundedPreceding, 0);
            ExpectKw("FOLLOWING");
            return (BoundKind.UnboundedFollowing, 0);
        }
        if (TryKw("CURRENT"))
        {
            ExpectKw("ROW");
            return (BoundKind.CurrentRow, 0);
        }
        int n = (int)SignedLong();
        if (TryKw("PRECEDING")) return (BoundKind.Preceding, n);
        ExpectKw("FOLLOWING");
        return (BoundKind.Following, n);
    }
}
