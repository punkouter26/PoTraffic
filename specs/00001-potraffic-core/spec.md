# Feature Specification: PoTraffic — Empirical Commute Volatility Engine

**Feature Branch**: `00001-potraffic-core`
**Created**: 2026-02-19
**Status**: Draft
**Input**: User description: "PoTraffic — The Empirical Commute Volatility Engine"

---

## User Scenarios & Testing *(mandatory)*

---

### User Story 1 — Monitor a Route Automatically (Priority: P1)

A commuter defines a route (start address, end address) and assigns a recurring monitoring window (e.g., 7:00 AM – 9:00 AM on weekdays). Once activated, the system silently polls the route every five minutes throughout the window, recording the travel duration, distance, mapping provider used, and exact timestamp for each poll. The commuter does not need to be present or interact with the app for data to be collected.

**Why this priority**: This is the entire data-collection backbone of the platform. Every other feature — the volatility chart, optimal departure time, historical baseline — depends entirely on this engine producing timestamped, accurate readings. Without it, there is no product.

**Independent Test**: Can be validated end-to-end by creating a route with a window, waiting for the window to activate, and verifying that new poll records appear in persistent storage every five minutes for the duration of the window, without any manual user interaction.

**Acceptance Scenarios**:

1. **Given** a user has saved a route with a monitoring window of 7:00 AM – 9:00 AM, **When** the clock reaches 7:00 AM on a scheduled day, **Then** the system automatically begins polling and records the first data point within 30 seconds of the window start.
2. **Given** the monitoring window is active, **When** five minutes elapse since the last poll, **Then** the system records a new data point capturing: exact UTC timestamp, travel duration (minutes), travel distance (miles/km), and the name of the mapping provider that returned the data.
3. **Given** the monitoring window is active, **When** the clock reaches the window end time, **Then** polling stops automatically and no further data is recorded for that session.
4. **Given** a user has already consumed all 10 of their daily monitoring windows, **When** the start time of another window arrives, **Then** that window is skipped and the user is informed that their daily quota is exhausted.
5. **Given** a mapping provider returns an error for a given poll, **When** the system cannot retrieve a valid duration/distance, **Then** the system logs the failure, skips that data point, and retries at the next scheduled interval without disrupting the session.

---

### User Story 2 — View the Volatility Dashboard (Priority: P2)

A commuter opens the app and immediately sees their active or most recent monitoring session as a dual-line chart. One line traces today's actual duration readings over time; the other shows the historical baseline — the average duration for the same route on the same day of the week over the past 90 days. The area between the two lines is shaded to indicate whether today is tracking above or below the historical average. The dashboard also displays the calculated optimal departure time and the user's remaining daily quota.

**Why this priority**: This is the primary consumer of all collected data and the core user-facing value proposition. A commuter might never visit Route Management or Admin — but they will open the Dashboard every morning. Without it, the data collected in P1 has no actionable outlet.

**Independent Test**: Can be validated by seeding a route with both historical poll records (spanning multiple same-weekday sessions) and a current-session set of records, then verifying the chart renders two distinct lines, the delta shading appears correctly colored relative to whether today is faster or slower, and the optimal departure time displayed matches the historical minimum for that route.

**Acceptance Scenarios**:

1. **Given** a monitoring session is in progress, **When** each new poll is recorded, **Then** the "Today's Actual" line on the chart updates in real time (or within one minute) without requiring a manual page refresh.
2. **Given** sufficient historical data exists (at least 3 prior same-weekday sessions), **When** the dashboard loads, **Then** the "Historical Baseline" line and its ±1σ variance band are displayed, calculated from mean and standard deviation at each 5-minute interval across all qualifying prior sessions within the 90-day window.
3. **Given** today's recorded duration at a given interval is higher than the historical average, **When** the chart renders, **Then** the delta area between the two lines is shaded in the designated "Tax" color (indicating a worse-than-normal commute).
4. **Given** today's recorded duration at a given interval is lower than the historical average, **When** the chart renders, **Then** the delta area is shaded in the designated "Bonus" color (indicating a better-than-normal commute).
5. **Given** there is insufficient historical data (fewer than 3 prior same-weekday sessions), **When** the dashboard loads, **Then** the historical baseline line and variance band are omitted and a clear message explains that at least 3 sessions are needed before a reliable baseline can be shown.
6. **Given** the dashboard is displayed, **When** the user views the quota indicator, **Then** they can see exactly how many of their 10 daily windows have been consumed, with a visual warning when 8 or more have been used.
7. **Given** a poll data point has been flagged as a reroute, **When** the user hovers over or taps that point on the chart, **Then** a tooltip displays both the recorded duration and an indication that the mapping provider suggested a significantly longer route than usual, including the observed distance increase.

