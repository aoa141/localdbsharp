namespace LocalDbSharp.Parsing;

// ============================================================ expressions

internal abstract class Expr { }

internal sealed class Literal(object? value, SqlType? type) : Expr
{
    public readonly object? Value = value;
    public readonly SqlType? Type = type;
}

internal sealed class ColumnRef(string? table, string name) : Expr
{
    public readonly string? Table = table;
    public readonly string Name = name;
    public override string ToString() => Table == null ? Name : Table + "." + Name;
}

internal sealed class VarRef(string name) : Expr
{
    public readonly string Name = name;
}

/// <summary>Placeholder for the DEFAULT keyword in VALUES lists and EXEC arguments.</summary>
internal sealed class DefaultExpr : Expr { }

internal sealed class Unary(string op, Expr e) : Expr
{
    public readonly string Op = op; // "-", "+", "~", "NOT"
    public readonly Expr E = e;
}

internal sealed class Binary(string op, Expr l, Expr r) : Expr
{
    public readonly string Op = op; // + - * / % & | ^ = <> < <= > >= AND OR
    public readonly Expr L = l, R = r;
}

internal sealed class IsNullExpr(Expr e, bool not) : Expr
{
    public readonly Expr E = e;
    public readonly bool Not = not;
}

internal sealed class BetweenExpr(Expr e, Expr lo, Expr hi, bool not) : Expr
{
    public readonly Expr E = e, Lo = lo, Hi = hi;
    public readonly bool Not = not;
}

internal sealed class InExpr(Expr e, List<Expr>? list, Query? sub, bool not) : Expr
{
    public readonly Expr E = e;
    public readonly List<Expr>? List = list;
    public readonly Query? Sub = sub;
    public readonly bool Not = not;
}

internal sealed class LikeExpr(Expr e, Expr pattern, Expr? escape, bool not) : Expr
{
    public readonly Expr E = e, Pattern = pattern;
    public readonly Expr? Escape = escape;
    public readonly bool Not = not;
}

internal sealed class ExistsExpr(Query q) : Expr
{
    public readonly Query Q = q;
}

internal sealed class SubqueryExpr(Query q) : Expr
{
    public readonly Query Q = q;
}

/// <summary>expr op ANY|ALL (subquery)</summary>
internal sealed class QuantifiedExpr(string op, Expr e, Query q, bool all) : Expr
{
    public readonly string Op = op;
    public readonly Expr E = e;
    public readonly Query Q = q;
    public readonly bool All = all;
}

internal sealed class CaseExpr(Expr? operand, List<(Expr When, Expr Then)> whens, Expr? @else) : Expr
{
    public readonly Expr? Operand = operand;
    public readonly List<(Expr When, Expr Then)> Whens = whens;
    public readonly Expr? Else = @else;
}

internal sealed class CastExpr(Expr e, SqlType type, bool isTry, Expr? style) : Expr
{
    public readonly Expr E = e;
    public readonly SqlType Type = type;
    public readonly bool Try = isTry;
    public readonly Expr? Style = style;
}

internal sealed class FuncCall(string name, List<Expr> args) : Expr
{
    public readonly string Name = name; // upper-case
    public string? Schema;
    public readonly List<Expr> Args = args;
    public bool Distinct, Star;
    public OverClause? Over;
    public List<OrderItem>? WithinGroup;
}

internal enum BoundKind { UnboundedPreceding, Preceding, CurrentRow, Following, UnboundedFollowing }

internal sealed class OverClause
{
    public List<Expr> Partition = [];
    public List<OrderItem> Order = [];
    public bool HasFrame, Rows;
    public BoundKind StartKind = BoundKind.UnboundedPreceding, EndKind = BoundKind.CurrentRow;
    public int StartOffset, EndOffset;
}

/// <summary>NEXT VALUE FOR sequence</summary>
internal sealed class NextValueExpr(TableName sequence) : Expr
{
    public readonly TableName Sequence = sequence;
}

internal sealed class OrderItem(Expr e, bool desc)
{
    public readonly Expr E = e;
    public readonly bool Desc = desc;
}

