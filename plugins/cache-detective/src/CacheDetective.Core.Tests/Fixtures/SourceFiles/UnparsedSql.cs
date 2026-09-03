using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Linq;
using Dapper;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace Dapper
{
    public static class SqlMapper
    {
        public static IEnumerable<TEntity> Query<TEntity>(this IDbConnection connection, string sql) =>
            Array.Empty<TEntity>();

        public static int Execute(this IDbConnection connection, string sql) => 0;
    }
}

namespace Microsoft.Data.SqlClient
{
    public sealed class SqlCommand
    {
        public SqlCommand(string commandText) { }
    }

    public sealed class SqlDataAdapter
    {
    }
}

namespace Microsoft.EntityFrameworkCore
{
    public static class FixtureSqlExtensions
    {
        public static IQueryable<TEntity> FromSqlRaw<TEntity>(this DbSet<TEntity> source, string sql)
            where TEntity : class => source;

        public static int ExecuteSqlRaw(this DatabaseFacade database, string sql) => 0;
    }
}

public sealed class SqlEntity
{
    public int Id { get; set; }
}

public sealed class SqlFixtureDbContext : DbContext
{
    public DbSet<SqlEntity> Rows { get; set; } = null!;
}

public sealed class UnparsedSqlController : ControllerBase
{
    private readonly IDbConnection _connection = null!;
    private readonly SqlFixtureDbContext _database = null!;

    public object DapperQuery() => _connection.Query<SqlEntity>("select 1");

    public int DapperExecute() => _connection.Execute("update rows");

    public object SqlCommandSite() => new SqlCommand("select 1");

    public object SqlDataAdapterSite() => new SqlDataAdapter();

    public object EfFromSqlRaw() => _database.Rows.FromSqlRaw("select * from rows").ToList();

    public int EfExecuteSqlRaw() => _database.Database.ExecuteSqlRaw("delete from rows");

    public void StoredProcedureSite()
    {
        DbCommand command = null!;
        command.CommandType = CommandType.StoredProcedure;
    }
}
