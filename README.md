# Order Management API

A concurrency-first REST API for Order Management built with **ASP.NET Core 8** and **Entity Framework Core 8**, designed to solve the four real-world problems described in the test brief: **double orders**, **stock going negative under concurrent requests**, **inconsistent order status updates**, and **insufficient logging**.

The central concern of this prototype is **correctness under concurrency**. Every mutating endpoint is built so that two simultaneous requests cannot corrupt stock, create duplicate orders, or leave an order in an impossible state — and the behaviour is verified by real concurrency tests against a real relational database (SQLite), not mocks.

---

## Table of Contents
1. [Tech Stack & Project Structure](#tech-stack--project-structure)
2. [How to Run](#how-to-run)
3. [How to Test](#how-to-test)
4. [API Reference](#api-reference)
5. [Idempotency Strategy (Skenario C + double orders)](#1-idempotency-strategy-skenario-c--double-orders)
6. [Concurrency Handling (FOCUS utama)](#2-concurrency-handling-focus-utama)
7. [Additional Race Conditions Identified & Prevented](#3-additional-race-conditions-identified--prevented)
8. [Consistent Validation & Error Handling](#4-consistent-validation--error-handling)
9. [Logging with Correlation ID](#5-logging-with-correlation-id)
10. [Database Choice & Schema](#6-database-choice--schema)
11. [Testing Strategy](#7-testing-strategy)

---

## Tech Stack & Project Structure

| Concern | Choice |
|---|---|
| Runtime / Framework | .NET 8 / ASP.NET Core 8 Web API (controllers) |
| ORM | Entity Framework Core 8.0.10 |
| Database | SQLite (file-based, see [Database Choice](#6-database-choice--schema)) |
| Logging | Serilog (console + rolling file) with Correlation ID enrichment |
| API Docs | Swashbuckle / Swagger |
| Testing | xUnit + real SQLite (file-backed, isolated per test) |

```
OrderManagement/
├── OrderManagement.sln
├── schema.sql                      # Standalone human-readable schema (mirrors the EF migration)
├── .gitignore
├── OrderManagement.Api/
│   ├── Program.cs                  # Bootstrap, DI, pipeline, DB ensure-created + seeding
│   ├── appsettings.json            # Connection string: Data Source=ordermanagement.db
│   ├── Domain/
│   │   ├── Entities/               # Order, OrderItem, Product, IdempotencyRecord, IConcurrencyToken
│   │   ├── Enums/                  # OrderStatus
│   │   ├── Exceptions/             # DomainException hierarchy (→ HTTP status mapping)
│   │   └── OrderStateMachine.cs    # Valid transition rules
│   ├── Application/
│   │   ├── DTOs/                   # Request/response contracts
│   │   ├── Interfaces/             # IOrderService, IProductService
│   │   └── Services/               # OrderService (concurrency-aware), ProductService
│   ├── Infrastructure/
│   │   ├── Data/                   # AppDbContext, ConcurrencyTokenInterceptor, design-time factory
│   │   ├── Idempotency/            # RequestHasher (SHA-256 canonical body hash)
│   │   └── Migrations/             # EF Core migration + model snapshot
│   └── Api/
│       ├── Controllers/            # OrdersController, ProductsController
│       ├── Middleware/             # CorrelationIdMiddleware, GlobalExceptionMiddleware
│       └── Extensions/             # ServiceCollectionExtensions (DI, logging, pipeline)
└── OrderManagement.Tests/
    ├── Helpers/                    # TestHostFactory (isolated SQLite per test), scope extensions
    ├── Unit/                       # OrderStateMachineTests, OrderServiceTests
    └── Concurrency/                # ScenarioA (stock), ScenarioB (status), ScenarioC (idempotency)
```

---

## How to Run

### Prerequisites

- .NET 8 SDK
- (No SQL Server/PostgreSQL needed — SQLite is file-based and included via the EF Core SQLite provider.)

### Steps

```bash
# 1. Restore & build
cd OrderManagement
dotnet restore
dotnet build -c Release

# 2. Run the API (creates ordermanagement.db, applies schema, seeds products)
dotnet run --project OrderManagement.Api -c Release
#  → API listens on http://localhost:5000 (and https://localhost:5001 in dev)
#  → Swagger UI: http://localhost:5000/swagger
```

On startup `Program.cs` calls `Database.EnsureCreated()` (which runs the EF migration) and seeds three products so the API is immediately usable:

| Product | Id (fixed GUID) | Stock | Price |
|---|---|---|---|
| Product X | `00000000-0000-0000-0000-000000000001` | 15 | $100 |
| Product Y | `00000000-0000-0000-0000-000000000002` | 50 | $25 |
| Product Z | `00000000-0000-0000-0000-000000000003` | 10 | $500 |

### Quick smoke test

```bash
# Create an order (note the required Idempotency-Key header)
curl -X POST http://localhost:5000/api/orders \
  -H "Content-Type: application/json" \
  -H "Idempotency-Key: $(uuidgen)" \
  -d '{"customerId":"CUST-1","items":[{"productId":"00000000-0000-0000-0000-000000000001","quantity":2}],"shippingAddress":"1 Main St"}'

# Re-send with the SAME key + SAME body → returns the SAME order (200 OK, no duplicate)
curl -X POST http://localhost:5000/api/orders \
  -H "Content-Type: application/json" \
  -H "Idempotency-Key: <same-uuid-as-above>" \
  -d '{"customerId":"CUST-1","items":[{"productId":"00000000-0000-0000-0000-000000000001","quantity":2}],"shippingAddress":"1 Main St"}'
```

---

## How to Test

```bash
cd OrderManagement
dotnet test -c Release
```

The suite contains **35 tests** across three categories:

| Category | File | What it verifies |
|---|---|---|
| Unit — State machine | `Unit/OrderStateMachineTests.cs` | Every valid and invalid transition; terminal-state rules. |
| Unit — Service logic | `Unit/OrderServiceTests.cs` | Stock deduction, insufficient-stock rejection, unknown product, empty-items validation, happy-path status flow, cancel restores stock, cancel from Shipped rejected, list filters + pagination. |
| **Concurrency — Skenario A** | `Concurrency/ScenarioA_ConcurrentStockDeduction.cs` | 2 and 20 concurrent orders competing for limited stock; stock **never** goes negative; at most the available quantity is sold. |
| **Concurrency — Skenario B** | `Concurrency/ScenarioB_ConcurrentStatusUpdate.cs` | 2 and 25 concurrent identical status updates — exactly one wins, the rest get 409; conflicting Confirm vs Cancel race ends in a single consistent state. |
| **Concurrency — Skenario C** | `Concurrency/ScenarioC_IdempotentCreateUnderRace.cs` | 2 and 30 concurrent duplicate creates with the same key — exactly one order is created, all callers receive the same order id, stock is deducted exactly once; same key + different payload → 409. |

Every concurrency test runs against a **real, isolated SQLite database** (unique file per test) with real transactions and real UNIQUE/CHECK constraints — not an in-memory mock — because the whole point is to validate behaviour that only a real relational engine can enforce.

---

## API Reference

Base URL: `/api/orders`

### `POST /api/orders` — Create Order (idempotent)

| Header | Required | Description |
|---|---|---|
| `Idempotency-Key` | **Yes** | Client-generated unique key. Reuse with the same body to safely retry without creating a duplicate. |

**Request body**
```json
{
  "customerId": "CUST-1",
  "items": [
    { "productId": "00000000-0000-0000-0000-000000000001", "quantity": 2 }
  ],
  "shippingAddress": "1 Main St"
}
```

**Responses**
- `201 Created` — order created (first time with this key).
- `200 OK` — idempotent replay: same key + same body returns the **same** order, no duplicate created.
- `409 Conflict` — insufficient stock (`INSUFFICIENT_STOCK`), or idempotency key reused with a different body (`IDEMPOTENCY_CONFLICT`).
- `422 Unprocessable Entity` — validation error (empty items, quantity < 1, missing customerId).
- `404 Not Found` — a referenced product does not exist.

### `GET /api/orders/{id}` — Get Order by ID
- `200 OK` with the order + line items.
- `404 Not Found` — unknown id.

### `GET /api/orders` — List Orders (filter + paginate)

Query parameters: `status`, `customerId`, `fromDate`, `toDate`, `page` (default 1), `pageSize` (default 20, max 100).

- `200 OK` with a `PagedResult<OrderResponse>` (`items`, `page`, `pageSize`, `totalCount`, `totalPages`, `hasNext`, `hasPrevious`).

### `PATCH /api/orders/{id}/status` — Update Order Status

**Request body**
```json
{ "status": "Confirmed" }
```
- `200 OK` — status updated.
- `404 Not Found` — unknown order.
- `409 Conflict` — invalid transition (`INVALID_STATUS_TRANSITION`) or concurrent modification (`CONCURRENCY_CONFLICT`).
- `422 Unprocessable Entity` — invalid status enum.

### `POST /api/orders/{id}/cancel` — Cancel Order
- `200 OK` — cancelled, stock restored.
- `404 Not Found` — unknown order.
- `409 Conflict` — not cancellable from current state, or concurrent modification.

### `GET /api/products` / `GET /api/products/{id}` — List/Get Products

---

## 1. Idempotency Strategy (Skenario C + double orders)

### The problem

A client double-clicks "Place Order", or a flaky network causes an automatic retry. Without protection, **two identical POSTs create two orders and deduct stock twice** — a double-charge and oversold inventory.

### The strategy: client-generated key + insert-first + unique constraint

1. **The client sends an `Idempotency-Key` header** (a UUID or any opaque unique string) with every `POST /orders`. The key is the client's claim that "this is attempt #N of logical request R"; retries reuse the same key.

2. **Before any business work, the service INSERTs an `IdempotencyRecord` row** whose primary key *is* the idempotency key. Because `IdempotencyKey` is the `PRIMARY KEY`, the database enforces a `UNIQUE` constraint: **of two concurrent inserts with the same key, exactly one succeeds**; the other gets a `UNIQUE constraint failed` error.

3. **The winner proceeds** to deduct stock and create the order, then marks its `IdempotencyRecord` as `Completed` (caching the response body and the resulting order id).

4. **The loser catches the unique-constraint violation** and **replays** the winner's stored result: it reads the `IdempotencyRecord`, waits (polls) if the winner is still `Pending`, and returns the **same order** with `200 OK` (not `201 Created`). No second order is ever created.

5. **Payload-integrity guard:** the record stores a `RequestHash` (SHA-256 of the canonical JSON request body). If the same key is ever reused with a *different* body, the service rejects it with `409 Conflict` — an idempotency key must always be paired with the same payload. This prevents a subtle bug where a key is reused for a genuinely different order.

### Why this design (justification)

- **Insert-first, not check-then-insert.** A "SELECT then INSERT if absent" pattern has a TOCTOU race: two concurrent SELECTs both see "absent" and both INSERT. Making the key the primary key turns the database itself into the arbiter — the UNIQUE constraint is atomic and cannot be raced.
- **The key is client-generated**, so retries (even after a client crash/restart) can reuse it without server-side session state. The server is stateless apart from the idempotency log table.
- **Short, separate transaction for the claim.** The claim INSERT is committed in its own short transaction so the loser can immediately observe "someone owns this key" rather than blocking until the (potentially slow) order/stock transaction finishes. The loser then polls for the winner's completion.
- **Fresh DbContext per idempotency operation.** The claim, completion-marking, and replay all use independent contexts from `IDbContextFactory<AppDbContext>` so that a failed insert never pollutes the context used for the order, and concurrent callers never share tracked entities (EF Core's `DbContext` is **not** thread-safe).
- **Why not a distributed cache / Redis?** This prototype stays within a single database for simplicity, but the pattern is portable: the `IdempotencyRecord` table could be replaced/augmented by Redis with `SETNX` for the claim, keeping the same insert-first semantics. The database table has the advantage of being transactional with the order itself.

### Retention

In production the `IdempotencyRecords` table should be purged by a scheduled job (e.g. rows older than 24–48h). Keys need only live long enough to cover the client's retry window.

---

## 2. Concurrency Handling (FOCUS utama)

This is the heart of the prototype. Three scenarios are explicitly required and each is guarded by a deliberate, layered mechanism.

### Skenario A — Concurrent stock deduction (stock must never go negative)

**Scenario:** Two orders for 10 units each arrive simultaneously when only 15 units are in stock. A naive read-check-write (`if stock >= qty then stock -= qty`) lets both read 15, both pass the check, and both deduct → stock becomes -5.

**Guard — atomic conditional UPDATE (single statement):**

```sql
UPDATE Products
SET StockQuantity = StockQuantity - @qty,
    UpdatedAt = @now
WHERE Id = @id AND StockQuantity >= @qty
```

Because the predicate (`StockQuantity >= @qty`) and the mutation (`StockQuantity = StockQuantity - @qty`) happen in **one atomic statement**, the database serialises the two updates. The first commit changes stock from 15 → 5. The second update's `WHERE StockQuantity >= 10` no longer matches (5 < 10), so it affects **0 rows**. The service detects `affected == 0` and rejects the second order with `409 INSUFFICIENT_STOCK`. Stock can therefore **never go negative**, even with thousands of concurrent requests.

**Defence in depth — three independent layers:**

1. **Atomic conditional UPDATE** (above) — the primary guard.
2. **EF Core optimistic concurrency token (`RowVersion`)** — every `Product` has a `RowVersion` (application-managed `byte[]`; see [Database Choice](#6-database-choice--schema)). EF Core includes `WHERE ... AND RowVersion = @original` in updates, so a stale write affects 0 rows and throws `DbUpdateConcurrencyException`.
3. **Database CHECK constraint `CK_Product_StockQuantity_NonNegative` (`StockQuantity >= 0`)** — the hard backstop. Even if a bug bypassed both application layers, the database would reject a negative-stock write.

See `OrderService.ExecuteCreateOrderAsync` for the implementation and `Concurrency/ScenarioA_ConcurrentStockDeduction.cs` for the proof.

### Skenario B — Concurrent status update (inconsistent order status)

**Scenario:** Two operators (or a user + an automated retry) try to move the *same* order to a new status at the same instant. Without protection, a last-writer-wins UPDATE can produce an impossible transition (e.g. Delivered → Shipped) or silently lose one operator's intent.

**Guard — optimistic concurrency via `Order.RowVersion`:**

Each `UpdateStatusAsync` call:
1. Creates a **fresh `DbContext`** from `IDbContextFactory` (so concurrent calls hold independent `RowVersion` snapshots — `DbContext` is not thread-safe).
2. Reads the order **with tracking** (EF records the current `RowVersion`).
3. Validates the transition via `OrderStateMachine.EnsureCanTransition`.
4. Sets the new status and calls `SaveChanges`. EF emits `UPDATE Orders SET Status=@s, RowVersion=@new WHERE Id=@id AND RowVersion=@original`.

The first commit wins and rotates `RowVersion`. The second's `WHERE RowVersion = @original` no longer matches → **0 rows** → `DbUpdateConcurrencyException` → caught and surfaced as `409 CONCURRENCY_CONFLICT` ("the resource was modified by another request; please reload and retry").

An **execution strategy** (`CreateExecutionStrategy`) wraps the read-modify-write in a retry-capable block for transient errors, and an **explicit transaction** keeps the read and write consistent.

See `OrderService.UpdateStatusAsync` and `Concurrency/ScenarioB_ConcurrentStatusUpdate.cs`.

### Skenario C — Idempotent create under race (double orders)

**Scenario:** Two identical `POST /orders` with the same `Idempotency-Key` arrive within the same millisecond. Both must result in **exactly one order** and both callers must receive the **same order id**.

**Guard — unique-constraint claim + replay** (detailed in [Idempotency Strategy](#1-idempotency-strategy-skenario-c--double-orders)). The loser of the INSERT race polls the winner's `IdempotencyRecord` until it is `Completed`, then reconstructs and returns the same `OrderResponse`. Stock is deducted exactly once.

A subtle implementation point: after a unique-constraint violation, the winner's just-committed row may not be *immediately* visible on the loser's separate connection. `ReplayIdempotentResultAsync` therefore reads the record with a **short retry loop** (`ReadIdempotencyRecordWithRetryAsync`) using a fresh context per attempt, closing the visibility gap without busy-waiting.

See `OrderService.CreateOrderAsync` / `ReplayIdempotentResultAsync` and `Concurrency/ScenarioC_IdempotentCreateUnderRace.cs`.

---

## 3. Additional Race Conditions Identified & Prevented

The brief asks for **two or more** additional race conditions beyond A/B/C. The following are identified and guarded:

### Race #1 — Concurrent cancel vs. confirm (or any two valid transitions from the same state)

**The race:** An order is `Pending`. One request calls `PATCH /status` → `Confirmed`; another calls `POST /cancel`. Both `Pending → Confirmed` and `Pending → Cancelled` are *individually valid* transitions. Without coordination, both could succeed and the order could end in a contradictory state, or stock could be restored for an order that was also confirmed.

**Prevention:** Both `UpdateStatusAsync` and `CancelOrderAsync` use the **same optimistic-lock mechanism** (`Order.RowVersion`) on independent contexts. Whichever commits first rotates `RowVersion`; the other's `SaveChanges` affects 0 rows → `DbUpdateConcurrencyException` → `409`. Because `CancelOrderAsync` re-reads the order with tracking *inside its transaction*, if confirm won first, the cancel call now sees `Confirmed` (still a valid cancel source) and either (a) wins the optimistic lock and cancels, restoring stock, or (b) loses the lock and is rejected. In every case the order ends in a **single, reachable, consistent state** and **stock always matches the final status**. This invariant is asserted by `ScenarioB.Conflicting_transitions_confirm_vs_cancel_only_one_applies`.

### Race #2 — Stock restore on cancel interleaving with a concurrent new order

**The race:** Order O is being cancelled (stock +qty restore in flight) at the same moment a new order O' is deducting the same product. A naive "read stock, add back, write" for the restore could clobber O's deduction or produce an incorrect total.

**Prevention:** Both the deduction (Skenario A) and the restore use **single atomic UPDATEs** against the same row. SQLite serialises row-level writes within a transaction, so the two statements apply atomically and never lose an update: `UPDATE Products SET StockQuantity = StockQuantity + @qty WHERE Id = @id` (restore) and `UPDATE Products SET StockQuantity = StockQuantity - @qty WHERE Id = @id AND StockQuantity >= @qty` (deduct) cannot interleave corruptly. The cancel's restore runs inside the same explicit transaction as the status change, so the order is only marked `Cancelled` *and* stock restored atomically — no partial state.

### Race #3 — Duplicate idempotency claim under lost-update (the "claim-then-crash" window)

**The race:** A request claims an idempotency key (INSERT) but the process crashes *before* marking the record `Completed` or `Failed`. The row is stuck `Pending` forever; retries with the same key would wait indefinitely.

**Prevention:** `WaitForCompletionAsync` polls with a bounded number of attempts and exponential backoff; if the original never completes, it throws `IdempotencyConflictException` ("did not complete in time, please retry") rather than hanging. A production hardening would add a `ClaimedAt`/lease timestamp and a sweeper that transitions stale `Pending` rows to `Failed` so the key becomes reusable; the current design fails safe (rejects) rather than failing silent (creating a duplicate).

### Race #4 — Read-skew between stock check and price snapshot

**The race:** While checking stock, the product's price changes. An order could be created with a unit price from one point in time and a stock deduction from another.

**Prevention:** `ExecuteCreateOrderAsync` captures the **price snapshot** (`AsNoTracking` read) and performs the **stock deduction** in immediate succession within the same request; the order line stores `UnitPrice` at creation time, so later price changes never retroactively alter an order's total. Stock and price are read from the same consistent snapshot, and the order's `TotalAmount` is computed from the captured `UnitPrice`, not a re-read.

---

## 4. Consistent Validation & Error Handling

All errors flow through **`GlobalExceptionMiddleware`**, which maps every exception to a uniform JSON envelope and the correct HTTP status. Controllers stay thin — they let domain exceptions propagate.

**Error envelope**
```json
{
  "correlationId": "9f3c...e1",
  "errorCode": "INSUFFICIENT_STOCK",
  "message": "Insufficient stock for product '...': requested 20, available 15.",
  "details": { "productId": "...", "requested": 20, "available": 15 },
  "timestamp": "2024-08-12T08:40:05Z"
}
```

**Status code mapping**

| HTTP | `errorCode` | When |
|---|---|---|
| 400 | `VALIDATION_ERROR` | Missing `Idempotency-Key` header on `POST /orders`. |
| 404 | `NOT_FOUND` | Order or product not found. |
| 409 | `INSUFFICIENT_STOCK` | An order line requests more than available stock. |
| 409 | `INVALID_STATUS_TRANSITION` | Disallowed state-machine transition, or cancel from a non-cancellable state. |
| 409 | `CONCURRENCY_CONFLICT` | Optimistic lock lost (`DbUpdateConcurrencyException`). |
| 409 | `IDEMPOTENCY_CONFLICT` | Idempotency key reused with a different body, or in an inconsistent state. |
| 409 | `DUPLICATE_REQUEST` | Raw unique-constraint violation surfaced at the middleware boundary. |
| 422 | `VALIDATION_ERROR` | Domain validation failure (empty items, quantity < 1, invalid status enum). `details` holds a field→errors map. |
| 499 | `REQUEST_CANCELLED` | Client disconnected. |
| 500 | `INTERNAL_ERROR` | Any unhandled exception (logged with the correlation id for support). |

**Why 409 for insufficient stock (not 422)?** The request is structurally valid; it conflicts with the *current state* of stock. 422 is reserved for requests that are malformed regardless of state. This follows the HTTP semantics where 409 means "conflict with the current state of the resource."

**Input validation** uses both data annotations (`[Required]`, `[Range]`, `[MinLength]`, `[EnumDataType]`) for structural checks (handled by `[ApiController]` automatic 400 responses) and explicit domain validation in `OrderService.ValidateCreateRequest` for business rules (e.g. duplicate product ids in one request are grouped and summed, not rejected).

---

## 5. Logging with Correlation ID

Logging uses **Serilog** (not `Console.WriteLine`) with **console + rolling daily file** sinks (`logs/orders-YYYYMMDD.log`). Every log event is enriched with a `CorrelationId`.

**Flow:**
1. `CorrelationIdMiddleware` runs first in the pipeline. It reads `X-Correlation-Id` from the request (or generates a new GUID), stores it in `HttpContext.Items`, echoes it in the `X-Correlation-Id` response header, and pushes it onto the Serilog `LogContext` so every subsequent log line in the request is tagged.
2. All service and controller logs (`ILogger<T>`) automatically include the correlation id via the `LogContext` enricher.
3. `GlobalExceptionMiddleware` includes the correlation id in the error envelope, so a user can quote it to support and the exact request trail can be found in the logs.

**Example log line**
```
[08:40:05 INF 9f3c1a2b...] Claimed idempotency key abc-123 for a new order.
[08:40:05 INF 9f3c1a2b...] Created order 7e... for customer CUST-1 total 550.
[08:40:06 WRN 9f3c1a2b...] Concurrent status update conflict on order 7e.... Another update won; this request (to Shipped) was rejected.
```

Log levels: client errors (4xx) → `Warning`; server errors (5xx) → `Error`; normal flow → `Information`.

---

## 6. Database Choice & Schema

### Choice: SQLite (with justification)

| Criterion | SQLite | Why it matters here |
|---|---|---|
| Real relational engine | ✅ | Concurrency correctness can only be validated against real transactions, UNIQUE constraints, and CHECK constraints — not an in-memory list. |
| Zero infrastructure | ✅ | No server to install/configure; the test suite and reviewer can run it instantly. Runs identically in CI and locally. |
| File-based, isolated per test | ✅ | Each test gets a unique `.db` file, so tests are fully isolated and parallel-safe. |
| ACID transactions | ✅ | Required for the atomic stock UPDATE and the transactional cancel. |
| UNIQUE / CHECK constraints | ✅ | UNIQUE on `IdempotencyKey` (Skenario C) and CHECK on `StockQuantity >= 0` (stock backstop). |

**The one SQLite-specific note — application-managed concurrency token:**

SQLite has **no native auto-updating `rowversion` type** (unlike SQL Server's `rowversion`/`timestamp` or PostgreSQL's `xmin`). Per the [EF Core docs](https://learn.microsoft.com/ef/core/modeling/concurrency), on databases without a native rowversion we must manage the token in application code. The solution:

- `Product`, `Order`, and `IdempotencyRecord` implement `IConcurrencyToken { byte[] RowVersion; }`.
- In `AppDbContext.OnModelCreating`, the property is configured with **`.IsConcurrencyToken()`** (not `.IsRowVersion()`). `IsRowVersion()` tells EF the value is *database-generated*, which on SQLite produces a NOT NULL column that is never populated → "NOT NULL constraint failed" on insert. `IsConcurrencyToken()` tells EF the value is *client-managed* and must be included in INSERTs.
- `ConcurrencyTokenInterceptor` (a `SaveChangesInterceptor`) assigns a fresh `Guid.NewGuid().ToByteArray()` to every Added/Modified `IConcurrencyToken` entity on each save. EF Core then emits `UPDATE ... WHERE ... AND RowVersion = @original`, and a concurrent modifier causes 0 affected rows → `DbUpdateConcurrencyException`.
- The interceptor is registered both in the DI options (`AddDbContext`/`AddDbContextFactory`) **and** in `OnConfiguring`, so it is present on every context — including those created by `IDbContextFactory` (used by the status/cancel handlers and idempotency replay) and the design-time factory.

**Portability:** On SQL Server or PostgreSQL the same column would become a native `rowversion`/`xmin`; the interceptor would then simply be redundant (the provider overwrites the value). No application code changes are needed.

### Schema

The full schema is in [`schema.sql`](./schema.sql) and in the EF Core migration `OrderManagement.Api/Infrastructure/Migrations/20260812084006_InitialCreate.cs`. Key points:

- **`Products`** — `Id`, `Name`, `StockQuantity`, `Price`, `RowVersion` (concurrency token), `CreatedAt`, `UpdatedAt`; `CHECK (StockQuantity >= 0)`; index on `Name`.
- **`Orders`** — `Id`, `CustomerId`, `ShippingAddress`, `Status` (int enum), `TotalAmount`, `CreatedAt`, `UpdatedAt`, `RowVersion`; indexes on `Status`, `CustomerId`, `CreatedAt`, and a composite `(Status, CustomerId, CreatedAt)` for the list filter.
- **`OrderItems`** — `Id`, `OrderId` (CASCADE), `ProductId` (RESTRICT), `Quantity`, `UnitPrice` (price snapshot).
- **`IdempotencyRecords`** — `IdempotencyKey` (PK → UNIQUE), `RequestHash` (SHA-256), `RequestPath`, `Status`, `ResponseStatusCode`, `ResponseBody`, `OrderId`, `CreatedAt`, `CompletedAt`, `RowVersion`.

---

## 7. Testing Strategy

- **Real database, not mocks.** Concurrency tests use a real SQLite file per test (`TestHostFactory.BuildAsync` creates a unique `.db`, applies the schema via `EnsureCreated`, and seeds products). Mocks cannot validate UNIQUE/CHECK constraints or transactional isolation — the entire value of these tests is that they exercise a real engine.
- **Per-task DI scopes.** EF Core's `DbContext` is **not thread-safe**. Each concurrent `Task.Run` resolves its own `IOrderService` via `sp.CreateOrderScope()`, which creates a fresh DI scope (mirroring how ASP.NET Core gives each HTTP request its own scope). This is why the concurrency tests are realistic: each concurrent request gets an independent tracked context, exactly as in production.
- **Deterministic assertions on non-deterministic races.** The tests do not assert *which* request wins (that is scheduling-dependent); they assert the **invariants** that must hold regardless of who wins:
  - Stock is never negative.
  - The total sold never exceeds available stock.
  - Exactly one order is created per idempotency key.
  - All duplicate-key callers receive the same order id.
  - The order always ends in a single, reachable, consistent state with stock matching that state.

### Running a single scenario

```bash
dotnet test -c Release --filter "FullyQualifiedName~ScenarioA"
dotnet test -c Release --filter "FullyQualifiedName~ScenarioB"
dotnet test -c Release --filter "FullyQualifiedName~ScenarioC"
```

---

## Design Notes & Trade-offs

- **Atomic conditional UPDATE vs. SELECT-FOR-UPDATE (pessimistic locking).** The conditional UPDATE was chosen because it is a single statement (no lock-management, no deadlock risk, no held locks during application work) and works uniformly across SQLite/SQL Server/PostgreSQL. Pessimistic locking would be an alternative on SQL Server/PostgreSQL (`SERIALIZABLE` or `SELECT ... FOR UPDATE`) but is heavier and not uniformly available on SQLite.
- **Optimistic vs. pessimistic for status updates.** Optimistic concurrency (`RowVersion`) was chosen for status updates because order status changes are low-contention (an order is rarely updated by many operators at once) and optimistic locking avoids holding write locks across the round-trip to the client. Under high contention it degrades to retry/reject, which is acceptable for this workflow.
- **`IDbContextFactory` for independent contexts.** Mutating handlers (`UpdateStatusAsync`, `CancelOrderAsync`, idempotency replay) create fresh contexts from the factory so concurrent requests never share a tracked context. The request-scoped `DbContext` is used only for the main order/stock work of a single create.
- **EnsureCreated vs. Migrations.** `Program.cs` uses `EnsureCreated()` for prototype convenience (creates the DB + schema on first run). The EF migration is included and is the canonical schema definition; a production deployment would use `dotnet ef database update` instead.