// ============================================================ queries

internal sealed class Cte
{
    public required string Name;
    public List<string>? Columns;
    public required Query Query;
}

internal sealed class Query
{
    public List<Cte>? Ctes;
    public required QueryExpr Body;
    public List<OrderItem>? OrderBy;
    public Expr? Offset, Fetch;
    public int? MaxRecursion;
    public ForJsonClause? ForJson;
}

internal sealed class ForJsonClause
{
    public bool Auto;
    public string? Root;
    public bool IncludeNulls, WithoutArrayWrapper;
}

internal abstract class QueryExpr { }

internal sealed class SelectItem
{
    public Expr? E;               // null when IsStar
    public string? Alias;
    public bool IsStar;
    public string? StarTable;
    public string? Variable;      // SELECT @v = expr
    public string VarOp = "=";
}

internal sealed class SelectSpec : QueryExpr
{
    public bool Distinct;
    public Expr? Top;
    public bool TopPercent, TopWithTies;
    public List<SelectItem> Items = [];
    public TableName? Into;
    public TableSource? From;
    public Expr? Where;
    public List<Expr> GroupBy = [];
    /// <summary>For ROLLUP / CUBE / GROUPING SETS: the sets to aggregate by (each a subset of GroupBy).</summary>
    public List<List<Expr>>? GroupingSets;
    public Expr? Having;

    // analysis caches (the AST is immutable after parsing, so these are stable)
    public bool? HasAggregates;
    public List<FuncCall>? Windows;
}

internal sealed class SetOp : QueryExpr
{
    public required string Op; // UNION, INTERSECT, EXCEPT
    public bool All;
    public required QueryExpr Left, Right;
}

/// <summary>A parenthesised query used as an operand of a set operation.</summary>
internal sealed class NestedQuery(Query q) : QueryExpr
{
    public readonly Query Q = q;
}

internal sealed class TableName
{
    public string? Database, Schema;
    public required string Name;
    public bool IsVariable => Name.StartsWith('@');
    public bool IsTemp => Name.StartsWith('#');
    public override string ToString() => (Database != null ? Database + "." : "") + (Schema != null ? Schema + "." : Database != null ? "." : "") + Name;
}

internal abstract class TableSource
{
    public string? Alias;
}

internal sealed class TableRef : TableSource
{
    public required TableName Name;
}

internal sealed class DerivedTable : TableSource
{
    public required Query Query;
    public List<string>? Columns;
}

internal sealed class ValuesTable : TableSource
{
    public required List<List<Expr>> Rows;
    public List<string>? Columns;
}

internal sealed class FunctionTable : TableSource
{
    public required TableName Name;
    public required List<Expr> Args;
    public List<string>? Columns;
    public List<OpenJsonColumn>? JsonColumns;
}

internal sealed class OpenJsonColumn
{
    public required string Name;
    public required SqlType Type;
    public string? Path;
    public bool AsJson;
}

internal sealed class PivotSource : TableSource
{
    public required TableSource Source;
    public required FuncCall Aggregate;
    public required string ForColumn;
    public required List<string> InValues;
}

internal sealed class UnpivotSource : TableSource
{
    public required TableSource Source;
    public required string ValueColumn, NameColumn;
    public required List<string> Columns;
}

internal enum JoinKind { Inner, Left, Right, Full, Cross, CrossApply, OuterApply }

internal sealed class JoinSource : TableSource
{
    public required TableSource Left, Right;
    public JoinKind Kind;
    public Expr? On;
}

// ============================================================ statements

internal sealed class Batch
{
    public readonly List<Statement> Statements = [];
    public TSqlException? Error;
}

internal abstract class Statement
{
    public int Line;
}

internal sealed class SelectStmt : Statement
{
    public required Query Q;
}

internal sealed class OutputClause
{
    public List<SelectItem> Items = [];
    public TableName? Into;
    public List<string>? IntoColumns;
}

internal sealed class InsertStmt : Statement
{
    public List<Cte>? Ctes;
    public required TableName Target;
    public List<string>? Columns;
    public List<List<Expr>>? Values;
    public Query? Select;
    public ExecStmt? Exec;
    public bool DefaultValues;
    public OutputClause? Output;
}

