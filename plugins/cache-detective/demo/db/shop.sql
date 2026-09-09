-- The database half of the demo stand.
--
-- Idempotent by construction: every object is created only if absent and every body is then replaced
-- with ALTER, so applying this script twice leaves the same catalogue and the same object_ids. The
-- integration test applies it twice for exactly that reason.
--
-- Each object carries the outcome the scanner should report for it.

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

-------------------------------------------------------------------------------
-- Tables
-------------------------------------------------------------------------------

IF OBJECT_ID(N'dbo.Products', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.Products
    (
        Id   INT           NOT NULL CONSTRAINT PK_Products PRIMARY KEY,
        Name NVARCHAR(200) NOT NULL
    );
END
GO

IF OBJECT_ID(N'dbo.Prices', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.Prices
    (
        ProductId INT            NOT NULL CONSTRAINT PK_Prices PRIMARY KEY,
        Amount    DECIMAL(18, 2) NOT NULL
    );
END
GO

IF OBJECT_ID(N'dbo.Discounts', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.Discounts
    (
        Id        INT            NOT NULL IDENTITY(1, 1) CONSTRAINT PK_Discounts PRIMARY KEY,
        ProductId INT            NOT NULL,
        -- Not named Percent: that is a reserved word, and bracketing it at every use reads worse.
        Rate      DECIMAL(9, 4)  NOT NULL,
        AppliedAt DATETIME2(3)   NOT NULL CONSTRAINT DF_Discounts_AppliedAt DEFAULT SYSUTCDATETIME()
    );
END
GO

IF OBJECT_ID(N'dbo.PriceHistory', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.PriceHistory
    (
        Id        INT            NOT NULL IDENTITY(1, 1) CONSTRAINT PK_PriceHistory PRIMARY KEY,
        ProductId INT            NOT NULL,
        Amount    DECIMAL(18, 2) NOT NULL,
        RecordedAt DATETIME2(3)  NOT NULL CONSTRAINT DF_PriceHistory_RecordedAt DEFAULT SYSUTCDATETIME()
    );
END
GO

IF OBJECT_ID(N'dbo.Inventory', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.Inventory
    (
        ProductId INT NOT NULL CONSTRAINT PK_Inventory PRIMARY KEY,
        OnHand    INT NOT NULL
    );
END
GO

-------------------------------------------------------------------------------
-- Case A: procedure writes Discounts, trigger writes PriceHistory, view reads it
-------------------------------------------------------------------------------

-- Expected: a writes edge to dbo.Discounts, and the head of the chain for case A's finding. The code
-- half reaches this through EXEC dbo.ApplyDiscount, so the two halves meet on this name.
IF OBJECT_ID(N'dbo.ApplyDiscount', N'P') IS NULL
    EXEC(N'CREATE PROCEDURE dbo.ApplyDiscount AS SET NOCOUNT ON;');
GO

ALTER PROCEDURE dbo.ApplyDiscount
    @ProductId INT,
    @Percent   DECIMAL(9, 4)
AS
BEGIN
    SET NOCOUNT ON;

    INSERT dbo.Discounts (ProductId, Rate)
    VALUES (@ProductId, @Percent);
END
GO

-- Expected: a fires edge from dbo.Discounts and a writes edge to dbo.PriceHistory, all three write
-- events, confirmed. Declared FOR INSERT, UPDATE, so an insert by ApplyDiscount reaches it.
IF OBJECT_ID(N'dbo.trg_Discounts_Audit', N'TR') IS NULL
    EXEC(N'CREATE TRIGGER dbo.trg_Discounts_Audit ON dbo.Discounts AFTER INSERT AS SET NOCOUNT ON;');
GO

ALTER TRIGGER dbo.trg_Discounts_Audit
ON dbo.Discounts
AFTER INSERT, UPDATE
AS
BEGIN
    SET NOCOUNT ON;

    INSERT dbo.PriceHistory (ProductId, Amount)
    SELECT i.ProductId, p.Amount
    FROM inserted AS i
    INNER JOIN dbo.Prices AS p ON p.ProductId = i.ProductId;
END
GO

-- Expected: reads edges to dbo.Products and dbo.PriceHistory. The code half maps an entity to this
-- name and cannot tell it is a view; the graph lets the view displace the table of the same name.
IF OBJECT_ID(N'dbo.vw_ProductCard', N'V') IS NULL
    EXEC(N'CREATE VIEW dbo.vw_ProductCard AS SELECT 1 AS Id, CAST(N'''' AS NVARCHAR(200)) AS Name, CAST(0 AS DECIMAL(18, 2)) AS LatestPrice;');
GO

ALTER VIEW dbo.vw_ProductCard
AS
SELECT p.Id,
       p.Name,
       (SELECT TOP (1) h.Amount
        FROM dbo.PriceHistory AS h
        WHERE h.ProductId = p.Id
        ORDER BY h.RecordedAt DESC) AS LatestPrice
FROM dbo.Products AS p;
GO

-------------------------------------------------------------------------------
-- Case D: dynamic SQL the catalogue cannot see through
-------------------------------------------------------------------------------

-- Expected: an unresolved row of kind sql from the database indexer, carrying a database object name
-- rather than a file and a line. sys.dm_sql_referenced_entities reports nothing for a string built at
-- run time, so the procedure must be recorded rather than reported as touching nothing.
IF OBJECT_ID(N'dbo.RebuildReport', N'P') IS NULL
    EXEC(N'CREATE PROCEDURE dbo.RebuildReport AS SET NOCOUNT ON;');
GO

ALTER PROCEDURE dbo.RebuildReport
    @TableName SYSNAME
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @statement NVARCHAR(MAX) =
        N'SELECT COUNT_BIG(*) FROM ' + QUOTENAME(@TableName) + N';';
    EXEC sys.sp_executesql @statement;
END
GO

-------------------------------------------------------------------------------
-- Case F: the negative case that has to be a hidden write
-------------------------------------------------------------------------------

-- Expected: a writes edge to dbo.Prices, and no finding — the Pricing handler that calls this
-- procedure invalidates price:{id} itself.
IF OBJECT_ID(N'dbo.ApplyLoyaltyDiscount', N'P') IS NULL
    EXEC(N'CREATE PROCEDURE dbo.ApplyLoyaltyDiscount AS SET NOCOUNT ON;');
GO

ALTER PROCEDURE dbo.ApplyLoyaltyDiscount
    @Id INT
AS
BEGIN
    SET NOCOUNT ON;

    UPDATE dbo.Prices
    SET Amount = Amount * 0.95
    WHERE ProductId = @Id;
END
GO

-------------------------------------------------------------------------------
-- Case E is the absence of an object: dbo.RecalculateTax is named by Pricing and never created here,
-- so the graph holds a procedure vertex with no outgoing edges and the query layer derives the second
-- of its two reasons — not in the catalogue of this database. Do not add it.
-------------------------------------------------------------------------------
