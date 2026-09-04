using System.Collections;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;

namespace CacheDetective.Tests.Database;

/// <summary>
/// A catalogue held in memory, reached through real ADO.NET shapes. The fake connection is the only way
/// the indexer can obtain a command, so <see cref="CommandTexts"/> is a complete record of the SQL it
/// issued — which is what lets a test check the read-only claim instead of taking it on trust.
/// </summary>
internal sealed class FakeCatalogue
{
    public List<(string Schema, string Name, string? Definition)> Procedures { get; } = [];

    public List<(string Schema, string Name)> Views { get; } = [];

    public List<(string Schema, string Name, string Table, string Event)> Triggers { get; } = [];

    /// <summary>Procedure-to-procedure calls, by qualified name.</summary>
    public List<(string From, string To)> ProcedureCalls { get; } = [];

    public Dictionary<string, List<(string Schema, string Name, bool Selected, bool Updated)>> References
    { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Objects for which the dynamic management function fails, as it does when a body names an
    /// object that no longer exists.</summary>
    public HashSet<string> Unresolvable { get; } = new(StringComparer.OrdinalIgnoreCase);

    public List<string> CommandTexts { get; } = [];

    /// <summary>Whether the login holds VIEW DEFINITION at database scope. False models the login that
    /// reads an empty <c>sys.sql_expression_dependencies</c> and loses every procedure-to-procedure
    /// call, which the indexer must report rather than pass over in silence.</summary>
    public bool CanSeeDependencies { get; set; } = true;

    public DbConnection Connect() => new FakeConnection(this);

    internal IReadOnlyList<object?[]> Read(string sql, string? objectName)
    {
        CommandTexts.Add(sql);

        if (sql.Contains("HAS_PERMS_BY_NAME", StringComparison.Ordinal))
        {
            return [[CanSeeDependencies ? 1 : 0]];
        }

        // Order matters: the dependency query names sys.procedures as well.
        if (sql.Contains("dm_sql_referenced_entities", StringComparison.Ordinal))
        {
            var name = objectName
                ?? throw new InvalidOperationException("The object name was not passed as a parameter.");
            if (Unresolvable.Contains(name))
            {
                throw new FakeDbException($"Invalid object name '{name}.Missing'.");
            }

            return References.TryGetValue(name, out var references)
                ? references.Select(reference => new object?[]
                {
                    reference.Schema, reference.Name, reference.Selected ? 1 : 0, reference.Updated ? 1 : 0
                }).ToArray()
                : [];
        }

        if (sql.Contains("sql_expression_dependencies", StringComparison.Ordinal))
        {
            return ProcedureCalls.Select(call => new object?[]
            {
                Schema(call.From), Name(call.From), Schema(call.To), Name(call.To)
            }).ToArray();
        }

        if (sql.Contains("sys.views", StringComparison.Ordinal))
        {
            return Views.Select(view => new object?[] { view.Schema, view.Name }).ToArray();
        }

        if (sql.Contains("sys.triggers", StringComparison.Ordinal))
        {
            return Triggers.Select(trigger => new object?[]
            {
                trigger.Schema, trigger.Name, trigger.Table, trigger.Event
            }).ToArray();
        }

        if (sql.Contains("sys.procedures", StringComparison.Ordinal))
        {
            return Procedures.Select(procedure => new object?[]
            {
                procedure.Schema, procedure.Name, procedure.Definition
            }).ToArray();
        }

        throw new InvalidOperationException($"The fake catalogue has no answer for: {sql}");
    }

    private static string Schema(string qualifiedName) => qualifiedName.Split('.')[0];

    private static string Name(string qualifiedName) => qualifiedName.Split('.')[1];
}

internal sealed class FakeDbException(string message) : DbException(message);

internal sealed class FakeConnection(FakeCatalogue catalogue) : DbConnection
{
    [AllowNull]
    public override string ConnectionString { get; set; } = "fake";

    public override string Database => "fake";

    public override string DataSource => "fake";

    public override string ServerVersion => "0";

    public override ConnectionState State => ConnectionState.Open;

    public override void ChangeDatabase(string databaseName) => throw new NotSupportedException();

    public override void Close()
    {
    }

    public override void Open()
    {
    }

    protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) =>
        throw new NotSupportedException();

    protected override DbCommand CreateDbCommand() => new FakeCommand(catalogue);
}

internal sealed class FakeCommand(FakeCatalogue catalogue) : DbCommand
{
    private readonly FakeParameterCollection _parameters = [];

    [AllowNull]
    public override string CommandText { get; set; } = string.Empty;

    public override int CommandTimeout { get; set; }

    public override CommandType CommandType { get; set; }

    public override bool DesignTimeVisible { get; set; }

    public override UpdateRowSource UpdatedRowSource { get; set; }

    protected override DbConnection? DbConnection { get; set; }

    protected override DbParameterCollection DbParameterCollection => _parameters;

    protected override DbTransaction? DbTransaction { get; set; }

    public override void Cancel()
    {
    }