internal sealed class SetClause
{
    public string? Column, Variable;
    public string Op = "=";
    public required Expr Value;
}

internal sealed class UpdateStmt : Statement
{
    public List<Cte>? Ctes;
    public required TableName Target;
    public List<SetClause> Sets = [];
    public TableSource? From;
    public Expr? Where, Top;
    public OutputClause? Output;
}

internal sealed class DeleteStmt : Statement
{
    public List<Cte>? Ctes;
    public required TableName Target;
    public TableSource? From;
    public Expr? Where, Top;
    public OutputClause? Output;
}

internal enum MergeMatch { Matched, NotMatchedByTarget, NotMatchedBySource }
internal enum MergeAction { Update, Delete, Insert }

internal sealed class MergeClause
{
    public MergeMatch Match;
    public Expr? Condition;
    public MergeAction Action;
    public List<SetClause>? Sets;
    public List<string>? InsertColumns;
    public List<Expr>? InsertValues;
    public bool InsertDefaultValues;
}

internal sealed class MergeStmt : Statement
{
    public List<Cte>? Ctes;
    public required TableName Target;
    public string? TargetAlias;
    public required TableSource Source;
    public required Expr On;
    public List<MergeClause> Clauses = [];
    public OutputClause? Output;
}

internal sealed class TruncateStmt : Statement
{
    public required TableName Name;
}

internal sealed class ColumnDef
{
    public required string Name;
    public SqlType? Type;
    public TableName? UserType;      // alias type created with CREATE TYPE ... FROM
    public bool RowVersion;          // rowversion / timestamp: generated by the engine
    public bool WithValues;          // ALTER TABLE ADD ... DEFAULT x WITH VALUES
    public bool? Nullable;
    public Expr? Default;
    public string? DefaultName;
    public bool Identity;
    public long Seed = 1, Increment = 1;
    public Expr? Computed;
}

internal enum ConstraintKind { PrimaryKey, Unique, ForeignKey, Check, Default }

internal sealed class ConstraintDef
{
    public ConstraintKind Kind;
    public string? Name;
    public List<string> Columns = [];
    public TableName? RefTable;
    public List<string>? RefColumns;
    public bool CascadeDelete, SetNullOnDelete, CascadeUpdate;
    public bool? Clustered;     // explicit CLUSTERED / NONCLUSTERED on a key constraint
    public bool[]? Descending;  // per key column
    public Expr? Expr;          // CHECK expression or DEFAULT value
}

internal sealed class CreateTableStmt : Statement
{
    public required TableName Name;
    public List<ColumnDef> Columns = [];
    public List<ConstraintDef> Constraints = [];
}

internal sealed class AlterTableStmt : Statement
{
    public required TableName Name;
    public List<ColumnDef> AddColumns = [];
    public List<ConstraintDef> AddConstraints = [];
    public List<string> DropColumns = [];
    public List<string> DropConstraints = [];
    public ColumnDef? AlterColumn;
    public bool IfExists;
}

internal enum ObjectKind { Table, View, Procedure, Function, Index, Schema, Database, Type, Trigger, Sequence }

internal sealed class DropStmt : Statement
{
    public ObjectKind Kind;
    public List<TableName> Names = [];
    public bool IfExists;
    public TableName? OnTable;
}

internal sealed class CreateIndexStmt : Statement
{
    public required string Name;
    public required TableName Table;
    public List<string> Columns = [];
    public bool[]? Descending;
    public bool Unique, Clustered;
}

internal sealed class CreateViewStmt : Statement
{
    public required TableName Name;
    public List<string>? Columns;
    public required Query Query;
    public bool OrAlter;
}

internal sealed class ParamDef
{
    public required string Name;
    public required SqlType Type;
    public TableName? UserType;      // alias type or table type (table-valued parameter)
    public Expr? Default;
    public bool Output;
}

internal sealed class CreateProcStmt : Statement
{
    public required TableName Name;
    public List<ParamDef> Params = [];
    public List<Statement> Body = [];
    public bool OrAlter;
}

