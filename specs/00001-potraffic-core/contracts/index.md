# API Contracts — PoTraffic Core

**Feature Branch**: `00001-potraffic-core`  
**Created**: 2026-02-19  
**Architecture**: ASP.NET Core Minimal API (.NET 10) · Vertical Slice · JWT Bearer auth · ProblemDetails error envelope

---

## Table of Contents

| File | Slice | Endpoints |
|---|---|---|
| [auth.md](auth.md) | US1 — Authentication | Register, Login, Logout, Refresh Token |
| [routes.md](routes.md) | US2 — Monitoring Engine | CRUD Routes, Monitoring Windows, Start/Stop, Check Now, Verify Address |
| [history.md](history.md) | US3 — History & Baseline | Poll History, Baseline (STDDEV), Session List |
| [admin.md](admin.md) | US4 — Admin Dashboard | User Usage Metrics, System Configuration, Poll Cost Summary |
| [account.md](account.md) | US5 — Account & Settings | Profile, Password, Quota, Delete Account (GDPR) |
| [system.md](system.md) | System | Client Log Forwarding, E2E Testing Endpoints |

---

## Conventions

| Convention | Value |
|---|---|
| Route casing | kebab-case |
| ID type | `Guid` (UUID v4 / sequential) |
| Timestamps | ISO 8601 UTC strings (`2026-02-19T07:00:00Z`) |
| Pagination | `page` (1-based) · `pageSize` · `totalCount` |
| Error envelope | RFC 7807 `ProblemDetails` |
| Auth header | `Authorization: Bearer <jwt>` |
| Quota tracking | `quotaRemaining` present in any response that consumes a daily quota slot |

---

## Global Error Responses

All endpoints may return these in addition to endpoint-specific errors:

| Status | Condition |
|---|---|
| 400 | Request body fails FluentValidation |
| 401 | Missing or expired JWT bearer token |
| 403 | Authenticated but insufficient role |
| 500 | Unhandled server error — body is `ProblemDetails` with `traceId` |