---

### User Story 3 — Manage Routes and Perform Quick Checks (Priority: P3)

A commuter adds, edits, and removes their saved routes via a compact administrative interface. Each route has a start and end address (verified for precision), an assigned monitoring window (days and times), and a selected mapping provider. The user can also perform a one-off "Check Now" query that returns the current travel duration for any saved route instantly, without consuming a monitoring window or persisting data.

**Why this priority**: Route Management is a prerequisite for the monitoring engine to operate on persistent, correctly-defined routes. "Check Now" adds significant immediate utility without data cost. However, the core scheduled engine (P1) can operate once at least one route has been configured, so this story supports and enables P1 but is not itself the most critical path.

**Independent Test**: Can be validated by adding a route with an unverified address, triggering address verification, confirming the standardized address is stored, then performing a "Check Now" and verifying a duration result appears as a transient notification without a new database record being created.

**Acceptance Scenarios**:

1. **Given** a user enters a free-text address during route creation or editing, **When** they trigger address verification, **Then** the system communicates with the configured mapping service, returns a formatted, standardised address, and pre-fills the address field with the result before saving.
2. **Given** a user attempts to save a route with an address that cannot be verified, **When** verification fails, **Then** the system prevents saving and shows a clear message asking the user to correct the address.
3. **Given** a user has saved routes, **When** they view the Route Management page, **Then** they see a compact grid showing each route's origin, destination, monitoring window days/times, and assigned provider in a single row without horizontal scrolling on desktop.
4. **Given** a user clicks the "Check Now" button on a saved route, **When** the query completes, **Then** a transient on-screen notification displays the current travel duration and distance; no record is written to the database and no daily quota is consumed.
5. **Given** a user selects a different mapping provider for a route, **When** they save the change, **Then** all future monitoring polls and "Check Now" requests for that route use the newly selected provider.
6. **Given** a user deletes a route, **When** the deletion is confirmed, **Then** the route and all its associated historical poll records are permanently removed and the route no longer appears in any list or chart.

---

### User Story 4 — Automated Data Maintenance and Reroute Intelligence (Priority: P4)

The system automatically maintains data quality without user intervention. A nightly process removes poll records older than 90 days across all users, ensuring that baselines always reflect current route infrastructure. Simultaneously, each incoming poll record is evaluated: if the reported distance has increased significantly compared to the session average, the record is flagged as a suspected reroute so that users can apply their own judgment when interpreting spikes in the volatility chart.

**Why this priority**: These are background integrity mechanisms. Users are not directly aware of them, but without them the baseline becomes statistically polluted over time and reroute spikes become misleading noise. They are behind-the-scenes correctness guarantors, not prominent features, hence P4.

**Independent Test**: Can be validated by injecting poll records with timestamps 91+ days old, triggering the maintenance process manually (or waiting for the scheduled run), and verifying those records are deleted. Reroute detection can be tested by injecting a sequence of poll records with stable distance followed by one record with a distance increase greater than the threshold, and asserting that the last record is marked as a reroute in the database.

**Acceptance Scenarios**:

1. **Given** poll records exist that are older than 90 calendar days, **When** the nightly maintenance process runs, **Then** all records older than 90 days are permanently deleted and the row count reduction is logged.
2. **Given** the nightly process runs, **When** it completes, **Then** no records from the last 90 days are affected or deleted.
3. **Given** a route has an established session distance baseline, **When** a new poll record arrives with a distance that is 15% or more greater than the session's median distance, **Then** the system flags that record as a "suspected reroute."
4. **Given** a reroute flag requires two consecutive elevated-distance readings to confirm (to prevent single-poll noise triggering false positives), **When** only one anomalous reading is followed by a return to normal distance, **Then** the flag is cleared and no reroute is recorded.

