# API Contracts — Monitoring Engine (US2)

**Slice**: `Features/Routes/`  
**Route group**: `/api/routes`

---

## Routes

### GET /api/routes

**Summary**: List all routes owned by the authenticated user (FR-017).

**Auth**: `Bearer`

**Request** — query parameters
| Field | Type | Required | Notes |
|---|---|---|---|
| `page` | `int` | ❌ | Defaults to `1` |
| `pageSize` | `int` | ❌ | Defaults to `20`; max `100` |

**Response 200**
```json
{
  "page": 1,
  "pageSize": 20,
  "totalCount": 2,
  "items": [
    {
      "id": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
      "originAddress": "10 Downing Street, London SW1A 2AA, UK",
      "destinationAddress": "Canary Wharf, London E14 5AB, UK",
      "provider": "GoogleMaps",
      "monitoringStatus": "Active",
      "hangfireJobChainId": "bg-job-001",
      "createdAt": "2026-02-01T09:00:00Z",
      "windows": [
        {
          "id": "a1b2c3d4-0000-0000-0000-000000000001",
          "startTime": "07:00",
          "endTime": "09:00",
          "daysOfWeek": ["Monday", "Tuesday", "Wednesday", "Thursday", "Friday"],
          "isActive": true
        }
      ]
    }
  ]
}
```

**Error Responses**
| Status | Condition |
|---|---|
| 401 | Bearer token invalid or expired |

---

### POST /api/routes

**Summary**: Create a new route. Both addresses are verified against the configured mapping provider before saving (FR-013). Rejects routes where origin and destination resolve to the same coordinates (FR-014).

**Auth**: `Bearer`

**Request**
| Field | Type | Required | Notes |
|---|---|---|---|
| `originAddress` | `string` | ✅ | Free-text; will be standardised via provider verification |
| `destinationAddress` | `string` | ✅ | Free-text; will be standardised via provider verification |
| `provider` | `string` | ✅ | Enum: `"GoogleMaps"` \| `"TomTom"` |

**Response 201**
```json
{
  "id": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
  "originAddress": "10 Downing Street, London SW1A 2AA, UK",
  "destinationAddress": "Canary Wharf, London E14 5AB, UK",
  "provider": "GoogleMaps",
  "monitoringStatus": "Active",
  "createdAt": "2026-02-19T08:00:00Z"
}
```

**Error Responses**
| Status | Condition |
|---|---|
| 400 | Validation failure (missing fields, unknown provider) |
| 401 | Bearer token invalid or expired |
| 422 | Origin or destination address could not be verified by the mapping provider |
| 422 | Origin and destination resolve to identical coordinates (FR-014) |

---

### PUT /api/routes/{routeId}

**Summary**: Update an existing route's addresses and/or provider. Re-verifies addresses when changed. Any active Hangfire polling chain is restarted with the new configuration.

**Auth**: `Bearer`

**Path Parameters**
| Parameter | Type | Notes |
|---|---|---|
| `routeId` | `Guid` | — |

**Request**
| Field | Type | Required | Notes |
|---|---|---|---|
| `originAddress` | `string` | ❌ | If provided, re-verified against mapping provider |
| `destinationAddress` | `string` | ❌ | If provided, re-verified against mapping provider |
| `provider` | `string` | ❌ | Enum: `"GoogleMaps"` \| `"TomTom"` |

**Response 200**
```json
{
  "id": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
  "originAddress": "10 Downing Street, London SW1A 2AA, UK",
  "destinationAddress": "London Bridge, London SE1 9BG, UK",
  "provider": "TomTom",
  "monitoringStatus": "Active",
  "updatedAt": "2026-02-19T08:30:00Z"
}
```

**Error Responses**
| Status | Condition |
|---|---|
| 400 | Validation failure |
| 401 | Bearer token invalid or expired |
| 403 | Route does not belong to the authenticated user |
| 404 | Route not found |
| 422 | Address could not be verified or origin/destination resolve to same coordinates |

---

### DELETE /api/routes/{routeId}

**Summary**: Permanently delete a route and all associated data. Cancels the Hangfire polling chain (`BackgroundJob.Delete(hangfireJobChainId)`) before deleting the row. Cascade deletes `MonitoringWindows`, `MonitoringSessions`, and `PollRecords` are schema-enforced via `ON DELETE CASCADE` foreign keys — no application-layer loop is required (FR-031 data model note).

