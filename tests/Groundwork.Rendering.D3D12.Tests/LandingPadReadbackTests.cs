using System.Numerics;
using Groundwork.Voxels;

namespace Groundwork.Rendering.D3D12.Tests;

public sealed partial class PresentationSmokeTests
{
    private const uint LandingAmber = 0xFF20C8FF;
    private const uint LandingBorder = 0xFF203646;
    private const uint LandingGround = 0xFF435636;
    private static readonly double LandingAngle = 35 * Math.PI / 180;
    private static readonly PlanetVector LandingRadial = new(Math.Cos(LandingAngle), 0, Math.Sin(LandingAngle));

    [TestMethod]
    public void WarpLandingPadColorAndSilhouetteAreVisibleOnApproach() => VerifyLandingPad(useWarp: true, requireDebugLayer: false);

    [TestMethod]
    [TestCategory("WindowsDesktopSmoke")]
    [DataRow(true)]
    [DataRow(false)]
    public void LandingPadColorAndSilhouetteAreVisibleWithDebugLayer(bool useWarp) =>
        VerifyLandingPad(useWarp, requireDebugLayer: true);

    private static void VerifyLandingPad(bool useWarp, bool requireDebugLayer)
    {
        var terrain = new PlanetTerrainCoordinator();
        var center = AtLanding(64.65, 0);
        var origin = PlanetLocalFrame.Create(AtLanding(96, 0));
        var firstCamera = AtLanding(80, -5);
        Assert.IsTrue(origin.TryRebase(firstCamera, out var approachOrigin));
        Assert.IsTrue(PlanetTerrainSelection.TrySelectFlight(firstCamera, LandingRadial, 1, null, out var plan));
        Assert.IsNotNull(plan);
        Assert.AreEqual(PlanetTerrainView.Near, plan.View);
        Assert.IsTrue(terrain.TryStage(plan, approachOrigin));
        while (terrain.TryReserveMesh(out var work))
        {
            var mesh = PlanetPatchMesh.Build(work.Tile.Patch, 64, LandingHeight, work.Tile.Transitions);
            Assert.IsTrue(terrain.CompleteMesh(work, mesh));
        }

        Assert.IsTrue(terrain.TryPreparePresentation(firstCamera, out var first));
        var gpu = new GpuDevice(warp: useWarp, enableDebugLayer: true, enableDred: true,
            region: D3D12Renderer.PackPlanetTerrain(first, LandingColor));
        using (gpu)
        using (first)
        {
            AssertPlanetAdapter(gpu.Adapter, useWarp);
            Assert.IsTrue(gpu.DredEnabled);
            if (requireDebugLayer)
                Assert.IsTrue(gpu.DebugLayerEnabled);
            VerifyLandingView(gpu, first, center, 1, minWidth: 16);
            Assert.IsTrue(terrain.AcknowledgePresented(first));
            Assert.IsTrue(terrain.TryReadActive(AtLanding(72, -2), out var middle));
            using (middle)
                VerifyLandingView(gpu, middle, center, 2, minWidth: 29);
            Assert.IsTrue(terrain.TryReadActive(AtLanding(68.5, 0), out var low));
            using (low)
                VerifyLandingView(gpu, low, center, 3, minWidth: 55);
        }
        terrain.Stop();
        Assert.IsTrue(terrain.IsSettled);
        AssertFlightGpuReleased(gpu);
    }

    private static void VerifyLandingView(GpuDevice gpu, PlanetTerrainLease lease, PlanetVector center, ulong sequence, int minWidth)
    {
        var forward = Vector3.Normalize(lease.Frame.ToLocal(center) - lease.CameraPosition);
        var frame = D3D12Renderer.CreatePlanetFrame(lease, sequence, forward, Vector3.UnitY,
            MathF.PI / 3, 0.08f, 220, Vector4.One, new Vector4(0, 0, 0, 1));
        var pixels = GpuDevice.RenderOffscreen(gpu, TerrainWidth, TerrainHeight, frame);
        var middleX = TerrainWidth / 2;
        var middleY = TerrainHeight / 2;
        Assert.IsTrue(IsAmber(pixels.ColorAt(middleX, middleY)));
        Assert.IsLessThan(1f, pixels.DepthAt(middleX, middleY));
        var left = middleX;
        var right = middleX;
        var top = middleY;
        var bottom = middleY;
        while (left > 0 && IsAmber(pixels.ColorAt(left - 1, middleY))) left--;
        while (right + 1 < TerrainWidth && IsAmber(pixels.ColorAt(right + 1, middleY))) right++;
        while (top > 0 && IsAmber(pixels.ColorAt(middleX, top - 1))) top--;
        while (bottom + 1 < TerrainHeight && IsAmber(pixels.ColorAt(middleX, bottom + 1))) bottom++;
        Assert.IsGreaterThanOrEqualTo(minWidth, right - left + 1);
        Assert.IsGreaterThanOrEqualTo(minWidth - 5, bottom - top + 1);
        foreach (var x in new[] { left + 2, right - 2 })
        foreach (var y in new[] { top + 2, bottom - 2 })
        {
            Assert.IsFalse(IsAmber(pixels.ColorAt(x, y)), "The corners outside the diamond must remain distinct.");
            Assert.IsLessThan(1f, pixels.DepthAt(x, y), "The silhouette must sit on terrain.");
        }
        Console.WriteLine($"Landing approach sample {sequence}: amber cross-section {right - left + 1} x {bottom - top + 1} pixels.");
    }

    private static bool IsAmber(ReadOnlySpan<byte> color) => color[0] > 180 && color[1] > 120 && color[2] < 80;

    private static PlanetVector AtLanding(double radius, double tangent) =>
        new(radius * LandingRadial.X - tangent * LandingRadial.Z, 0, radius * LandingRadial.Z + tangent * LandingRadial.X);

    private static uint LandingColor(PlanetSampleKey key)
    {
        var d = PlanetCoordinates.Direction(key);
        if (d.X * LandingRadial.X + d.Z * LandingRadial.Z <= 0)
            return LandingGround;
        var diamond = 64 * (Math.Abs(-d.X * LandingRadial.Z + d.Z * LandingRadial.X) + Math.Abs(d.Y));
        return diamond <= 2.35 ? LandingAmber : diamond <= 2.95 ? LandingBorder : LandingGround;
    }

    private static double LandingHeight(PlanetSampleKey key)
    {
        var d = PlanetCoordinates.Direction(key);
        var arc = 64 * Math.Acos(Math.Clamp(d.X * LandingRadial.X + d.Z * LandingRadial.Z, -1, 1));
        return (Math.Round(1200 * d.X * d.Z + 600 * d.Y) + Math.Round(120 * Math.Clamp(2 - arc, 0, 1))) / 1000;
    }
}
