// filepath: tests/PoTraffic.E2ETests/Ui/Viewports.cs
// CI/CD rule #5 — Mobile-first Playwright viewport parity.
// Every browser-based E2E test runs against both a mobile viewport (iPhone 14)
// and a desktop landscape viewport (Chromebook Plus baseline). Guarantees the
// visual parity rule across form factors.

using Microsoft.Playwright;

namespace PoTraffic.E2ETests.Ui;

public sealed record ViewportProfile(string Name, int Width, int Height, bool IsMobile)
{
    /// <summary>Standard mobile viewport (iPhone 14 baseline).</summary>
    public static readonly ViewportProfile Mobile = new("mobile", 390, 844, IsMobile: true);

    /// <summary>Standard desktop landscape viewport (Chromebook Plus baseline).</summary>
    public static readonly ViewportProfile DesktopLandscape = new("desktop-landscape", 1280, 800, IsMobile: false);

    public static IEnumerable<ViewportProfile> All => [Mobile, DesktopLandscape];

    public ViewportSize ToViewportSize() => new() { Width = Width, Height = Height };
}

[CollectionDefinition("ViewportMatrix", DisableParallelization = false)]
public sealed class ViewportMatrixCollection : ICollectionFixture<ViewportMatrixFixture> { }

public sealed class ViewportMatrixFixture
{
    public IReadOnlyList<ViewportProfile> Profiles { get; } = ViewportProfile.All.ToList();
}