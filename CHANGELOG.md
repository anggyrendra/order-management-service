# Changelog

All notable changes to this project are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [1.0.0] - 2026-08-12

### Added
- **Order Management REST API** built with ASP.NET Core 8.0 Web API (controllers).
- **Create Order** endpoint (`POST /api/orders`) with `Idempotency-Key` header support to prevent double orders under concurrent or retried requests.
- **Get Order by ID** endpoint (`GET /api/orders/{id}`).
- **List Orders** endpoint (`GET /api/orders`) with filters by status, customerId, and date range, plus pagination (page/pageSize).
- **Update Order Status** endpoint (`PATCH /api/orders/{id}/status`) enforcing a strict state machine:
  - Pending → Confirmed | Cancelled
  - Confirmed → Shipped | Cancelled
  - Shipped → Delivered
  - Delivered / Cancelled → terminal (no further changes)
- **Cancel Order** endpoint (`POST /api/orders/{id}/cancel`) allowed only from Pending or Confirmed, with automatic stock restoration.
- **Products** endpoints (`GET /api/products`, `GET /api/products/{id}`) for stock visibility.
- **Stock Management** with atomic conditional SQL UPDATE (`SET StockQuantity = StockQuantity - @qty WHERE StockQuantity >= @qty`) ensuring stock never goes negative, even under high concurrency.
- **Idempotency** via `IdempotencyRecord` table with `IdempotencyKey` as primary key (UNIQUE constraint), insert-first pattern, and SHA-256 request hash for payload-mismatch detection.
- **Optimistic Concurrency** using application-managed concurrency tokens (`IsConcurrencyToken()`) with a custom `ConcurrencyTokenInterceptor` (ISaveChangesInterceptor) that generates fresh tokens on every Add/Update for entities implementing `IConcurrencyToken`.
- **Database-level backstop** via CHECK constraint `CK_Product_StockQuantity_NonNegative` on the Products table.
- **Correlation ID middleware** that reads or generates an `X-Correlation-Id` header and pushes it into Serilog's `LogContext` for end-to-end request tracing.
- **Global exception middleware** mapping domain exceptions to a consistent JSON error envelope with status codes 400, 404, 409, and 422, each including the correlation ID.
- **Serilog** structured logging with console and file sinks (no `Console.WriteLine`).
- **Entity Framework Core 8.0.10** with SQLite provider, including EF Core migration (`InitialCreate`) and a standalone `schema.sql` for manual inspection.
- **xUnit test suite** with 35 tests covering:
  - Skenario A — Concurrent stock deduction (2 tests, including a 20-request stress test).
  - Skenario B — Concurrent status update (3 tests).
  - Skenario C — Idempotent create under race (3 tests, including 30 concurrent duplicate-key creates).
  - Order state machine transitions (13 tests).
  - OrderService business logic (10 tests).
- **Comprehensive README** documenting tech stack, project structure, API reference, idempotency strategy justification, concurrency handling for Skenario A/B/C, 4 additional race conditions identified and prevented, validation and error handling, logging strategy, database choice justification, testing strategy, and design notes.
- **.gitignore**, **.editorconfig**, **LICENSE** (MIT), and this **CHANGELOG.md** for repository readiness.

### Concurrency Handling
- **Skenario A (concurrent stock deduction):** Resolved via single atomic conditional UPDATE where the database acts as the arbiter — at most one competing UPDATE affects a row.
- **Skenario B (concurrent status update):** Resolved via optimistic concurrency tokens on the Order entity; losing updates throw `DbUpdateConcurrencyException`, mapped to HTTP 409.
- **Skenario C (idempotent create under race):** Resolved via UNIQUE constraint on `IdempotencyKey` as a mutex; the losing request replays the stored response using fresh `DbContext` instances with a retry loop to handle visibility gaps.

### Additional Race Conditions Prevented
1. Stale read on cancel (optimistic token on Order prevents restoring stock for an order that already transitioned).
2. Lost update on product edit (concurrency token on Product).
3. Double stock restore on cancel (optimistic lock ensures only one cancel succeeds).
4. Ordering deadlock between stock deduction and order insert (sequential per-product UPDATE within a single transaction).