---

### User Story 5 — Administer the System and View Diagnostics (Priority: P5)

A user with the Administrator role can view a system-wide usage table showing every registered user's poll count for the current day and the estimated operational cost attributable to their activity. The admin can also view aggregated volatility data across all users to identify macro-traffic events (e.g., a regional storm affecting the entire user base). A dedicated diagnostics page displays all system configuration keys and values — with any sensitive values partially masked — for health verification without exposing secrets.

**Why this priority**: Admin tooling is essential for ongoing operation and cost governance but delivers no direct commuter value. It enhances the product's viability as a multi-user SaaS platform but does not affect the core commute-intelligence flow for regular users.

**Independent Test**: Can be validated by creating two test user accounts, each generating a known number of poll events, then verifying the Admin usage table shows the correct counts and computed cost per user. Diagnostics can be tested by configuring a known set of keys (including one marked sensitive), loading the /Diag page, and asserting the sensitive key's middle characters are replaced with asterisks.

**Acceptance Scenarios**:

1. **Given** an admin is logged in and navigates to the Admin page, **When** the usage table loads, **Then** it lists every registered user with their today's poll count and a calculated cost estimate based on the system's configured cost-per-poll rate.
2. **Given** the admin views the global aggregation section, **When** there are multiple users with active sessions on the same route corridor, **Then** the system presents an aggregated volatility view that surfaces duration patterns across all users, not just one.
3. **Given** the admin navigates to the /Diag page, **When** the configuration list is rendered, **Then** all values that are classified as sensitive (API keys, secret tokens, connection strings) have characters 3 through N-2 masked with asterisks, showing only the first two and last two characters (e.g., `AK****99`).
4. **Given** a non-admin user attempts to access the Admin or /Diag pages, **When** the request is made, **Then** they are denied access and redirected to an appropriate "access denied" view.

---

### Edge Cases

- What happens when both addresses in a route are identical? System must reject the route at creation time with a clear validation message.
- What happens when the monitoring window spans midnight (e.g., 11:00 PM – 1:00 AM)? System must correctly handle cross-midnight windows and attribute date-of-week to the window's start time.
- What happens when a user's daily quota resets at midnight but a window started before midnight is still running? The in-progress window completes normally; quota reset applies to new windows only.
- What happens if the system is offline or restarted during an active monitoring window? System must resume any windows that are still within their scheduled time range upon recovery.
- What happens when the mapping provider returns the same duration but a dramatically shorter distance? This should not trigger a reroute flag; the threshold is one-directional (increase only).
- What happens when fewer than 12 data points (1 hour) exist in a current session? The dashboard must still render the partial "Today's Actual" line with the data available.
- What happens when two routes share the same start/end but differ only in assigned windows? They are treated as distinct routes with independent historical pools.
- What happens when a historical data point from 89 days ago represents a public holiday with anomalously low traffic? Public holidays for the user's configured locale MUST be excluded from the same-day-of-week historical baseline calculation. Users must set a locale/country in their profile so the system knows which holidays apply. This requires a locale setting on the User entity.
- What happens when a user deletes their account? All routes, monitoring windows, sessions, and poll records MUST be permanently hard-deleted immediately in a cascading deletion. A clear confirmation prompt MUST be shown before any destruction occurs, and the operation is irreversible.

---

## Requirements *(mandatory)*

### Functional Requirements

**Monitoring Engine**

