// filepath: tests/PoTraffic.E2ETests/Ui/Helpers/SkippableException.cs
using System;

namespace PoTraffic.E2ETests.Ui.Helpers;

/// <summary>
/// Wrapper around <see cref="Exception"/> that signals an E2E scenario should be
/// skipped rather than failed (used by <c>SkipUnlessE2EReadyAttribute</c> when
/// Playwright binaries or the app are unreachable).
/// </summary>
public sealed class SkippableException(string message) : Exception(message);