    public override int ExecuteNonQuery() => throw new NotSupportedException();

    public override object ExecuteScalar() => throw new NotSupportedException();

    public override void Prepare()
    {
    }

    protected override DbParameter CreateDbParameter() => new FakeParameter();

    protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) =>
        new FakeReader(catalogue.Read(CommandText, _parameters.SingleValue));
}

internal sealed class FakeParameterCollection : DbParameterCollection, IEnumerable<DbParameter>
{
    private readonly List<DbParameter> _parameters = [];

    public override int Count => _parameters.Count;

    public override object SyncRoot => _parameters;

    public string? SingleValue => _parameters.SingleOrDefault()?.Value as string;

    public override int Add(object value)
    {
        _parameters.Add((DbParameter)value);
        return _parameters.Count - 1;
    }

    public override void AddRange(Array values) => throw new NotSupportedException();

    public override void Clear() => _parameters.Clear();

    public override bool Contains(object value) => _parameters.Contains(value);

    public override bool Contains(string value) => IndexOf(value) >= 0;

    public override void CopyTo(Array array, int index) => throw new NotSupportedException();

    public override IEnumerator GetEnumerator() => _parameters.GetEnumerator();

    public override int IndexOf(object value) => _parameters.IndexOf((DbParameter)value);

    public override int IndexOf(string parameterName) =>
        _parameters.FindIndex(parameter => parameter.ParameterName == parameterName);

    public override void Insert(int index, object value) => _parameters.Insert(index, (DbParameter)value);

    public override void Remove(object value) => _parameters.Remove((DbParameter)value);

    public override void RemoveAt(int index) => _parameters.RemoveAt(index);

    public override void RemoveAt(string parameterName) => RemoveAt(IndexOf(parameterName));

    protected override DbParameter GetParameter(int index) => _parameters[index];

    protected override DbParameter GetParameter(string parameterName) => _parameters[IndexOf(parameterName)];

    protected override void SetParameter(int index, DbParameter value) => _parameters[index] = value;

    protected override void SetParameter(string parameterName, DbParameter value) =>
        _parameters[IndexOf(parameterName)] = value;

    IEnumerator<DbParameter> IEnumerable<DbParameter>.GetEnumerator() => _parameters.GetEnumerator();
}

internal sealed class FakeParameter : DbParameter
{
    public override DbType DbType { get; set; }

    public override ParameterDirection Direction { get; set; }

    public override bool IsNullable { get; set; }

    [AllowNull]
    public override string ParameterName { get; set; } = string.Empty;

    public override int Size { get; set; }

    [AllowNull]
    public override string SourceColumn { get; set; } = string.Empty;

    public override bool SourceColumnNullMapping { get; set; }

    public override object? Value { get; set; }

    public override void ResetDbType()
    {
    }
}

internal sealed class FakeReader(IReadOnlyList<object?[]> rows) : DbDataReader
{
    private int _index = -1;

    public override int Depth => 0;

    public override int FieldCount => rows.Count == 0 ? 0 : rows[0].Length;

    public override bool HasRows => rows.Count > 0;

    public override bool IsClosed => false;

    public override int RecordsAffected => -1;

    public override object this[int ordinal] => GetValue(ordinal);

    public override object this[string name] => throw new NotSupportedException();

    public override bool Read() => ++_index < rows.Count;

    public override bool NextResult() => false;

    public override object GetValue(int ordinal) => rows[_index][ordinal] ?? DBNull.Value;

    public override bool IsDBNull(int ordinal) => rows[_index][ordinal] is null;

    public override string GetString(int ordinal) => (string)GetValue(ordinal);

    public override int GetInt32(int ordinal) => Convert.ToInt32(GetValue(ordinal));

    public override bool GetBoolean(int ordinal) => Convert.ToBoolean(GetValue(ordinal));

    public override byte GetByte(int ordinal) => throw new NotSupportedException();

    public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length) =>
        throw new NotSupportedException();

    public override char GetChar(int ordinal) => throw new NotSupportedException();

    public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length) =>
        throw new NotSupportedException();

    public override string GetDataTypeName(int ordinal) => throw new NotSupportedException();

    public override DateTime GetDateTime(int ordinal) => throw new NotSupportedException();

    public override decimal GetDecimal(int ordinal) => throw new NotSupportedException();

    public override double GetDouble(int ordinal) => throw new NotSupportedException();

    public override Type GetFieldType(int ordinal) => throw new NotSupportedException();

    public override float GetFloat(int ordinal) => throw new NotSupportedException();

    public override Guid GetGuid(int ordinal) => throw new NotSupportedException();

    public override short GetInt16(int ordinal) => throw new NotSupportedException();

    public override long GetInt64(int ordinal) => throw new NotSupportedException();

    public override string GetName(int ordinal) => throw new NotSupportedException();

    public override int GetOrdinal(string name) => throw new NotSupportedException();

    public override int GetValues(object[] values) => throw new NotSupportedException();

    public override IEnumerator GetEnumerator() => throw new NotSupportedException();
}
