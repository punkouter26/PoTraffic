# API Contracts — Admin Dashboard (US4)

**Slice**: `Features/Admin/`  
**Route group**: `/api/admin`  
**Auth note**: All endpoints in this file require the `Administrator` role. Any request from a Commuter-role token receives `403`. Non-authenticated requests receive `401`. See FR-022.

---

### GET /api/admin/users

**Summary**: List all registered users with their today-UTC poll count and estimated provider cost, enabling real-time cost governance (FR-023). Results are paginated.

**Auth**: `Bearer` · `[AdminOnly]`

**Query Parameters**
| Field | Type | Required | Notes |
|---|---|---|---|
| `page` | `int` | ❌ | Defaults to `1` |
| `pageSize` | `int` | ❌ | Defaults to `50`; max `200` |
| `search` | `string` | ❌ | Optional email prefix filter |

**Response 200**
```json
{
  "page": 1,
  "pageSize": 50,
  "totalCount": 12,
  "asOfUtc": "2026-02-19T08:00:00Z",
  "items": [
    {
      "userId": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
      "email": "alice@example.com",
      "locale": "Europe/London",
      "createdAt": "2026-01-10T12:00:00Z",
      "lastLoginAt": "2026-02-19T06:55:00Z",
      "todayPollCount": 47,
      "todayEstimatedCostUsd": 0.2350,
      "providerBreakdown": [
        { "provider": "GoogleMaps", "pollCount": 32, "estimatedCostUsd": 0.1600 },
        { "provider": "TomTom",    "pollCount": 15, "estimatedCostUsd": 0.0675 }
      ]
    }
  ]
}
```

**Field Notes**
| Field | Notes |
|---|---|
| `todayEstimatedCostUsd` | Sum of `pollCount × SystemConfiguration["cost.perpoll.<provider>"]` per provider (FR-023) |
| `providerBreakdown` | Per-provider breakdown using the per-provider cost rate from `SystemConfiguration`; enables granular cost attribution |
| `asOfUtc` | Timestamp at which the usage data was calculated; reflects the UTC day boundary used to scope "today" |

**Error Responses**
| Status | Condition |
|---|---|
| 401 | Bearer token invalid or expired |
| 403 | Authenticated user does not hold the Administrator role |

---

### GET /api/admin/system-configuration

**Summary**: Retrieve all `SystemConfiguration` key-value entries. Sensitive values are masked on this endpoint following the same rule as the /Diag page (FR-025, FR-026): first two and last two characters are preserved; all intermediate characters are replaced with `*`.

**Auth**: `Bearer` · `[AdminOnly]`

**Response 200**
```json
{
  "entries": [
    {
      "key": "cost.perpoll.googlemaps",
      "value": "0.005",
      "description": "Cost in USD per Google Maps Directions API call",
      "isSensitive": false
    },
    {
      "key": "cost.perpoll.tomtom",
      "value": "0.0045",
      "description": "Cost in USD per TomTom Routing API call",
      "isSensitive": false
    },
    {
      "key": "quota.daily.default",
      "value": "10",
      "description": "Maximum monitoring sessions per user per UTC day",
      "isSensitive": false
    },
    {
      "key": "googlemaps.api-key",
      "value": "AI****9x",
      "description": "Google Maps Directions API key",
      "isSensitive": true
    }
  ]
}
```

**Field Notes**
| Field | Notes |
|---|---|
| `value` | Sensitive values (`isSensitive: true`) are masked: first 2 + last 2 chars visible, all middle chars replaced with `*` (e.g. `AK****99`). This applies to 100% of sensitive entries — no plain-text exposure (SC-009) |
| `isSensitive` | Informs the client to display a lock icon or similar indicator |

**Error Responses**
| Status | Condition |
|---|---|
| 401 | Bearer token invalid or expired |
| 403 | Authenticated user does not hold the Administrator role |

---

### PUT /api/admin/system-configuration/{key}

**Summary**: Update the value of a single `SystemConfiguration` entry. The updated value takes effect immediately upon the next read by any application service. Sensitive entries remain masked in the response.

**Auth**: `Bearer` · `[AdminOnly]`

**Path Parameters**
| Parameter | Type | Notes |
|---|---|---|
| `key` | `string` | Natural string PK of the configuration entry; URL-encoded if it contains dots |

**Request**
| Field | Type | Required | Notes |
|---|---|---|---|
| `value` | `string` | ✅ | New raw value; application layer validates type compatibility (numeric, time, etc.) |

**Response 200**
```json
{
  "key": "quota.daily.default",
  "value": "15",
  "description": "Maximum monitoring sessions per user per UTC day",
  "isSensitive": false,
  "updatedAt": "2026-02-19T09:00:00Z"
}
```

**Error Responses**
| Status | Condition |
|---|---|
| 400 | `value` is empty or fails type-coercion validation for the known key |
| 401 | Bearer token invalid or expired |
| 403 | Authenticated user does not hold the Administrator role |
| 404 | Configuration key not found |

---

### GET /api/admin/poll-cost-summary

**Summary**: Return an aggregated cost summary grouped by mapping provider for a given date range. Supports cost review and budget tracking across the entire user base (FR-023, FR-024).

**Auth**: `Bearer` · `[AdminOnly]`

**Query Parameters**
| Field | Type | Required | Notes |
|---|---|---|---|
| `from` | `string` | ❌ | ISO 8601 UTC date; defaults to start of current UTC day |
| `to` | `string` | ❌ | ISO 8601 UTC date; defaults to current UTC moment |

**Response 200**
```json
{
  "from": "2026-02-19T00:00:00Z",
  "to": "2026-02-19T09:00:00Z",
  "totalPollCount": 1240,
  "totalEstimatedCostUsd": 6.0320,
  "byProvider": [
    {
      "provider": "GoogleMaps",
      "pollCount": 840,
      "costPerPoll": 0.0050,
      "estimatedCostUsd": 4.2000
    },
    {
      "provider": "TomTom",
      "pollCount": 400,
      "costPerPoll": 0.0045,
      "estimatedCostUsd": 1.8000
    }
  ]
}
```

**Field Notes**
| Field | Notes |
|---|---|
| `costPerPoll` | Snapshot of the `SystemConfiguration` rate at query time; noted per-provider to reflect any mid-period rate changes |

**Error Responses**
| Status | Condition |
|---|---|
| 400 | `from` or `to` is not a valid ISO 8601 date string, or `from > to` |
| 401 | Bearer token invalid or expired |
| 403 | Authenticated user does not hold the Administrator role |
