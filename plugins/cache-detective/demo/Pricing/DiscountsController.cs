using System.Data;
using Dapper;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Caching.Memory;

namespace Pricing;

/// <summary>
/// The raw-SQL half of the demo: Dapper, an ADO.NET command, and a command declared to hold a procedure
/// name. Every action names the outcome the scanner should report for it, and the outcomes are of three
/// different kinds on purpose — a finding, an edge and an <c>unresolved</c> row are not the same thing.
/// </summary>
[ApiController]
[Route("discounts")]
public sealed class DiscountsController : ControllerBase
{
    private readonly IDbConnection _connection;
    private readonly IMemoryCache _memory;

    public DiscountsController(IDbConnection connection, IMemoryCache memory)
    {
        _connection = connection;
        _memory = memory;
    }

    /// <summary>
    /// Case A, the writing end. Calls a procedure that writes <c>dbo.Discounts</c>; the trigger on that
    /// table writes <c>dbo.PriceHistory</c>; the view <c>vw_ProductCard</c> reads it; and Catalog caches
    /// <c>product:{id}</c> over that view with no expiry. Nothing here invalidates anything.
    /// <para>Expected: one UNGUARDED_WRITE finding, confidence <c>confirmed</c>, subject <em>this</em>
    /// handler — the write is three hops away and performed by a trigger, but the handler at the head of
    /// the chain is the one that has to be fixed.</para>
    /// </summary>
    [HttpPost("apply")]
    public int ApplyDiscount(int productId, decimal percent) =>
        _connection.Execute("EXEC dbo.ApplyDiscount @ProductId, @Percent",
            new { ProductId = productId, Percent = percent });

    /// <summary>
    /// Case B. A concatenated <em>value</em> — the commonest shape of hand-written SQL there is.
    /// <para>Expected: a <c>reads</c> edge to <c>dbo.Prices</c>, confidence <c>confirmed</c>. The unknown
    /// fragment is substituted with a parameter and the grammar puts it in a value position, which cannot
    /// change which table the statement touches. No finding: reading is not writing.</para>
    /// </summary>
    [HttpGet("prices")]
    public IEnumerable<PriceRow> GetPrices(int productId) =>
        _connection.Query<PriceRow>("SELECT ProductId, Amount FROM dbo.Prices WHERE ProductId = "
                                     + productId);

    /// <summary>
    /// Case C. The unknown fragment lands where the table name goes.
    /// <para>Expected: an <c>unresolved</c> row of kind <c>sql</c> whose reason names the position — an
    /// unknown table name. Note that the parser does <em>not</em> fail here: <c>SELECT * FROM @p</c> is
    /// legal T-SQL, a table variable, so it is the parse tree and not an error that decides.</para>
    /// </summary>
    [HttpGet("search")]
    public IEnumerable<PriceRow> Search(string table) =>
        _connection.Query<PriceRow>($"SELECT * FROM {table}");

    /// <summary>
    /// Case E. A command declared to carry a procedure name, naming a procedure <c>db/shop.sql</c> does
    /// not create.
    /// <para>Expected: an <c>unresolved</c> row naming both the procedure and the database — the second
    /// of the two reasons a procedure vertex can be a dead end. Before <c>index_database</c> runs, the
    /// same call site reports the first reason instead, that no database is indexed.</para>
    /// </summary>
    [HttpPost("tax")]
    public int RecalculateTax(int productId)
    {
        using var command = new SqlCommand();
        command.Connection = (SqlConnection)_connection;
        command.CommandText = "dbo.RecalculateTax";
        command.CommandType = CommandType.StoredProcedure;
        command.Parameters.AddWithValue("@ProductId", productId);
        return command.ExecuteNonQuery();
    }

    /// <summary>
    /// Case F, the writing end. Calls a procedure that writes <c>dbo.Prices</c>, which
    /// <c>price:{id}</c> depends on, and invalidates that key itself.
    /// <para>Expected: no finding. The write is hidden inside the procedure, so this is what proves the
    /// invalidation is looked for around the handler at the head of the chain rather than around the
    /// procedure that performed the write.</para>
    /// </summary>
    [HttpPost("loyalty/{id:int}")]
    public int ApplyLoyaltyDiscount(int id)
    {
        var affected = _connection.Execute("EXEC dbo.ApplyLoyaltyDiscount @Id", new { Id = id });
        _memory.Remove($"price:{id}");
        return affected;
    }
}

public sealed class PriceRow
{
    public int ProductId { get; set; }

    public decimal Amount { get; set; }
}
