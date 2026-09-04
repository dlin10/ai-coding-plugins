using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using Dapper;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;

namespace Dapper
{
    public static class SqlMapper
    {
        public static IEnumerable<TEntity> Query<TEntity>(this IDbConnection connection, string sql) =>
            Array.Empty<TEntity>();

        public static int Execute(this IDbConnection connection, string sql) => 0;
    }
}

public sealed class PriceRow
{
    public int Id { get; set; }
}

public sealed class SqlAnalysisController : ControllerBase
{
    private readonly IDbConnection _connection = null!;
    private readonly IMemoryCache _memory = null!;

    public object ConcatenatedQuery(int id) =>
        _connection.Query<PriceRow>("SELECT * FROM dbo.Products WHERE Id = " + id);

    public object UnknownTableName(string table) =>
        _connection.Query<PriceRow>($"SELECT * FROM {table}");

    public object UnknownSchemaName(string schema) =>
        _connection.Query<PriceRow>($"SELECT * FROM {schema}.Products");

    public int BatchWithProcedure() =>
        _connection.Execute("UPDATE dbo.Prices SET Amount = Amount * 2; EXEC dbo.ApplyDiscount;");

    public void DeclaredProcedureCommand()
    {
        DbCommand command = null!;
        command.CommandText = "dbo.ApplyDiscount";
        command.CommandType = CommandType.StoredProcedure;
    }

    public void CachesThroughProcedure()
    {
        _memory.Set("pricing:hot", 1, new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(30)
        });
        _connection.Execute("EXEC dbo.RefreshPrices");
    }
}