- **FR-001**: System MUST automatically poll a saved route at 5-minute intervals for the duration of the defined monitoring window, without requiring any user interaction during the window.
- **FR-002**: System MUST record the following data for every successful poll: exact UTC timestamp, travel duration in minutes, travel distance in the user's preferred unit, and the identity of the mapping provider used.
- **FR-003**: System MUST enforce a maximum of 10 monitoring windows per user per calendar day (resetting at midnight UTC). A quota slot is consumed at the exact moment a monitoring window activates, regardless of whether the session is subsequently cancelled, experiences failures, or produces no data.
- **FR-004**: System MUST skip and notify the user when a window would exceed the daily quota limit.
- **FR-005**: System MUST log and gracefully skip individual failed polls without terminating the monitoring session.
- **FR-006**: System MUST flag a poll record as a "suspected reroute" (stored as `IsRerouted = true` on the `PollRecord`) when the reported distance exceeds the session median distance by 15% or more AND is confirmed by a second consecutive elevated reading.

**Volatility Dashboard**

- **FR-007**: System MUST display a dual-series chart with two series: "Today's Actual" (live, updated each poll) and "Historical Baseline" (mean duration per 5-minute interval across all qualifying prior sessions for the same route and day-of-week within the 90-day window). The Historical Baseline MUST be accompanied by a shaded variance band representing mean ± 1 standard deviation (±1σ) at each interval, allowing users to distinguish days that are unusually different from days that fall within normal expected variation.
- **FR-008**: System MUST shade the delta area between the two chart lines using the designated "Tax" colour when today's duration is above the baseline and the "Bonus" colour when below.
- **FR-009**: System MUST display an "Optimal Departure Window" below the chart: a range of contiguous 5-minute slots whose historical average duration falls within 5% of the minimum for that route and weekday, expressed as a clock-time range (e.g., 'Best: 08:05–08:20'). The recommendation is purely empirical — no arrival-time constraint — and represents the historically fastest period within the monitoring window. When qualifying slots are non-contiguous, only the longest contiguous run is reported.
- **FR-010**: System MUST display a persistent quota indicator showing consumed / total (10) daily windows, with a prominent warning when 8 or more windows have been used.
- **FR-011**: System MUST display a tooltip on suspected-reroute data points exposing the flagged distance increase beside the recorded duration.
- **FR-012**: System MUST display a clear message on the dashboard when fewer than three prior same-weekday sessions exist for the selected route. The Historical Baseline line and variance band MUST NOT be rendered until this three-session threshold is met.

**Route Management**

- **FR-013**: System MUST verify entered addresses against the configured mapping service before allowing a route to be saved, substituting the standardised formatted address.
- **FR-014**: System MUST prevent saving a route whose origin and destination resolve to the same coordinates.
- **FR-015**: System MUST allow users to assign one of the available mapping providers to each individual route.
- **FR-016**: System MUST provide a "Check Now" function that queries the current travel duration and distance for a route and returns the result as a transient notification, without persisting any data or decrementing the daily quota.
- **FR-017**: System MUST display saved routes in a compact grid showing at minimum: origin, destination, window days/times, and assigned provider.
- **FR-018**: System MUST allow users to add and edit routes without leaving the Route Management view; the interaction MUST not require navigating to a separate page.

**Data Maintenance**

- **FR-019**: System MUST run a nightly data-pruning process that permanently deletes all poll records older than 90 calendar days.
- **FR-020**: System MUST log the count of records deleted during each pruning run.
- **FR-021**: The historical baseline MUST be computed exclusively from records within the rolling 90-day window. Records captured on public holidays recognised in the user's configured locale MUST be excluded from baseline calculations.

**Admin & Diagnostics**

