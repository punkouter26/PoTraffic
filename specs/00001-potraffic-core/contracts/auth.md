# API Contracts — Authentication (US1)

**Slice**: `Features/Auth/`  
**Route group**: `/api/auth`

---

### POST /api/auth/register

**Summary**: Create a new Commuter account. Email verification is required before the account is active (FR-029).

**Auth**: `None`

**Request**
| Field | Type | Required | Notes |
|---|---|---|---|
| `email` | `string` | ✅ | RFC 5321; max 320 chars; must be unique |
| `password` | `string` | ✅ | Min 8 chars; must contain uppercase, lowercase, digit, and special character |
| `locale` | `string` | ✅ | IANA timezone string (e.g. `Europe/London`); stored on `Users.Locale` for holiday exclusion |

**Response 201**
```json
{
  "userId": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
  "email": "alice@example.com",
  "message": "Registration successful. Please verify your email before signing in."
}
```

**Error Responses**
| Status | Condition |
|---|---|
| 400 | Validation failure (weak password, invalid email format, invalid locale) |
| 409 | Email address is already registered |

---

### POST /api/auth/login

**Summary**: Authenticate with email and password. Returns a short-lived JWT access token and a long-lived refresh token. Updates `Users.LastLoginAt`.

**Auth**: `None`

**Request**
| Field | Type | Required | Notes |
|---|---|---|---|
| `email` | `string` | ✅ | — |
| `password` | `string` | ✅ | — |

**Response 200**
```json
{
  "accessToken": "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9...",
  "refreshToken": "dGhpcyBpcyBhIHJlZnJlc2ggdG9rZW4...",
  "expiresAt": "2026-02-19T08:00:00Z",
  "userId": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
  "role": "Commuter"
}
```

**Error Responses**
| Status | Condition |
|---|---|
| 400 | Missing email or password |
| 401 | Invalid credentials |
| 403 | Account exists but email has not been verified |
| 429 | Too many failed login attempts — rate limited |

---

### POST /api/auth/logout

**Summary**: Invalidate the current refresh token server-side. The client is responsible for discarding the access token.

**Auth**: `Bearer`

**Request**
| Field | Type | Required | Notes |
|---|---|---|---|
| `refreshToken` | `string` | ✅ | The refresh token issued at login |

**Response 204**
```
(no body)
```

**Error Responses**
| Status | Condition |
|---|---|
| 400 | `refreshToken` is missing or malformed |
| 401 | Bearer token invalid or expired |

---

### POST /api/auth/refresh-token

**Summary**: Exchange a valid refresh token for a new access token and rotate the refresh token.

**Auth**: `None` (refresh token is the credential)

**Request**
| Field | Type | Required | Notes |
|---|---|---|---|
| `refreshToken` | `string` | ✅ | Opaque token from prior login or refresh |

**Response 200**
```json
{
  "accessToken": "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9...",
  "refreshToken": "cm90YXRlZCByZWZyZXNoIHRva2Vu...",
  "expiresAt": "2026-02-19T09:00:00Z"
}
```

**Error Responses**
| Status | Condition |
|---|---|
| 400 | `refreshToken` is missing or malformed |
| 401 | Refresh token is expired, revoked, or not found |
