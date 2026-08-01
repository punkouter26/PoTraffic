// filepath: tests/PoTraffic.Tests/Unit/Features/Routes/PollRouteJobWindowBoundaryTests.cs
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
    [InlineData("2026-07-01T08:59:59Z", true,  "08:00", "09:00", "08:59:59 is inside")]
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