internal sealed class CreateFunctionStmt : Statement
{
    public required TableName Name;
    public List<ParamDef> Params = [];
    public SqlType? ReturnType;          // scalar
    public Query? InlineQuery;           // RETURNS TABLE AS RETURN (...)
    public string? TableVar;             // RETURNS @t TABLE (...)
    public CreateTableStmt? TableDef;
    public List<Statement>? Body;
    public bool OrAlter;
}

internal sealed class CreateTypeStmt : Statement
{
    public required TableName Name;
    public SqlType? BaseType;            // CREATE TYPE x FROM <type>
    public bool? Nullable;
    public CreateTableStmt? TableDef;    // CREATE TYPE x AS TABLE (...)
}

internal sealed class CreateSequenceStmt : Statement
{
    public required TableName Name;
    public SqlType? Type;
    public long? Start, Increment, Min, Max;
    public bool Cycle;
}

internal sealed class AlterSequenceStmt : Statement
{
    public required TableName Name;
    public bool Restart;
    public long? RestartWith, Increment;
}

internal sealed class CreateTriggerStmt : Statement
{
    public required TableName Name, Table;
    public bool InsteadOf, OnInsert, OnUpdate, OnDelete, OrAlter;
    public List<Statement> Body = [];
}

internal sealed class EnableTriggerStmt : Statement
{
    public string? Name;            // null = ALL
    public required TableName Table;
    public bool Enable;
}

internal sealed class DbccStmt : Statement
{
    public required string Command;
    public List<Expr> Args = [];
}

internal sealed class CreateSchemaStmt : Statement
{
    public required string Name;
}

internal sealed class CreateDatabaseStmt : Statement
{
    public required string Name;
}

internal sealed class UseStmt : Statement
{
    public required string Name;
}

internal sealed class DeclareItem
{
    public required string Name;
    public SqlType? Type;
    public TableName? UserType;
    public Expr? Init;
    public CreateTableStmt? Table;
}

internal sealed class DeclareStmt : Statement
{
    public List<DeclareItem> Items = [];
}

internal sealed class SetVarStmt : Statement
{
    public required string Name;
    public string Op = "=";
    public required Expr Value;
}

internal sealed class SetOptionStmt : Statement
{
    public required string Option;
    public string? Value;
    public TableName? Table;
}

internal sealed class IfStmt : Statement
{
    public required Expr Cond;
    public required Statement Then;
    public Statement? Else;
}

internal sealed class WhileStmt : Statement
{
    public required Expr Cond;
    public required Statement Body;
}

internal sealed class BlockStmt : Statement
{
    public List<Statement> Body = [];
}

internal sealed class TryCatchStmt : Statement
{
    public List<Statement> Try = [], Catch = [];
}

internal sealed class BreakStmt : Statement { }
internal sealed class ContinueStmt : Statement { }

internal sealed class ReturnStmt : Statement
{
    public Expr? Value;
}

internal sealed class PrintStmt : Statement
{
    public required Expr Value;
}

internal sealed class ThrowStmt : Statement
{
    public Expr? Number, Message, State;
}

internal sealed class RaiserrorStmt : Statement
{
    public List<Expr> Args = [];
}

internal sealed class ExecArg
{
    public string? Name;
    public required Expr Value;
    public bool Output;
}

internal sealed class ExecStmt : Statement
{
    public TableName? Proc;
    public string? ReturnVar;
    public List<ExecArg> Args = [];
    public Expr? DynamicSql;
}

internal enum TranKind { Begin, Commit, Rollback, Save }

internal sealed class TranStmt : Statement
{
    public TranKind Kind;
    public string? Name;
}

internal sealed class DeclareCursorStmt : Statement
{
    public required string Name;
    public required Query Query;
}

internal enum CursorOp { Open, Close, Deallocate }

internal sealed class CursorStmt : Statement
{
    public CursorOp Op;
    public required string Name;
}

internal sealed class FetchStmt : Statement
{
    public required string Name;
    public List<string> Into = [];
}