**Auth**: `Bearer`

**Path Parameters**
| Parameter | Type | Notes |
|---|---|---|
| `routeId` | `Guid` | — |

**Response 204**
```
(no body)
```

**Error Responses**
| Status | Condition |
|---|---|
| 401 | Bearer token invalid or expired |
| 403 | Route does not belong to the authenticated user |
| 404 | Route not found |

---

### POST /api/routes/verify-address

**Summary**: Verify and standardise a free-text address against the configured mapping provider, without creating a route. Used by the UI during inline route creation/editing (FR-013, FR-018).

**Auth**: `Bearer`

**Request**
| Field | Type | Required | Notes |
|---|---|---|---|
| `address` | `string` | ✅ | Free-text address to verify |
| `provider` | `string` | ✅ | Enum: `"GoogleMaps"` \| `"TomTom"` — specifies which provider to geocode against |

**Response 200**
```json
{
  "standardisedAddress": "10 Downing Street, London SW1A 2AA, UK",
  "latitude": 51.5034,
  "longitude": -0.1276
}
```

**Error Responses**
| Status | Condition |
|---|---|
| 400 | Address string is empty or provider is invalid |
| 401 | Bearer token invalid or expired |
| 422 | Address could not be resolved by the mapping provider |

---

### POST /api/routes/{routeId}/check-now

**Summary**: Perform an immediate, ad-hoc travel duration query for the route (FR-016). Returns the current duration and distance as a transient result. Does **not** persist a `PollRecord`, does **not** create a session, and does **not** decrement the daily quota.

**Auth**: `Bearer`

**Path Parameters**
| Parameter | Type | Notes |
|---|---|---|
| `routeId` | `Guid` | — |

**Request** — no body

**Response 200**
```json
{
  "routeId": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
  "provider": "GoogleMaps",
  "travelDurationSeconds": 1560,
  "distanceMetres": 12400,
  "queriedAt": "2026-02-19T07:45:00Z"
}
```

**Error Responses**
| Status | Condition |
|---|---|
| 401 | Bearer token invalid or expired |
| 403 | Route does not belong to the authenticated user |
| 404 | Route not found |
| 502 | Mapping provider returned an error or timed out |

---

## Monitoring Windows

### GET /api/routes/{routeId}/windows

**Summary**: List all monitoring windows defined for a route.

**Auth**: `Bearer`

**Path Parameters**
| Parameter | Type | Notes |
|---|---|---|
| `routeId` | `Guid` | — |

**Response 200**
```json
{
  "routeId": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
  "windows": [
    {
      "id": "a1b2c3d4-0000-0000-0000-000000000001",
      "startTime": "07:00",
      "endTime": "09:00",
      "daysOfWeek": ["Monday", "Tuesday", "Wednesday", "Thursday", "Friday"],
      "daysOfWeekMask": 31,
      "isActive": true
    }
  ]
}
```

**Error Responses**
| Status | Condition |
|---|---|
| 401 | Bearer token invalid or expired |
| 403 | Route does not belong to the authenticated user |
| 404 | Route not found |

---

### POST /api/routes/{routeId}/windows

**Summary**: Add a new monitoring window to a route. The Hangfire recursive job chain is (re)scheduled using the updated window set.

**Auth**: `Bearer`

**Path Parameters**
| Parameter | Type | Notes |
|---|---|---|
| `routeId` | `Guid` | — |

**Request**
| Field | Type | Required | Notes |
|---|---|---|---|
| `startTime` | `string` | ✅ | `HH:mm` 24-hour format; e.g. `"07:00"` |
| `endTime` | `string` | ✅ | `HH:mm`; must be after `startTime` |
| `daysOfWeek` | `string[]` | ✅ | Array of ISO day names: `"Monday"` … `"Sunday"`; at least one required |

**Response 201**
```json
{
  "id": "a1b2c3d4-0000-0000-0000-000000000001",
  "routeId": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
  "startTime": "07:00",
  "endTime": "09:00",
  "daysOfWeek": ["Monday", "Tuesday", "Wednesday", "Thursday", "Friday"],
  "daysOfWeekMask": 31,
  "isActive": true
}
```

