# PoTraffic API Specification (Authoritative)

The full, granular API contract is maintained within the core specification folder to ensure consistency between requirements and implementation:

👉 [**Full API Contract Inventory**](../specs/00001-potraffic-core/contracts/index.md)

### Available Modules
*   [Authentication](../specs/00001-potraffic-core/contracts/auth.md)
*   [Account Management](../specs/00001-potraffic-core/contracts/account.md)
*   [Route Management](../specs/00001-potraffic-core/contracts/routes.md)
*   [History & Analytics](../specs/00001-potraffic-core/contracts/history.md)
*   [System & Maintenance](../specs/00001-potraffic-core/contracts/system.md)
*   [Admin Observability](../specs/00001-potraffic-core/contracts/admin.md)

### Error Handling Policy (RFC 7807)
All errors return a structured **ProblemDetails** response:

| Status Code | Description | Scenario |
|---|---|---|
| 400 | Bad Request | Logic errors or malformed JSON |
| 401 | Unauthorized | Missing or invalid JWT token |
| 403 | Forbidden | Insufficient permissions (e.g., non-admin accessing dashboards) |
| 422 | Unprocessable Entity | **FluentValidation** failure (includes per-field errors) |
| 409 | Conflict | Resource already exists (e.g., duplicate route origin/destination) |
| 500 | Internal Server Error | Unhandled exceptions (logged to Serilog) |
