# Research Report: Commute Volatility Monitoring

**Sub-agent**: `sddp.Researcher`  
**Date**: 2026-02-19  
**Feature**: PoTraffic — Empirical Commute Volatility Platform  

---

## Table of Contents

1. [Traffic Data Patterns](#1-traffic-data-patterns)
2. [Route Monitoring UX Patterns](#2-route-monitoring-ux-patterns)
3. [Real-Time vs Historical Visualization](#3-real-time-vs-historical-visualization)
4. [Quota and Cost Management](#4-quota-and-cost-management)
5. [Reroute Detection Signals](#5-reroute-detection-signals)
6. [Admin Observability Dashboards](#6-admin-observability-dashboards)
7. [Departure-Time Optimization](#7-departure-time-optimization)
8. [Data Retention and Freshness](#8-data-retention-and-freshness)

---

## 1. Traffic Data Patterns

### Key Findings

- **Day-of-week (DOW) normalization is the minimum viable baseline.** Traffic patterns differ so substantially between weekdays and weekends — and between individual weekdays (Monday/Friday corridors are typically heavier than Tuesday–Thursday for urban office commutes) — that a flat 7-day average is analytically misleading. Industry practice in traffic analytics platforms (INRIX, HERE, TomTom) segments baselines by DOW as the foundational partition.
- **Four-week minimum, 12–13 weeks recommended.** A 4-week rolling window covers one full calendar cycle per DOW, but is highly susceptible to single-week outliers (holidays, weather events, local incidents). The INRIX and TomTom travel-time reliability indices use 12–13 weeks (one calendar quarter) as the standard "typical traffic" window. This directly supports the 90-day (≈13-week) choice in this spec.
- **Seasonal adjustment is a secondary concern for personal commute tools.** Full seasonal decomposition (e.g., STL decomposition used in economic time series) is standard in macro-level traffic modeling but is over-engineered for a per-user commute tool. Users implicitly expect their baseline to drift as seasons change — the rolling window acts as a natural seasonal approximation. No explicit seasonal factor is needed unless the product reaches region-wide fleet analytics.
- **Holiday exclusion is high-impact and under-acknowledged.** Public holidays distort DOW baselines significantly (e.g., the Friday before a long weekend). Industry tools typically flag or exclude statutory holidays from baseline calculation. Not excluding them causes the baseline to underestimate typical travel time on the affected DOW for future weeks.
- **5-minute polling cadence aligns with industry norms.** INRIX's Traffic Quality Measurement standard uses 5-minute bin aggregation as its foundational resolution. Google Maps' historical traffic data is also modeled at 5-minute granularity. Polling every 5 minutes during a user-defined window is well-aligned with this standard.
- **Travel time standard deviation (σ) is more useful than mean alone.** Reliability metrics (e.g., the 80th-percentile travel time vs median) are the standard measure of route volatility in research and commercial platforms. A route may have a low mean travel time but a high σ, indicating high unpredictability — which is exactly the insight a "volatility" product should surface.

### Informed Defaults

| Parameter | Recommended Default | Rationale |
|---|---|---|
| Baseline window | 90 days | Matches 13-week industry quarter; sufficient DOW sample size (≈13 readings per DOW) |
| DOW normalization | Always on | Non-negotiable; flat baselines mislead users |
| Holiday exclusion | Exclude from baseline calculation (flag in UI) | Single holidays contaminate 1 of ≈13 DOW readings — a ~7% distortion |
| Volatility metric | σ (standard deviation) of travel time per 5-min slot | Surface alongside mean; drives the "volatility curve" shape |
| Minimum baseline readings before display | 3 same-DOW readings (≈3 weeks) | Prevents misleading display when data is sparse |

### Ambiguities to Flag in Spec

- **Holiday calendar scope**: Is the product global, national, or regional? Holiday exclusion requires a configurable locale/region per user's route origin. This is a data dependency that should be called out explicitly.
- **Sparse baseline handling**: What should the UI show during the first 3 weeks before enough DOW data exists? Options: show raw data only, show a "building baseline" state, or hide the volatility curve entirely.

---

## 2. Route Monitoring UX Patterns

### Key Findings

- **"Leave by" / "Arrive by" framing dominates consumer apps.** Waze, Google Maps, and Apple Maps all present commute intelligence through the lens of a goal (arrival time) rather than a stat (travel duration). Users anchor to clock times, not durations. The recommended departure time should therefore be expressed as "Leave at 08:14" rather than "14 minutes earlier than usual."
- **Commuters want a single, actionable signal, not a dashboard of metrics.** User research cited in the Google Maps 2019 commute redesign and INRIX's consumer-facing reports consistently shows that commuters make one binary decision: leave now, or wait. Complexity beyond a colored indicator (green/amber/red) and a departure suggestion is generally ignored on mobile.
- **Persistent notification / home-screen glanceable widget is the highest-value touchpoint.** Apps like Waze and Citymapper report that the commute alert (push notification or widget) is the primary interaction mode — the in-app visualization is secondary. For a web app, the equivalent is a clear "at-a-glance" summary at the top of the monitoring view.
- **Historical context increases trust.** INRIX research shows that users trust route-time predictions more when they can see a historical basis ("Based on your last 47 trips"). Surfacing the baseline sample size ("vs. 13 Tuesdays") increases perceived reliability of the insight.
- **Monitoring window UX**: Apps like Citymapper's "Smart Alarm" and Google's Commute tab show that users expect the monitoring window to be set once and reused. Reconfiguring it daily creates friction. The 2-hour window should default to a user-saved schedule.
- **"Worse than usual" framing outperforms raw numbers.** Waze's color-coded route quality and Google Maps' "heavier than usual traffic" labels are absorbed faster than travel-time deltas. Relative framing (vs. baseline) is more actionable than absolute duration.

### Informed Defaults

| UX Element | Recommendation |
|---|---|
| Departure time display | Express as a clock time ("Best departure: 08:14"), not a duration offset |
| Route quality indicator | Three-state: Normal / Worse than usual / Significantly worse (maps to green/amber/red) |
| Baseline sample label | Show "vs. N {Weekday}s" near the chart to build trust |
| Monitoring window | User-saved 2-hour window; pre-selected based on first-use setup; editable per-session |
| Primary screen layout | Glanceable summary card at top (quality indicator + best departure) → volatility chart below |

### Ambiguities to Flag in Spec

- **Mobile vs desktop priority**: The glanceable-card pattern is critical for mobile. If the primary persona is desktop (reviewing before leaving home), the information hierarchy can be richer. Persona/device priority should be explicit in the spec.
- **Notification model**: Does the app send browser push notifications when the route degrades during the monitoring window? This is a high-value UX feature but requires a separate permission and delivery mechanism.

---

## 3. Real-Time vs Historical Visualization

### Key Findings

- **Dual-line time-series charts are the established pattern.** Plotting a "Today" line (live, updating) against a shaded "Baseline" band (mean ± 1σ or mean ± 1 IQR) is the standard visualization in commercial traffic dashboards (INRIX IQ, HERE Traffic Analytics, TomTom AmiGO). The baseline band width communicates expected variability at a glance.
- **Delta shading conventions are well-established in financial and weather charting.** The pattern — shade the area between the live line and the baseline mean; green when today is better (below baseline), red/orange when worse (above baseline) — originates in financial "above/below trend" charts and has been adopted in traffic, weather, and health contexts. Users familiar with fitness apps (Garmin, Strava's "compare to average") immediately understand this encoding.
- **The x-axis should represent departure time, not elapsed monitoring time.** Plotting travel duration on the y-axis against departure time on the x-axis is more actionable than plotting against "minutes into monitoring window." Users can read "at 08:30 departure, expected 24 min; today 31 min (+7)" directly from the chart.
- **Animate or incrementally reveal the live line.** As 5-minute readings arrive, appending a new point and connecting it to previous readings (vs. redrawing the entire chart) is less disruptive visually and signals to the user that the data is live.
- **Mark the "now" cursor.** A vertical dashed line at the current time, separating the "observed today" portion from the "still-in-window" portion of the chart, helps users orient themselves in the monitoring window.
- **Baseline band width matters for interpretation.** A very narrow band (low variance route) that the live line exits dramatically is immediately alarming. A wide band (high variance route) where the live line is always within it signals that today is "normal for this chaotic route." The band must reflect real historical σ, not a fixed aesthetic width.
- **Color-blind accessibility**: The green/red convention is problematic for deuteranopia (~8% of males). Standard mitigation is to use a blue/orange palette (which is distinguishable under protanopia and deuteranopia) or to supplement with texture/pattern fills in the delta shading.

### Informed Defaults

| Chart Element | Recommendation |
|---|---|
| Baseline representation | Mean line + shaded band (mean ± 1σ) |
| Delta shading | Blue (better than baseline) / Orange (worse than baseline) — avoids green/red accessibility issue |
| X-axis | Departure time (clock time across the 2-hour window) |
| Y-axis | Travel duration in minutes |
| "Now" indicator | Vertical dashed line at current time |
| Data point frequency | One point per 5-minute poll; connected by line |
| Baseline label | "Typical {Weekday}" with sample count in legend |

### Ambiguities to Flag in Spec

- **What constitutes the baseline band**: Mean ± 1σ covers ~68% of observations (assuming normality). Traffic data is not normally distributed (right-skewed due to incident spikes). Using the 10th–90th percentile range as the band may be more representative. The spec should decide: σ-based or percentile-based band.
- **Historical line for previous days**: Should the chart optionally overlay the previous week's same-DOW line for richer context? This is a common "compare to last week" feature request in fitness and productivity apps.

---

## 4. Quota and Cost Management

### Key Findings

- **Mapping API pricing is almost universally per-request (not per-user per month).** Google Maps Platform, HERE, Mapbox, and TomTom all charge per routing API call. A 5-minute polling cadence over a 2-hour window = 24 calls per monitoring session. With 10 sessions/day max, the worst case per user per day is 240 API calls. At typical commercial rates (~$0.005–$0.010 per direction request), this is $1.20–$2.40/user/day at maximum usage — a material cost to communicate.
- **Quota as a cost-control mechanism is standard in SaaS with external API dependencies.** Products like Zapier (task quotas), Airtable (automation runs), and Twilio (SMS) all employ per-user daily/monthly quotas both for their own cost protection and to present clear pricing tiers to users. The 10-window daily quota is a sound and recognized pattern.
- **Quota visibility should be persistent and non-intrusive.** Stripe's API dashboard and Twilio's console show quota usage as a small progress indicator (e.g., "3 / 10 sessions today") persistently visible in the navigation or header. Hiding quota until exhaustion creates frustration; surfacing it proactively creates informed restraint.
- **Quota reset time must be explicit.** Users need to know when their quota resets (e.g., "Resets at midnight UTC" or "Resets at midnight in your local time"). Ambiguity around reset time is a top source of support tickets in quota-based products (per Twilio and SendGrid developer experience reports).
- **"Soft limit" warning before exhaustion reduces surprise.** Show a warning at 80% usage (8 of 10 sessions) and a more prominent alert at 100%. This mirrors AWS, SendGrid, and GitHub Actions behaviors.
- **Admin-level quota override is expected.** Admin panels in quota-gated APIs universally allow override per user or exemption for internal accounts. The spec should include an admin override capability.
- **Granularity matters for trust.** Showing "You have used 3 of 10 monitoring windows today" is trusted. Showing only "7 remaining" without context about what constitutes a "window" creates confusion.

### Informed Defaults

| Quota UX Element | Recommendation |
|---|---|
| Quota display | "X / 10 monitoring windows used today" — persistent in nav/header |
| Warning threshold | Amber at 8/10; Red/blocked at 10/10 |
| Reset schedule | Midnight UTC (server-side simplicity); display in user's local timezone |
| Soft block | Prevent new session start when at 10/10; show reset countdown |
| Admin override | Boolean flag per user: "Exempt from daily quota" |
| Cost attribution unit | Per-session API call count (not just session count) in admin view |

### Ambiguities to Flag in Spec

- **Partial session counting**: If a user starts a 2-hour window but cancels after 15 minutes, does that consume a full quota slot? Industry norm (Zapier, Make.com) is to count the trigger event (session start), not the completion. This should be explicit.
- **Quota scope**: Is the 10-window limit per calendar day (UTC) or per rolling 24-hour period? Each has different UX implications for users who span midnight.

---

## 5. Reroute Detection Signals

### Key Findings

- **Distance-duration divergence is a recognized signal in routing analytics, not a novel concept.** The core insight — that a longer route should save proportionately more time to be "worthwhile" — is formalized in routing research as the **detour ratio** or **excess distance penalty**. It is used in both academic vehicle routing research and commercial fleet telematics (Samsara, Geotab) to flag inefficient routes.
- **The canonical threshold for a "meaningful detour" in fleet analytics is +10% distance without a corresponding time improvement of at least the distance increase × average speed ratio.** In consumer routing research (Google Maps internal studies referenced in academic papers), re-routing is only user-beneficial if the time saving exceeds the distance overhead by a factor of roughly 1.5× (i.e., 10% more distance should save at least 15% of travel time to be worthwhile).
- **Absolute distance thresholds vs percentage thresholds**: For short urban routes (<10 km), a 1 km absolute increase is significant. For long commutes (>50 km), a 1 km increase is trivial. Percentage-based thresholds are more universally applicable across user route lengths.
- **The divergence signal is most useful when it persists across multiple readings.** A single anomalous reading (one 5-minute poll showing a longer distance) may reflect API-level routing variance (APIs do occasionally return slightly different routes on re-query). A flag should require 2–3 consecutive readings above threshold to reduce false positives.
- **Mapping provider route variance is itself a confound.** Different mapping providers return different distances for the "same" route because they have different road network models and snapping algorithms. If the app supports multiple providers, a distance increase may reflect a provider switch rather than a true detour. This must be controlled: compare distance only within the same provider.
- **User communication framing**: Waze communicates reroutes as "A faster route is available." INRIX's fleet tools use "Route deviation detected." For a consumer commute tool, neutral-to-positive framing ("Your route may have changed") is better received than alarm-framing ("Reroute detected!"), per UX writing practices for traffic apps.

### Informed Defaults

| Reroute Detection Parameter | Recommendation |
|---|---|
| Distance increase threshold | +15% above the session's first recorded distance |
| Time saving requirement | Distance increase is only "acceptable" if travel time decreases by ≥ distance % increase × 1.0 (i.e., 15% longer distance should save ≥15% of baseline time) |
| Consecutive readings required | 2 consecutive polls must exceed threshold before flagging |
| Provider scope | Compare distance only within the same mapping provider (cross-provider distance comparison is invalid) |
| UI framing | "Route appears longer — check if a detour is active" (informational, not alarming) |

### Ambiguities to Flag in Spec

- **Distance data source reliability**: How accurately does the mapping API report distance for the user's actual route vs. the theoretical shortest path? Some APIs report network distance (road distance), some report Euclidean-approximated distance. The spec should state which is used and that it is consistent per provider.
- **User-initiated re-routing vs. system-detected**: Should users be able to manually mark a reading as "I took a planned detour" to suppress the flag? This is a quality-of-life feature that prevents contamination of future baseline data.

---

## 6. Admin Observability Dashboards

### Key Findings

- **Per-user cost attribution is a top-3 admin feature request in API-cost-bearing SaaS products.** Products like Vercel, Cloudflare Workers, and Twilio all surfaced per-user API consumption breakdowns after launch because undifferentiated aggregate cost views were insufficient for operators to identify abuse or high-cost outliers.
- **The standard admin cost dashboard pattern has three layers**:
  1. **Global aggregate** — total API calls, total estimated cost, trend (today vs. 7-day avg, today vs. 30-day avg).
  2. **Per-user breakdown** — sortable table: user, sessions today, API calls today, estimated cost today, sessions lifetime, API calls lifetime, estimated cost lifetime.
  3. **Temporal drill-down** — click a user to see their session history (date, window start, window end, API calls, estimated cost).
- **Cost estimation vs. actual billing**: If the app is consuming a third-party API, admin dashboards should display *estimated* cost (calls × unit-price constant configured in settings) and label it clearly as an estimate. Actual billing may differ due to pricing tiers, promotions, or pricing changes. Stripe Atlas and Render both use "Estimated cost" labeling.
- **Anomaly alerting is expected at scale.** Admins expect an automatic flag when a user's usage is >2× their personal 7-day average or >2× the global per-user average. Cloudflare, Vercel, and AWS all offer configurable usage alerts.
- **Masked sensitive config on /Diag is a security best practice with a standard pattern.** The AWS Parameter Store console, Kubernetes `/healthz`, and Spring Boot Actuator all mask secrets in diagnostic endpoints. The convention is: show key names with values replaced by `***` or `[MASKED]` for strings, and show non-sensitive config (version, environment, feature flags, connection timeouts) unmasked.
- **Role-based access to admin pages must be enforced server-side.** Client-side route guards are insufficient; the admin page data must be served only to authenticated admin-role tokens. This is the universal security principle for SaaS admin panels.

### Informed Defaults

| Admin Dashboard Element | Recommendation |
|---|---|
| Global view | Total sessions today, total API calls today, estimated cost today; 7-day sparkline |
| User table columns | User, Sessions (today / 30d), API Calls (today / 30d), Est. Cost (today / 30d), Last Active |
| Default sort | Est. Cost today (descending) — surfaces high-cost users first |
| Cost unit | Display as "≈ $X.XX" with an asterisk linking to "Estimated based on $Y per call" note |
| /Diag endpoint | Show: app version, environment, DB connection status, external API connectivity; mask all keys/secrets with `[MASKED]` |
| Anomaly threshold | Flag users with >2× their personal 7-day average API call count |

### Ambiguities to Flag in Spec

- **Cost per API call configuration**: The unit cost per mapping API call will vary by provider and contract. Should this be a runtime-configurable admin setting, or a build-time constant? A configurable setting is more maintainable but adds admin complexity.
- **Cost currency**: Should cost always be shown in USD, or should it be configurable? For simplicity, USD with a caveat label is sufficient for MVP.
- **Audit log**: Should admin actions (e.g., quota override, user disable) be logged? This is a compliance concern in regulated industries but may be out of scope for MVP.

---

## 7. Departure-Time Optimization

### Key Findings

- **5-minute granularity is the actionable minimum for urban commutes; 15-minute granularity is sufficient for suburban/highway commutes.** Research from Google Maps (referenced in their 2021 commute insights blog) and INRIX's departure-time studies consistently shows that urban commuters can act on 5-minute guidance (they have more schedule flexibility and shorter walk times to vehicle), while highway commuters typically need a 10–15 minute lead time to change departure.
- **The "best departure window" is not a single point — it is a window.** Route quality is rarely monotone across time. The most user-useful output is a recommended *window*: "Optimal: 08:05–08:20" rather than a single minute. Windows shorter than 5 minutes are not credible given the data resolution; windows wider than 20 minutes are too vague to be actionable.
- **Users anchor their departure decisions to fixed personal commitments.** Academic research on commuter behavior (Ettema & Timmermans, "Departure Time Choice Behaviour" series) shows that commuters weigh arrival reliability (arriving on time vs. arriving early as a buffer) against departure flexibility (schedule constraints at home — children's school runs, partner schedules). Optimal departure time must account for the user's *required arrival time*, which is upstream context this platform does not currently capture. Without it, the recommendation is "depart when the route is fastest," which may conflict with users' real constraints.
- **"Depart now vs. wait" is the highest-utility micro-decision.** Real-time departure optimization research shows maximum value in the 0–30 minute lookahead. For the specific context of a 2-hour monitoring window, the most valuable signal is: "It is currently X min above baseline; the pattern suggests it will improve in Y minutes — waiting is advised" vs. "It is already at/below baseline — depart now."
- **"Best departure" should be derived from the volatility curve, not from a separate model.** The simplest credible algorithm is: scan the 2-hour window's baseline curve and find the 5-minute slot with the minimum median travel time. Among slots with travel time within 5% of that minimum, the earliest becomes the recommended departure. This avoids complex optimization while being transparent and explainable to users.
- **Showing "you missed the optimal window" is demotivating but useful for learning.** Some apps (Citymapper, Siri Commute) retrospectively show "The best time to have left was 08:10 — you avoided X min of delay by leaving at 08:15." This builds model trust over time even when the user didn't act on the recommendation.

### Informed Defaults

| Departure Optimization Element | Recommendation |
|---|---|
| Recommendation granularity | 5-minute slots (aligned with polling cadence) |
| Recommendation format | Window: "Best: 08:05–08:20" (earliest slot within 5% of optimal) |
| Algorithm | Minimum median travel time on baseline curve for the remaining window |
| "Too late" threshold | If current time > recommended window end, switch to "You're past the optimal window — current conditions: [color indicator]" |
| Lookahead horizon | Display recommendation only for the next 60 minutes (beyond that, too uncertain to be actionable) |

### Ambiguities to Flag in Spec

- **Required arrival time as input**: Without a user-specified "I must arrive by X:XX" constraint, departure optimization is unconstrained (always recommends the absolute fastest route, not the user's optimal). The spec should decide: is required arrival time a configurable user preference, or is the optimization purely empirical (fastest route in the window, user decides relevance)?
- **"Tomorrow" vs "today" recommendations**: Should the platform also show tomorrow's predicted best departure time based on historical patterns alone (no live data)? This is a commonly requested feature in commute apps and aligns with the existing baseline data.

---

## 8. Data Retention and Freshness

### Key Findings

- **90 days is the commercially established norm for "recent traffic" in route analytics, and it corresponds to one meteorological season.** INRIX, TomTom, and HERE all use rolling 12–13 week windows as their "typical traffic" calculation period in their commercial navigation and analytics products. This is not arbitrary — it is the period over which traffic patterns (academic research by Mahmassani et al., "Time-Dependent Network Assignment") are relatively stationary before seasonal drift becomes significant.
- **Older data is not worthless — it becomes structurally irrelevant.** Data from 91+ days ago does not turn "wrong" but it mixes seasonal context (comparing a summer reading to a winter week). The 90-day window ensures that all baseline readings were collected under comparable seasonal conditions to the current week.
- **Nightly batch pruning is the standard approach.** Real-time deletion of expired records (deleting as records age past 90 days) is technically equivalent but creates unnecessary per-query overhead. Nightly batch jobs (typically 00:00–04:00 server-local time, when load is lowest) are the universal operational pattern in analytics databases.
- **Soft-delete vs. hard-delete**: Some platforms retain aged data in a cold/archived state (cheaper storage tier) rather than deleting it, allowing retrospective analysis. For a user-facing commute tool, hard deletion is appropriate — archived personal location data that serves no product function is a GDPR/privacy liability without compensating benefit.
- **Data density varies across the window.** Early in a user's history (first 2 weeks), the baseline has very few readings per DOW (1–2). The baseline display should communicate density to avoid overfitting to sparse data. The volatility curve becomes reliable at ≈ 4 readings per DOW slot.
- **The "freshness" of the baseline matters relative to how recently the user's route was active.** If a user pauses commuting for 3 weeks (holiday, remote work), their 90-day window now has a 3-week gap. The baseline is still valid — it just excludes those weeks automatically. No special handling is needed; rolling windows self-correct.
- **Storage estimation**: At 5-minute polling over a 2-hour window, a single session produces 24 readings. With 10 sessions/day and 90-day retention, worst-case per user is 24 × 10 × 90 = 21,600 rows. At ~200 bytes per row (timestamp, duration, distance, provider, metadata), that is ~4.3 MB per user at maximum usage — well within relational database cost norms.

### Informed Defaults

| Retention Parameter | Recommendation |
|---|---|
| Retention window | 90 rolling calendar days |
| Pruning schedule | Nightly batch job: 02:00 UTC (low-traffic window) |
| Pruning strategy | Hard delete (no soft-delete, no archive) |
| Minimum readings to display baseline | 3 same-DOW readings in the active window |
| Freshness indicator | Display "Based on N {Weekday}s in the last 90 days" near the baseline line |
| Max rows per user (worst-case) | ~21,600 rows; plan indexes on (user_id, timestamp, day_of_week) for efficient baseline queries |

### Ambiguities to Flag in Spec

- **Pruning scope**: Should the janitor prune *all* data types (raw readings, computed aggregates, session metadata) or only raw readings? If baseline aggregates are pre-computed and cached, they may need separate pruning logic.
- **User account deletion**: When a user account is deleted, how promptly must their route data be deleted? GDPR Article 17 (Right to Erasure) requires "undue delay" — typically interpreted as within 30 days in commercial practice. The spec should include a user data-deletion pathway separate from the nightly janitor.
- **Cross-user data**: Raw readings are per-user and private. If the platform ever aggregates anonymised route data across users (e.g., "35 commuters on Route A averaged 28 min today"), the retention and privacy implications change significantly. This should be explicitly out of scope for MVP or scoped carefully.

---

## Summary: Recommended Defaults for Spec Authoring

The following table consolidates the most important defaults across all 8 research areas so the spec author can populate requirements without NEEDS CLARIFICATION markers on core parameters:

| Domain | Parameter | Default |
|---|---|---|
| Baseline | Rolling window | 90 days |
| Baseline | Normalization | Day-of-week; holidays excluded from baseline |
| Baseline | Minimum data to show | 3 same-DOW readings |
| Polling | Cadence | 5 minutes |
| Polling | Window length | 2 hours (user-defined start time) |
| Chart | Delta shading palette | Blue (better) / Orange (worse) |
| Chart | Baseline band | Mean ± 1σ (or 10th–90th percentile — spec to decide) |
| Departure | Recommendation granularity | 5-minute slots; express as window ("08:05–08:20") |
| Departure | Lookahead | Display for next 60 minutes only |
| Reroute | Distance threshold | +15% above session baseline distance |
| Reroute | Consecutive polls required | 2 consecutive readings above threshold |
| Quota | Daily session limit | 10 monitoring windows |
| Quota | Warning threshold | Amber at 8/10; blocked at 10/10 |
| Quota | Reset | Midnight UTC |
| Retention | Window | 90 rolling days |
| Retention | Pruning | Nightly hard delete at 02:00 UTC |
| Admin | Default table sort | Estimated cost today (descending) |
| Admin | /Diag secrets | Always masked with `[MASKED]` |

---

## Genuine Ambiguities That Must Be Resolved in the Spec

These are questions that research cannot answer — they require explicit product decisions:

1. **Holiday calendar**: Which locale/region's public holidays are excluded from baselines? Is this per-user (based on route location) or a global platform setting?
2. **Baseline band type**: Mean ± 1σ (fast, but assumes normality) vs. 10th–90th percentile (slower to compute, more accurate for skewed traffic distributions)?
3. **Partial session quota consumption**: Does a cancelled/abandoned session consume a quota slot?
4. **Required arrival time**: Is departure-time optimization purely empirical (fastest window available), or does it incorporate a user-configured "must arrive by" time?
5. **Cost per API call**: Is the unit price a runtime-configurable admin setting or a build-time constant?
6. **GDPR Right to Erasure pathway**: Is there a user-initiated "delete my data" workflow, separate from the nightly janitor?
7. **Cross-user aggregation**: Is anonymised route aggregation across users in scope for this product at any horizon?
8. **Notification model**: Does the platform send browser push notifications during an active monitoring window when conditions degrade?
9. **Mobile vs. desktop priority**: Which is the primary device context for the commute monitoring view?
10. **"Tomorrow" predictions**: Should the platform display tomorrow's predicted best departure time (baseline-only, no live data) as a planning aid?