**Error Responses**
| Status | Condition |
|---|---|
| 400 | Validation failure (`endTime` ≤ `startTime`, no days selected) |
| 401 | Bearer token invalid or expired |
| 403 | Route does not belong to the authenticated user |
| 404 | Route not found |

---

### PUT /api/routes/{routeId}/windows/{windowId}

**Summary**: Update an existing monitoring window. Reschedules the Hangfire chain accordingly.

**Auth**: `Bearer`

**Path Parameters**
| Parameter | Type | Notes |
|---|---|---|
| `routeId` | `Guid` | — |
| `windowId` | `Guid` | — |

**Request**
| Field | Type | Required | Notes |
|---|---|---|---|
| `startTime` | `string` | ❌ | `HH:mm` |
| `endTime` | `string` | ❌ | `HH:mm`; must be after `startTime` when provided |
| `daysOfWeek` | `string[]` | ❌ | At least one day required if provided |
| `isActive` | `bool` | ❌ | `false` soft-disables the window without deleting it |

**Response 200**
```json
{
  "id": "a1b2c3d4-0000-0000-0000-000000000001",
  "routeId": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
  "startTime": "07:30",
  "endTime": "09:00",
  "daysOfWeek": ["Monday", "Wednesday", "Friday"],
  "daysOfWeekMask": 21,
  "isActive": true
}
```

**Error Responses**
| Status | Condition |
|---|---|
| 400 | Validation failure |
| 401 | Bearer token invalid or expired |
| 403 | Route does not belong to the authenticated user |
| 404 | Route or window not found |

---

### DELETE /api/routes/{routeId}/windows/{windowId}

**Summary**: Delete a monitoring window. If this is the last window on the route, the Hangfire polling chain is stopped (`BackgroundJob.Delete`).

**Auth**: `Bearer`

**Path Parameters**
| Parameter | Type | Notes |
|---|---|---|
| `routeId` | `Guid` | — |
| `windowId` | `Guid` | — |

**Response 204**
```
(no body)
```

**Error Responses**
| Status | Condition |
|---|---|
| 401 | Bearer token invalid or expired |
| 403 | Route does not belong to the authenticated user |
| 404 | Route or window not found |

---

### POST /api/routes/{routeId}/windows/{windowId}/start

**Summary**: Manually activate a monitoring window, starting a new `MonitoringSession` immediately. Consumes one daily quota slot. Returns `quotaRemaining` so the client can update the quota indicator without a separate fetch (FR-003, FR-010).

**Auth**: `Bearer`

**Path Parameters**
| Parameter | Type | Notes |
|---|---|---|
| `routeId` | `Guid` | — |
| `windowId` | `Guid` | — |

**Request** — no body

**Response 200**
```json
{
  "sessionId": "b2c3d4e5-0000-0000-0000-000000000002",
  "routeId": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
  "windowId": "a1b2c3d4-0000-0000-0000-000000000001",
  "state": "Active",
  "startedAt": "2026-02-19T07:00:00Z",
  "quotaRemaining": 8
}
```

**Error Responses**
| Status | Condition |
|---|---|
| 401 | Bearer token invalid or expired |
| 403 | Route does not belong to the authenticated user |
| 404 | Route or window not found |
| 409 | A session for this route is already active today |
| 429 | Daily quota exhausted — no slots remaining (FR-003, FR-004) |

---

### POST /api/routes/{routeId}/windows/{windowId}/stop

**Summary**: Manually stop an active monitoring session. Cancels the Hangfire job chain leg for this window. Any in-flight poll is allowed to complete.

**Auth**: `Bearer`

**Path Parameters**
| Parameter | Type | Notes |
|---|---|---|
| `routeId` | `Guid` | — |
| `windowId` | `Guid` | — |

**Request** — no body

**Response 200**
```json
{
  "sessionId": "b2c3d4e5-0000-0000-0000-000000000002",
  "state": "Completed",
  "stoppedAt": "2026-02-19T08:47:00Z",
  "pollCount": 21
}
```

**Error Responses**
| Status | Condition |
|---|---|
| 401 | Bearer token invalid or expired |
| 403 | Route does not belong to the authenticated user |
| 404 | Route or window not found |
| 409 | No active session exists for this window today |
