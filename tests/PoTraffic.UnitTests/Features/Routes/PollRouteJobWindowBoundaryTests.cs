// filepath: tests/PoTraffic.IntegrationTests/Unit/Features/Routes/PollRouteJobWindowBoundaryTests.cs
using System;
using System.Reflection;
using FluentAssertions;
using PoTraffic.API.Features.MonitoringWindows;
using PoTraffic.API.Features.Routes;
using Xunit;

namespace PoTraffic.UnitTests.Features.Routes;

/// <summary>
/// Boundary tests for the static window predicates on <see cref="PollRouteJob"/>.
/// Ensures the self-scheduling chain (a) samples during the window, (b) sleeps
/// until the next window start when outside it, and (c) stops if no day is enabled.
/// </summary>
public sealed class PollRouteJobWindowBoundaryTests
{
    private const byte Mon = 0x01;
    private const byte Tue = 0x02;
    private const byte Wed = 0x04;
    private const byte Thu = 0x08;
    private const byte Fri = 0x10;
    private const byte Sat = 0x20;
    private const byte Sun = 0x40;
    private const byte AllDays = 0x7F;
    private const byte Weekdays = 0x1F;

    // Use reflection so we exercise the private static methods without making
    // them part of the public API surface.
    private static readonly MethodInfo IsWithinWindowMethod =
        typeof(PollRouteJob).GetMethod("IsWithinWindow", BindingFlags.Static | BindingFlags.NonPublic)!;

    private static readonly MethodInfo NextWindowStartMethod =
        typeof(PollRouteJob).GetMethod("NextWindowStart", BindingFlags.Static | BindingFlags.NonPublic)!;

    private static bool IsWithin(MonitoringWindow w, DateTimeOffset now) =>
        (bool)IsWithinWindowMethod.Invoke(null, [w, now])!;

    private static DateTimeOffset? NextStart(MonitoringWindow w, DateTimeOffset now) =>
        (DateTimeOffset?)NextWindowStartMethod.Invoke(null, [w, now])!;

    [Theory]
    // Closed-open interval — start time inclusive, end time exclusive.
    [InlineData("2026-07-01T08:00:00Z", true,  "08:00", "09:00", "08:00 exactly is inside")]
    [InlineData("2026-07-01T09:00:00Z", false, "08:00", "09:00", "09:00 exactly is OUT (closed-open)")]
    [InlineData("2026-07-01T07:59:59Z", false, "08:00", "09:00", "before window start")]
    public void IsWithinWindow_HonoursClosedOpenIntervalAndUtcTime(
        string nowIso, bool expected, string start, string end, string because)
    {
        var window = new MonitoringWindow
        {
            Id = WindowId.New(),
            RouteId = RouteId.New(),
            DaysOfWeekMask = AllDays,
            StartTime = TimeOnly.Parse(start),
            EndTime = TimeOnly.Parse(end),
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow
        };
        DateTimeOffset now = DateTimeOffset.Parse(nowIso);

        IsWithin(window, now).Should().Be(expected, because);
    }

    [Fact]
    public void IsWithinWindow_RejectsWhenDayNotEnabled()
    {
        var window = new MonitoringWindow
        {
            Id = WindowId.New(),
            RouteId = RouteId.New(),
            DaysOfWeekMask = Wed | Thu,                 // only Wed + Thu
            StartTime = new TimeOnly(0, 0),
            EndTime = new TimeOnly(23, 59),
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow
        };

        // Tuesday 12:00 UTC — outside enabled days even though inside time bounds
        IsWithin(window, new DateTimeOffset(2026, 7, 7, 12, 0, 0, TimeSpan.Zero))
            .Should().BeFalse("Tuesday is not in the Wednesday+Thursday mask");
    }

    /// <summary>
    /// Regression: an Eastern-time user picking 09:21–21:21 local submits a
    /// window stored as 13:21–01:21 UTC (wraps midnight UTC). Pre-fix the
    /// validator rejected this and the polling chain never fired — "Failed to
    /// save schedule" in production. Post-fix the validator permits wrap-around
    /// and IsWithinWindow must include any UTC time on the same day that lies
    /// within [start, 24:00) ∪ [00:00, end).
    /// </summary>
    [Theory]
    // Same-day (afternoon / evening leg of the wrap)
    [InlineData("2026-07-01T13:21:00Z", true,  "13:21 exactly — start of window")]
    [InlineData("2026-07-01T13:20:59Z", false, "one second before start — outside the wrap")]
    // Same-day (early-morning leg of the wrap)
    [InlineData("2026-07-01T01:20:59Z", true,  "just before window end")]
    [InlineData("2026-07-01T01:21:00Z", false, "01:21 exactly is OUT (closed-open end)")]
    public void IsWithinWindow_WrapAroundMidnightUtc(string nowIso, bool expected, string because)
    {
        var window = new MonitoringWindow
        {
            Id = WindowId.New(),
            RouteId = RouteId.New(),
            DaysOfWeekMask = AllDays,
            StartTime = new TimeOnly(13, 21),
            EndTime = new TimeOnly(1, 21),
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow
        };
        DateTimeOffset now = DateTimeOffset.Parse(nowIso);

        IsWithin(window, now).Should().Be(expected, because);
    }