- **FR-022**: System MUST restrict the Admin page and /Diag page to users with the Administrator role.
- **FR-023**: The Admin usage table MUST display, for each registered user: user identifier, total polls executed today, and estimated cost (calculated using the per-provider configurable cost-per-poll rate stored in SystemConfiguration for each poll's recorded provider).
- **FR-024**: System MUST provide a global volatility aggregation view on the Admin page that combines poll data across all users.
- **FR-025**: The /Diag page MUST display all application configuration entries as formatted key-value pairs.
- **FR-026**: System MUST mask sensitive configuration values on the /Diag page, showing only the first two and last two characters with all intermediate characters replaced by asterisks.

**Authentication & Authorisation**

- **FR-027**: System MUST require authenticated sessions to access any feature; unauthenticated users are redirected to sign-in.
- **FR-028**: System MUST support at least two roles: standard Commuter and Administrator, each with appropriately scoped access.
- **FR-029**: System MUST provide a self-service registration flow accessible to the public, allowing any visitor to create a Commuter account via an email address and password, subject to email verification before access is granted. **MVP scope**: no SMTP transport is required; the email verification token is emitted to the application log at `Information` level (Serilog console sink) so developers can confirm the token during local testing. The token-based confirmation endpoint is implemented and preserved for future email-provider integration. The Administrator role is pre-seeded and is not available via self-registration.

**Notifications**

- **FR-030**: All departure guidance (optimal departure window, delta shading, quota warnings) MUST be delivered passively through the in-app dashboard only. The system MUST NOT send proactive push notifications or emails; users are expected to open the application to review their commute intelligence.

**Data Rights**

- **FR-031**: System MUST provide a self-service 'Delete Account' function that, upon confirmed user action, permanently and immediately hard-deletes all data associated with that user — profile, routes, monitoring windows, sessions, and poll records — with no possibility of recovery.

**Design Context**

- **FR-032**: The Volatility Dashboard and Route Management views MUST be designed with a desktop web browser as the primary viewport (minimum 1280px). Mobile browsers (≥375px) MUST receive a fully functional experience with no features blocked, but layout and interaction density may be simplified for smaller screens.

---

### Key Entities *(include if feature involves data)*

- **User**: A registered commuter or administrator. Attributes: identity, role (Commuter / Administrator), daily window count, preferred distance unit, locale/country (for public holiday exclusion).
- **Route**: A saved origin-destination pair owned by a user. Attributes: display name, verified origin address & coordinates, verified destination address & coordinates, assigned mapping provider, active flag. A user may have multiple routes.
- **MonitoringWindow**: A recurring schedule attached to a Route. Attributes: days of week, start time, end time. Drives when the polling engine activates. A Route has one active window definition.
- **PollRecord**: A single telemetry reading from one poll. Attributes: route reference, UTC timestamp, duration (minutes), distance (km or miles), mapping provider used, reroute-suspected flag. Many PollRecords belong to a Route.
- **MonitoringSession**: A logical grouping of PollRecords collected within a single window activation. Attributes: route reference, session date, start time, end time, poll count, status (active / completed / quota-blocked). Supports reroute median calculation.
- **SystemConfiguration**: Key-value store of application settings, each tagged with a sensitivity flag that drives /Diag masking. Includes per-provider cost-per-poll entries for each supported mapping provider (e.g., `cost.googlemaps`, `cost.here`, `cost.mapbox`), enabling accurate per-user cost attribution in the Admin usage table.

---

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: A commuter can set up a new monitored route — from entering addresses through to the window being scheduled — in under 3 minutes.
- **SC-002**: The Volatility Dashboard loads and renders both chart lines within 3 seconds on a standard broadband connection when historical data is available.
- **SC-003**: The "Today's Actual" line on the dashboard updates to reflect the latest poll within 60 seconds of that poll being recorded.
- **SC-004**: Reroute detection correctly flags, and does not incorrectly flag, at least 95% of reroute events in a controlled test dataset with known reroutes and normal variations.
- **SC-005**: The nightly pruning process completes within 5 minutes regardless of whether the user base generates the maximum theoretical volume of poll records over 90 days.
- **SC-006**: The "Optimal Departure Time" displayed on the dashboard matches the historically lowest-average-duration interval for that route on that day of week, calculated from all qualifying records in the 90-day window.
- **SC-007**: A "Check Now" request returns a result within 5 seconds under normal network conditions.
- **SC-008**: The Admin usage table reflects poll activity within 5 minutes of it occurring, ensuring cost visibility is near-real-time.
- **SC-009**: No sensitive configuration value is ever displayed in plain text on the /Diag page; masking is applied to 100% of entries marked as sensitive.
- **SC-010**: All data collected for a given user is attributable only to that user; no cross-user data leakage occurs in any view, chart, or API response (except explicitly in the Admin aggregation view accessible only to Administrators).

---

## Compliance Check

| Principle | Status | Notes |
|---|---|---|
| I. Zero-Waste Codebase | ✅ PASS | Spec is well-scoped with no redundant requirements, dead entities, or duplicate concerns. `NEEDS CLARIFICATION` markers are used precisely. |
| II. SOLID & GoF Design | ⚠ WARN | The polling engine (US1, FR-001–FR-006) bundles scheduling, external-API invocation, persistence, and reroute detection within one conceptual unit. Plan phase must decompose into SRP-compliant services and name GoF patterns (Strategy for provider dispatch, Observer/Mediator for poll events). |
| III. Test Coverage | ⚠ WARN | Background polling and nightly pruning (US4) are difficult to exercise via Playwright E2E as written. Plan must define a concrete E2E strategy (e.g., test-only trigger endpoint in the Testing environment profile). |
| IV. Vertical Slice Architecture | ⚠ WARN | `PollRecord`, `MonitoringSession`, `Route`, `MonitoringWindow`, and `SystemConfiguration` are consumed by multiple slices. Plan must explicitly place them in `src/Shared/` or `src/Infrastructure/` and prohibit per-slice re-declaration. |
| V. Fixed Technology Stack | ⚠ WARN | Two gaps require planning decisions: (a) 60-second dashboard update SLA — Blazor WASM timer polling (no stack change) vs. SignalR (requires documented amendment); (b) background scheduling — confirm `IHostedService`/`BackgroundService` suffices, or enumerate any scheduler package requiring a stack amendment. |

**Overall Verdict: ⚠ WARN** — No principle is contradicted by the spec. Four planning decisions are required to prevent implementation violations:
1. **Principle II**: Decompose the polling engine into `IScheduleEvaluator`, `IRoutePoller`, `IPollPersistence`, `IRerouteDetector`; name GoF patterns in plan.
2. **Principle III**: Define E2E test strategy for background and scheduled processes via a test-environment-only trigger mechanism.
3. **Principle IV**: Declare shared entities in `src/Shared/` or `src/Infrastructure/`; prohibit local duplication in feature slices.
4. **Principle V**: Resolve real-time update strategy and background scheduler choice before implementation begins.

---

## Clarifications

### Session 2026-02-19

- **Q: Is there a user-triggered Right to Erasure (GDPR Art. 17) pathway?** → **A: Self-service hard-delete** — 'Delete Account' cascades immediate permanent deletion of all user data. Applied to: new edge case, new FR-031.
- **Q: When is a daily quota slot consumed — on session start, first poll, or proportionally?** → **A: On session start (moment the window activates)** — standard SaaS quota semantics; prevents gaming. Applied to: FR-003.
- **Q: Should Optimal Departure Time be constrained by a user-defined arrival time?** → **A: Purely empirical** — no arrival-time constraint for MVP; user interprets relevance. Applied to: FR-009.
- **Q: Should the Historical Baseline display a single mean line or a variance band?** → **A: Mean + ±1σ shaded band** — adds STDDEV to existing AVG aggregation; substantially improves chart interpretability. Applied to: FR-007, US2-S2, US2-S5.
- **Q: What is the primary device viewport for the Dashboard and Route Management?** → **A: Desktop primary (1280px+)** — mobile is a fully functional fallback. Applied to: new FR-032.
- **Q: Should Optimal Departure Time be a single slot or a time window?** → **A: Time window** — all 5-min slots within 5% of the historical minimum, expressed as a clock-time range (e.g., 'Best: 08:05–08:20'). Applied to: FR-009.
- **Q: Should Admin cost estimation use one global rate or per-provider rate?** → **A: Per-provider configurable rate** — one SystemConfiguration entry per provider for accurate cost attribution. Applied to: FR-023, SystemConfiguration entity.
- **Q: What is the minimum number of prior same-weekday sessions before the baseline renders?** → **A: 3 sessions** — research recommendation; one anomaly = 33% of baseline (vs. 50% at 2 sessions). Applied to: FR-012, US2-S2, US2-S5.
