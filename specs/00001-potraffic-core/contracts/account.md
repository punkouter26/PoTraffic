# API Contracts — Account & Settings (US5)

**Slice**: `Features/Account/`  
**Route group**: `/api/account`

---

### GET /api/account/profile

**Summary**: Retrieve the authenticated user's profile data, including locale setting used for public holiday exclusion (FR-021).

**Auth**: `Bearer`

**Request** — no body

**Response 200**
```json
{
  "userId": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
  "email": "alice@example.com",
  "locale": "Europe/London",
  "createdAt": "2026-01-10T12:00:00Z",
  "lastLoginAt": "2026-02-19T06:55:00Z",
  "role": "Commuter"
}
```

**Error Responses**
| Status | Condition |
|---|---|
| 401 | Bearer token invalid or expired |

---

### PUT /api/account/profile

**Summary**: Update the authenticated user's mutable profile fields. Currently only `locale` is user-editable; email changes are not supported in MVP.

**Auth**: `Bearer`

**Request**
| Field | Type | Required | Notes |
|---|---|---|---|
| `locale` | `string` | ✅ | IANA timezone string (e.g. `"America/New_York"`); validated against IANA tz database |

**Response 200**
```json
{
  "userId": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
  "email": "alice@example.com",
  "locale": "America/New_York",
  "updatedAt": "2026-02-19T09:15:00Z"
}
```

**Error Responses**
| Status | Condition |
|---|---|
| 400 | `locale` is empty or not a recognised IANA timezone identifier |
| 401 | Bearer token invalid or expired |

---

### PUT /api/account/password

**Summary**: Change the authenticated user's password. Requires the current password to confirm identity.

**Auth**: `Bearer`

**Request**
| Field | Type | Required | Notes |
|---|---|---|---|
| `currentPassword` | `string` | ✅ | Must match the hash stored on the user record |
| `newPassword` | `string` | ✅ | Min 8 chars; must contain uppercase, lowercase, digit, and special character |
| `confirmNewPassword` | `string` | ✅ | Must match `newPassword` exactly |

**Response 204**
```
(no body)
```

**Error Responses**
| Status | Condition |
|---|---|
| 400 | Validation failure (weak new password, `newPassword` ≠ `confirmNewPassword`) |
| 401 | Bearer token invalid or expired |
| 422 | `currentPassword` does not match the stored hash |

---

### GET /api/account/quota

**Summary**: Retrieve the authenticated user's daily quota usage — how many monitoring windows have been activated today versus the configured maximum (FR-003, FR-010).

**Auth**: `Bearer`

**Request** — no body

**Response 200**
```json
{
  "userId": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
  "date": "2026-02-19",
  "quotaLimit": 10,
  "quotaConsumed": 3,
  "quotaRemaining": 7,
  "resetAtUtc": "2026-02-20T00:00:00Z",
  "warningThresholdReached": false
}
```

**Field Notes**
| Field | Notes |
|---|---|
| `quotaRemaining` | `quotaLimit - quotaConsumed`; client shows prominent warning when ≤ 2 remaining (FR-010) |
| `warningThresholdReached` | `true` when `quotaConsumed >= 8`; client renders the visual warning indicator (FR-010) |
| `resetAtUtc` | Always `00:00 UTC` of the next calendar day; from `SystemConfiguration["quota.reset.utc"]` |

**Error Responses**
| Status | Condition |
|---|---|
| 401 | Bearer token invalid or expired |

---

### DELETE /api/account

**Summary**: Permanently and irreversibly delete the authenticated user's account and **all** associated data — profile, routes, monitoring windows, sessions, and poll records — in a single atomic operation (FR-031, GDPR Art. 17 Right to Erasure).

This endpoint implements a hard-delete: `DELETE FROM Users WHERE Id = @userId` is issued against the database. All cascade deletes are **schema-enforced** via `ON DELETE CASCADE` foreign keys (`Routes → MonitoringWindows`, `Routes → MonitoringSessions`, `Routes → PollRecords`). No application-layer loop iterates over child records — the database engine removes all descendant rows atomically within a single transaction. There is no recovery path after this endpoint returns `204`.

The client MUST display a confirmation prompt before calling this endpoint.

**Auth**: `Bearer`

**Request**
| Field | Type | Required | Notes |
|---|---|---|---|
| `confirmationPhrase` | `string` | ✅ | Caller must supply the literal string `"DELETE MY ACCOUNT"` to prevent accidental invocation |

**Response 204**
```
(no body)
```

**Post-conditions**
- All active Hangfire polling chains belonging to the user are cancelled before the DB delete.
- The bearer token used for this request is immediately invalidated; any subsequent use returns `401`.

**Error Responses**
| Status | Condition |
|---|---|
| 400 | `confirmationPhrase` is missing or does not equal `"DELETE MY ACCOUNT"` |
| 401 | Bearer token invalid or expired |
