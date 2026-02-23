# API Contracts — History & Baseline (US3)

**Slice**: `Features/History/`  
**Route group**: `/api/routes/{routeId}`

---

### GET /api/routes/{routeId}/poll-history

**Summary**: Retrieve a paginated, reverse-chronological list of `PollRecord` entries for a route. Soft-deleted records (`IsDeleted = 1`) are automatically excluded by the EF Core global query filter. The `isRerouted` flag is present on every entry so the client can render reroute indicators on the chart (FR-011).

**Auth**: `Bearer`

**Path Parameters**
| Parameter | Type | Notes |
|---|---|---|
| `routeId` | `Guid` | — |

**Query Parameters**
| Field | Type | Required | Notes |
|---|---|---|---|
| `page` | `int` | ❌ | Defaults to `1` (1-based) |
| `pageSize` | `int` | ❌ | Defaults to `50`; max `200` |
| `sessionId` | `Guid` | ❌ | When provided, scopes results to a single session |
| `from` | `string` | ❌ | ISO 8601 UTC lower bound for `polledAt` |
| `to` | `string` | ❌ | ISO 8601 UTC upper bound for `polledAt` |

**Response 200**
```json
{
  "routeId": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
  "page": 1,
  "pageSize": 50,
  "totalCount": 168,
  "items": [
    {
      "id": "c3d4e5f6-0000-0000-0000-000000000003",
      "sessionId": "b2c3d4e5-0000-0000-0000-000000000002",
      "polledAt": "2026-02-19T08:55:00Z",
      "travelDurationSeconds": 1740,
      "distanceMetres": 12650,
      "provider": "GoogleMaps",
      "isRerouted": true
    },
    {
      "id": "d4e5f6a7-0000-0000-0000-000000000004",
      "sessionId": "b2c3d4e5-0000-0000-0000-000000000002",
      "polledAt": "2026-02-19T08:50:00Z",
      "travelDurationSeconds": 1560,
      "distanceMetres": 12400,
      "provider": "GoogleMaps",
      "isRerouted": false
    }
  ]
}
```

**Notes**
- `isRerouted: true` indicates the record was flagged as a suspected reroute per FR-006 (distance ≥ 115% of session median, confirmed by two consecutive elevated readings).
- Ad-hoc "Check Now" polls (`sessionId: null`) are included in results unless `sessionId` filter is applied.

**Error Responses**
| Status | Condition |
|---|---|
| 400 | Invalid `from`/`to` date format or `page`/`pageSize` out of range |
| 401 | Bearer token invalid or expired |
| 403 | Route does not belong to the authenticated user |
| 404 | Route not found |

---

### GET /api/routes/{routeId}/baseline

**Summary**: Return the STDDEV-based historical baseline for a route: mean and ±1σ travel duration at each 5-minute time slot, aggregated across all qualifying prior sessions within the rolling 90-day window (FR-007, FR-012, FR-021).

Baseline computation rules:
- Only sessions within the past 90 calendar days are included (FR-019 rolling window).
- Sessions marked `isHolidayExcluded = true` on the user's locale are excluded (FR-021).
- Ad-hoc "Check Now" poll records (`SessionId IS NULL`) are excluded.
- A slot's `stdDevDurationSeconds` is `null` when fewer than 2 records exist for that slot — treat as insufficient data.
- The entire baseline object is `null` when fewer than **3 distinct prior same-weekday sessions** have contributed data to any slot (FR-012). The client MUST display the "building baseline" message in this case and MUST NOT render the variance band.

**Auth**: `Bearer`

**Path Parameters**
| Parameter | Type | Notes |
|---|---|---|
| `routeId` | `Guid` | — |

**Query Parameters**
| Field | Type | Required | Notes |
|---|---|---|---|
| `dayOfWeek` | `string` | ❌ | ISO day name (e.g. `"Wednesday"`); defaults to today's UTC day of week |

**Response 200 — sufficient data (≥ 3 sessions)**
```json
{
  "routeId": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
  "dayOfWeek": "Wednesday",
  "sessionCount": 7,
  "baselineAvailable": true,
  "optimalDepartureWindow": {
    "startTime": "08:05",
    "endTime": "08:20",
    "meanDurationSeconds": 1320
  },
  "slots": [
    {
      "slotMinute": 420,
      "slotTime": "07:00",
      "meanDurationSeconds": 1560.0,
      "stdDevDurationSeconds": 84.3,
      "sampleCount": 7
    },
    {
      "slotMinute": 425,
      "slotTime": "07:05",
      "meanDurationSeconds": 1500.0,
      "stdDevDurationSeconds": 72.1,
      "sampleCount": 7
    }
  ]
}
```

**Response 200 — insufficient data (< 3 sessions)**
```json
{
  "routeId": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
  "dayOfWeek": "Wednesday",
  "sessionCount": 1,
  "baselineAvailable": false,
  "optimalDepartureWindow": null,
  "slots": []
}
```

**Field Notes**
| Field | Notes |
|---|---|
| `baselineAvailable` | `false` when `sessionCount < 3`; client must show "building baseline" message and omit baseline line and variance band (FR-012) |
| `stdDevDurationSeconds` | `null` for slots with fewer than 2 data points (SQL `STDEV()` returns NULL for < 2 rows); client treats `null` σ as "insufficient data" for that slot |
| `slotMinute` | Minutes since midnight, rounded down to nearest 5-minute bucket (e.g. `420` = 07:00, `485` = 08:05) |
| `optimalDepartureWindow` | Time range of contiguous 5-minute slots whose mean duration falls within 5% of the route's historical minimum for that weekday (FR-009); `null` when `baselineAvailable = false` |

**Error Responses**
| Status | Condition |
|---|---|
| 400 | Invalid `dayOfWeek` value |
| 401 | Bearer token invalid or expired |
| 403 | Route does not belong to the authenticated user |
| 404 | Route not found |

---

### GET /api/routes/{routeId}/sessions

**Summary**: Return a paginated list of `MonitoringSession` records for a route, ordered by `sessionDate` descending.

**Auth**: `Bearer`

**Path Parameters**
| Parameter | Type | Notes |
|---|---|---|
| `routeId` | `Guid` | — |

**Query Parameters**
| Field | Type | Required | Notes |
|---|---|---|---|
| `page` | `int` | ❌ | Defaults to `1` |
| `pageSize` | `int` | ❌ | Defaults to `20`; max `100` |

**Response 200**
```json
{
  "routeId": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
  "page": 1,
  "pageSize": 20,
  "totalCount": 14,
  "items": [
    {
      "id": "b2c3d4e5-0000-0000-0000-000000000002",
      "sessionDate": "2026-02-19",
      "state": "Completed",
      "firstPollAt": "2026-02-19T07:00:18Z",
      "lastPollAt": "2026-02-19T08:55:03Z",
      "pollCount": 21,
      "isHolidayExcluded": false
    }
  ]
}
```

**Error Responses**
| Status | Condition |
|---|---|
| 400 | `page` or `pageSize` out of range |
| 401 | Bearer token invalid or expired |
| 403 | Route does not belong to the authenticated user |
| 404 | Route not found |
