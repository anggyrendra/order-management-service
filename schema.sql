-- ============================================================================
-- Order Management API — Standalone Database Schema (SQLite)
-- ============================================================================
-- This script is the human-readable equivalent of the EF Core migration
-- (OrderManagement.Api/Infrastructure/Migrations/20260812084006_InitialCreate.cs).
-- It is provided so reviewers can inspect the schema without running EF.
--
-- Database choice: SQLite (see README.md "Database Choice" for the full
-- justification). SQLite is used for this prototype because it is a real
-- relational engine with transactions, UNIQUE constraints, CHECK constraints
-- and row-level locking within a transaction — all of which are essential to
-- validate the concurrency scenarios (A/B/C). It requires zero infrastructure
-- and runs identically in CI and locally. The code is provider-portable: the
-- only SQLite-specific note is that RowVersion is an APPLICATION-MANAGED
-- optimistic concurrency token (BLOB) rather than a database-generated
-- rowversion, because SQLite has no native auto-updating rowversion type. On
-- SQL Server or PostgreSQL the same column would become a native rowversion /
-- xmin and the application-managed interceptor would simply be redundant.
-- ============================================================================

PRAGMA foreign_keys = ON;

BEGIN TRANSACTION;

-- ----------------------------------------------------------------------------
-- Products
--   StockQuantity is protected by THREE layers (defence in depth):
--     1. Atomic conditional UPDATE in the application
--        (UPDATE ... SET StockQuantity = StockQuantity - @q
--         WHERE Id = @id AND StockQuantity >= @q)
--     2. EF Core optimistic concurrency token (RowVersion) so a stale write
--        is rejected.
--     3. The CHECK constraint below is the hard DB-level backstop: even if a
--        bug bypassed the application logic, the database would reject a
--        negative stock write.
--   RowVersion (BLOB, NOT NULL) is the application-managed optimistic
--   concurrency token. The ConcurrencyTokenInterceptor regenerates it on
--   every INSERT/UPDATE; EF Core includes
--     WHERE ... AND RowVersion = @original
--   in UPDATEs, so a concurrent modifier causes 0 affected rows
--   (DbUpdateConcurrencyException).
-- ----------------------------------------------------------------------------
CREATE TABLE "Products" (
    "Id"            TEXT          NOT NULL CONSTRAINT "PK_Products" PRIMARY KEY,
    "Name"          TEXT          NOT NULL,              -- max 200 chars (enforced in app)
    "StockQuantity" INTEGER       NOT NULL,
    "Price"         decimal(18,2) NOT NULL,
    "RowVersion"    BLOB          NOT NULL,              -- application-managed concurrency token
    "CreatedAt"     TEXT          NOT NULL,              -- ISO-8601 UTC
    "UpdatedAt"     TEXT          NOT NULL,
    CONSTRAINT "CK_Product_StockQuantity_NonNegative" CHECK ("StockQuantity" >= 0)
);

CREATE INDEX "IX_Products_Name" ON "Products" ("Name");

-- ----------------------------------------------------------------------------
-- Orders
--   Status is stored as INTEGER (enum backed by int):
--     0 = Pending, 1 = Confirmed, 2 = Shipped, 3 = Delivered, 4 = Cancelled
--   Valid transitions are enforced in the application via OrderStateMachine:
--     Pending    -> Confirmed | Cancelled
--     Confirmed  -> Shipped    | Cancelled
--     Shipped    -> Delivered
--     Delivered  -> (terminal)
--     Cancelled  -> (terminal)
--   RowVersion is the optimistic concurrency token that guards Skenario B
--   (concurrent status updates): the loser's UPDATE affects 0 rows.
-- ----------------------------------------------------------------------------
CREATE TABLE "Orders" (
    "Id"             TEXT          NOT NULL CONSTRAINT "PK_Orders" PRIMARY KEY,
    "CustomerId"     TEXT          NOT NULL,              -- max 100 chars
    "ShippingAddress" TEXT         NOT NULL,              -- max 500 chars
    "Status"         INTEGER       NOT NULL,              -- OrderStatus enum (int)
    "TotalAmount"    decimal(18,2) NOT NULL,
    "CreatedAt"      TEXT          NOT NULL,
    "UpdatedAt"      TEXT          NOT NULL,
    "RowVersion"     BLOB          NOT NULL               -- application-managed concurrency token
);

