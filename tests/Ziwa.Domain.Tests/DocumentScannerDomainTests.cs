using Xunit;
using Ziwa.Domain.Scanner;

namespace Ziwa.Domain.Tests;

public sealed class DocumentScannerDomainTests
{
    [Fact]
    public void OrderClockwise_NormalizesScrambledCorners_StartingFromTopLeft()
    {
        PointPx[] scrambled =
        [
            new PointPx(420, 310), // BottomRight
            new PointPx(100, 90),  // TopLeft
            new PointPx(90, 300),  // BottomLeft
            new PointPx(410, 100), // TopRight
        ];

        DocumentQuadPx ordered = DocumentQuadPx.OrderClockwise(scrambled);
        Assert.Equal(new PointPx(100, 90), ordered.TopLeft);
        Assert.Equal(new PointPx(410, 100), ordered.TopRight);
        Assert.Equal(new PointPx(420, 310), ordered.BottomRight);
        Assert.Equal(new PointPx(90, 300), ordered.BottomLeft);
    }

    [Fact]
    public void WithCorner_ClampsToImageBounds_AndRejectsDegenerateCollapse()
    {
        DocumentQuadPx quad = DocumentQuadPx.CreateInsetDefault(400, 300, 0.1);
        DocumentQuadPx clamped = quad.WithCorner(0, new PointPx(-50, -30), 400, 300);
        Assert.Equal(new PointPx(0, 0), clamped.TopLeft);

        DocumentQuadPx degenerate = quad.WithCorner(2, quad.TopLeft, 400, 300);
        Assert.Equal(quad, degenerate);
    }

    [Fact]
    public void SplitForIdCard_ProducesNonOverlappingHalvesWithGutterTrim()
    {
        var quad = new DocumentQuadPx(
            new PointPx(20, 40),
            new PointPx(220, 40),
            new PointPx(220, 340),
            new PointPx(20, 340));

        (DocumentQuadPx top, DocumentQuadPx bottom) = quad.SplitForIdCard(IdCardSplitAxis.HorizontalTopBottom, splitRatio: 0.5, gutterTrimRatio: 0.04);
        Assert.True(top.BottomLeft.Y < bottom.TopLeft.Y);
        Assert.Equal(40.0, top.TopLeft.Y, precision: 4);
        Assert.Equal(340.0, bottom.BottomLeft.Y, precision: 4);

        (DocumentQuadPx left, DocumentQuadPx right) = quad.SplitForIdCard(IdCardSplitAxis.VerticalLeftRight, splitRatio: 0.5, gutterTrimRatio: 0.04);
        Assert.True(left.TopRight.X < right.TopLeft.X);
        Assert.Equal(20.0, left.TopLeft.X, precision: 4);
        Assert.Equal(220.0, right.TopRight.X, precision: 4);
    }

    [Fact]
    public void ComputeOutputDimensionsPx_RespectsMaxEdgeCap()
    {
        var huge = new DocumentQuadPx(
            new PointPx(0, 0),
            new PointPx(8000, 0),
            new PointPx(8000, 4000),
            new PointPx(0, 4000));

        (int w, int h) = huge.ComputeOutputDimensionsPx(maxEdgePx: 2000);
        Assert.Equal(2000, w);
        Assert.Equal(1000, h);
    }
}
