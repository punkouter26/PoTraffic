# API Contracts — System Endpoints

This file covers two unrelated but infrastructure-critical endpoint groups:

1. **`POST /api/client-logs`** — WASM structured log forwarding (all environments)
2. **`POST /e2e/dev-login`** and **`POST /e2e/seed`** — test-environment-only helpers for E2E and integration test suites

---

## Client Log Forwarding

### POST /api/client-logs

**Summary**: Accept a batch of structured log entries forwarded from the Blazor WASM client. The server enriches each entry with `correlationId` and `sessionId` from the payload and writes to the Serilog pipeline (see `research.md §3`).

This endpoint is active in **all** environments.

**Auth**: `Bearer`

**Request**

Body is a JSON array of log entries. Maximum batch size: **50 entries**. Emission is rate-limited to once per 10 seconds per client.

| Field | Type | Required | Notes |
|---|---|---|---|
| `[].timestamp` | `string` | ✅ | ISO 8601 UTC timestamp from the client |
| `[].level` | `string` | ✅ | Serilog level name: `"Verbose"` \| `"Debug"` \| `"Information"` \| `"Warning"` \| `"Error"` \| `"Fatal"` |
| `[].messageTemplate` | `string` | ✅ | Structured log message template — **never** a user-interpolated string (GDPR / injection guard) |
| `[].properties` | `object` | ❌ | Key-value structured properties; values must not include route addresses, GPS coordinates, or user PII (GDPR data minimisation) |
| `[].correlationId` | `string` | ✅ | Client-generated `Guid` stored in `sessionStorage`; links client and server log spans |
| `[].sessionId` | `string` | ✅ | Client-generated `Guid` identifying the current browser session |
| `[].exception` | `string` | ❌ | Exception type and message only — no stack trace PII |

**Example Request**
```json
[
  {
    "timestamp": "2026-02-19T07:05:12.345Z",
    "level": "Warning",
    "messageTemplate": "Poll fetch returned unexpected status {StatusCode} for route {RouteId}",
    "properties": {
      "statusCode": 503,
      "routeId": "3fa85f64-5717-4562-b3fc-2c963f66afa6"
    },
    "correlationId": "e1f2a3b4-0000-0000-0000-000000000010",
    "sessionId": "f2a3b4c5-0000-0000-0000-000000000011"
  }
]
```

**Response 204**
```
(no body)
```

**Error Responses**
| Status | Condition |
|---|---|
| 400 | Array is empty, exceeds 50 entries, or any entry fails schema validation |
| 401 | Bearer token invalid or expired |
| 429 | Batch submitted more frequently than once per 10 seconds from the same client |

---

## E2E / Integration Test Helpers

> ⚠ **These endpoints are ONLY active when `ASPNETCORE_ENVIRONMENT` is set to `"Testing"`.** In any other environment (`Development`, `Staging`, `Production`) the route registration code is conditionally excluded at startup. They MUST NOT be exposed in production.

---

### POST /e2e/dev-login

**Summary**: Issue a valid JWT access token for a test identity without going through the full authentication flow. Used by Playwright E2E tests and `WebApplicationFactory<T>` integration tests to acquire bearer tokens without depending on password hashing or email verification.

**Auth**: `None` — ⚠ Testing environment only

**Request**
| Field | Type | Required | Notes |
|---|---|---|---|
| `email` | `string` | ✅ | Email of an existing seeded test user; must exist in the database |
| `role` | `string` | ❌ | Defaults to `"Commuter"`; accepts `"Administrator"` to obtain an admin-scoped token |

**Response 200**
```json
{
  "accessToken": "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9...",
  "userId": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
  "email": "testuser@potraffic.test",
  "role": "Commuter",
  "expiresAt": "2026-02-19T10:00:00Z"
}
```

**Error Responses**
| Status | Condition |
|---|---|
| 400 | `email` is missing |
| 404 | No user with the given email exists in the test database |

---

### POST /e2e/seed

**Summary**: Seed the test database with a deterministic dataset for a given scenario. Used by Playwright E2E tests to prepare state before exercising a user journey. Existing data for the seeded users is wiped before inserting — each call is idempotent for a given `scenario`.

**Auth**: `None` — ⚠ Testing environment only

**Request**
| Field | Type | Required | Notes |
|---|---|---|---|
| `scenario` | `string` | ✅ | Named seed scenario; must match a registered `ITestScenarioSeeder` implementation |
| `userId` | `Guid` | ❌ | When provided, seeds data scoped to an existing test user; otherwise a new test user is created |

**Supported Scenarios**
| `scenario` | Description |
|---|---|
| `"empty-user"` | Creates a Commuter with no routes |
| `"route-no-history"` | Creates a Commuter with one route and no poll history |
| `"route-with-baseline"` | Creates a Commuter with one route and ≥ 3 weeks of prior same-weekday sessions (sufficient for baseline rendering) |
| `"quota-exhausted"` | Creates a Commuter with all 10 daily quota slots consumed today |
| `"reroute-sequence"` | Seeds a route session with a known reroute event flag sequence for FR-006 testing |
| `"admin-usage"` | Seeds two users with known poll counts for Admin usage table assertions |

**Response 200**
```json
{
  "scenario": "route-with-baseline",
  "userId": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
  "email": "testuser@potraffic.test",
  "routeId": "a1b2c3d4-5717-4562-b3fc-2c963f66afa6",
  "seededAt": "2026-02-19T09:00:00Z",
  "summary": {
    "usersCreated": 1,
    "routesCreated": 1,
    "sessionsCreated": 14,
    "pollRecordsCreated": 336
  }
}
```

**Error Responses**
| Status | Condition |
|---|---|
| 400 | `scenario` is missing or not a recognised scenario name |
| 404 | `userId` was specified but does not exist in the test database |
