// filepath: tests/PoTraffic.Tests.E2E/Ui/Helpers/SkippableException.cs
using System;

namespace PoTraffic.Tests.E2E.Helpers;

/// <summary>
/// Wrapper around <see cref="Exception"/> that signals an E2E scenario should be
/// skipped rather than failed (used by <c>SkipUnlessE2EReadyAttribute</c> when
/// Playwright binaries or the app are unreachable).
/// </summary>
public sealed class SkippableException(string message) : Exception(message);