-- Single-column indexes support individual filters.
CREATE INDEX "IX_Orders_Status"     ON "Orders" ("Status");
CREATE INDEX "IX_Orders_CustomerId" ON "Orders" ("CustomerId");
CREATE INDEX "IX_Orders_CreatedAt"  ON "Orders" ("CreatedAt");
-- Composite index supports the combined list filter
-- (status, customerId, date range) in a single seek.
CREATE INDEX "IX_Orders_Status_CustomerId_CreatedAt"
    ON "Orders" ("Status", "CustomerId", "CreatedAt");

-- ----------------------------------------------------------------------------
-- OrderItems
--   UnitPrice is the price snapshot captured at order-creation time, so a
--   later product price change never retroactively alters an order's total.
--   OrderId CASCADE-delete: deleting an order removes its lines.
--   ProductId RESTRICT-delete: a product referenced by an order cannot be
--   removed (historical integrity).
-- ----------------------------------------------------------------------------
CREATE TABLE "OrderItems" (
    "Id"        TEXT          NOT NULL CONSTRAINT "PK_OrderItems" PRIMARY KEY,
    "OrderId"   TEXT          NOT NULL,
    "ProductId" TEXT          NOT NULL,
    "Quantity"  INTEGER       NOT NULL,                  -- >= 1 (enforced in app)
    "UnitPrice" decimal(18,2) NOT NULL,
    CONSTRAINT "FK_OrderItems_Orders_OrderId"
        FOREIGN KEY ("OrderId") REFERENCES "Orders" ("Id") ON DELETE CASCADE,
    CONSTRAINT "FK_OrderItems_Products_ProductId"
        FOREIGN KEY ("ProductId") REFERENCES "Products" ("Id") ON DELETE RESTRICT
);

CREATE INDEX "IX_OrderItems_OrderId"   ON "OrderItems" ("OrderId");
CREATE INDEX "IX_OrderItems_ProductId" ON "OrderItems" ("ProductId");

-- ----------------------------------------------------------------------------
-- IdempotencyRecords
--   The IdempotencyKey is the PRIMARY KEY, which gives the UNIQUE guarantee
--   required for Skenario C (concurrent identical POST /orders): only one
--   INSERT can win; the other gets a "UNIQUE constraint failed" error, which
--   the service catches and converts into a replay of the winner's result.
--   RequestHash (SHA-256 of the canonical request body) lets us detect and
--   reject a key reused with a DIFFERENT payload (409 Conflict).
--   Status: 0 = Pending, 1 = Completed, 2 = Failed.
-- ----------------------------------------------------------------------------
CREATE TABLE "IdempotencyRecords" (
    "IdempotencyKey"     TEXT     NOT NULL CONSTRAINT "PK_IdempotencyRecords" PRIMARY KEY,
    "RequestHash"        TEXT     NOT NULL,               -- SHA-256 hex, 64 chars
    "RequestPath"        TEXT     NOT NULL,               -- e.g. "POST /orders"
    "Status"             INTEGER  NOT NULL,               -- IdempotencyStatus enum (int)
    "ResponseStatusCode" INTEGER  NOT NULL,
    "ResponseBody"       TEXT     NULL,                   -- cached JSON response
    "OrderId"            TEXT     NULL,                   -- link to created order (if any)
    "CreatedAt"          TEXT     NOT NULL,
    "CompletedAt"        TEXT     NOT NULL,
    "RowVersion"         BLOB     NOT NULL                -- application-managed concurrency token
);

COMMIT;