    /// <summary>
    /// A wrap-around window still needs to respect the day mask: polling must
    /// only happen on the days the user ticked.
    /// </summary>
    [Fact]
    public void IsWithinWindow_WrapAround_StillRespectsDayMask()
    {
        var window = new MonitoringWindow
        {
            Id = WindowId.New(),
            RouteId = RouteId.New(),
            DaysOfWeekMask = Mon | Wed | Fri,
            StartTime = new TimeOnly(22, 0), // 22:00 → 02:00 next day
            EndTime = new TimeOnly(2, 0),
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow
        };

        // Tuesday 23:00 UTC — even though in time range, Tuesday not enabled
        IsWithin(window, new DateTimeOffset(2026, 7, 7, 23, 0, 0, TimeSpan.Zero))
            .Should().BeFalse("Tuesday is not in Mon|Wed|Fri mask");
        // Wednesday 23:00 UTC — Wednesday enabled, in afternoon leg of wrap
        IsWithin(window, new DateTimeOffset(2026, 7, 8, 23, 0, 0, TimeSpan.Zero))
            .Should().BeTrue("Wednesday is enabled and 23:00 is within the wrap");
        // Wednesday 01:00 UTC — Wednesday still counts (morning leg of same day's wrap)
        IsWithin(window, new DateTimeOffset(2026, 7, 8, 1, 0, 0, TimeSpan.Zero))
            .Should().BeTrue("Wednesday is enabled and 01:00 is in the morning leg");
    }

    [Fact]
    public void NextWindowStart_WhenInsideNow_ReturnsNextDaysStart_NotTodays()
    {
        // Today is Wednesday 2026-07-01. Window covers 08:00–10:00 every day.
        // The implementation of NextWindowStart strictly returns the first
        // strictly-future slot start — at 09:30 UTC today's 08:00 has already
        // passed, so the next start is tomorrow at 08:00 UTC. (The polling
        // chain uses a separate "schedule next poll" delay to step inside
        // the current window; NextWindowStart is only consulted when fully
        // outside the window.)
        var window = new MonitoringWindow
        {
            Id = WindowId.New(),
            RouteId = RouteId.New(),
            DaysOfWeekMask = AllDays,
            StartTime = new TimeOnly(8, 0),
            EndTime = new TimeOnly(10, 0),
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow
        };

        DateTimeOffset now = new(2026, 7, 1, 9, 30, 0, TimeSpan.Zero);
        DateTimeOffset? next = NextStart(window, now);

        next.Should().NotBeNull();
        next!.Value.Date.Should().Be(new DateTime(2026, 7, 2),
            "next strictly-future start after 09:30 today is tomorrow at 08:00");
        next.Value.Hour.Should().Be(8);
    }

    [Fact]
    public void NextWindowStart_WhenOutsideWindow_RollsToNextEnabledDay()
    {
        // Window is Mon-Fri 08:00–10:00 UTC; current time is Saturday 12:00 UTC.
        // The very next open slot should be Monday 08:00 UTC.
        var window = new MonitoringWindow
        {
            Id = WindowId.New(),
            RouteId = RouteId.New(),
            DaysOfWeekMask = Weekdays,
            StartTime = new TimeOnly(8, 0),
            EndTime = new TimeOnly(10, 0),
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow
        };

        DateTimeOffset now = new(2026, 7, 4, 12, 0, 0, TimeSpan.Zero); // Saturday
        DateTimeOffset? next = NextStart(window, now);

        next.Should().NotBeNull();
        next!.Value.DayOfWeek.Should().Be(DayOfWeek.Monday);
        next.Value.Date.Should().Be(new DateTime(2026, 7, 6));
    }

    [Fact]
    public void NextWindowStart_WhenAllDaysDisabled_ReturnsNullSoChainStops()
    {
        var window = new MonitoringWindow
        {
            Id = WindowId.New(),
            RouteId = RouteId.New(),
            DaysOfWeekMask = 0, // no days
            StartTime = new TimeOnly(8, 0),
            EndTime = new TimeOnly(10, 0),
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow
        };

        DateTimeOffset now = new(2026, 7, 1, 12, 0, 0, TimeSpan.Zero);
        NextStart(window, now).Should().BeNull("no enabled days → polling chain must stop");
    }
}