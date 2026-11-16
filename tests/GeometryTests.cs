// GeometryTests - WO-02: the DIP -> physical-pixel pipeline of DESIGN.md section 4.
//
// Oracle provenance: every expected number below is hand-computed from DESIGN.md
// section 4 and PLAN.md AC2/AC8, never copied from Geometry's own output. The
// scenario is one 1920x1080 monitor at the virtual-screen origin, so the DIP and
// physical frames coincide and each criterion isolates a single rule instead of
// two interacting ones.
//
// Where a criterion has a natural boundary, both sides are asserted inside the
// same check (C5: 15 px rejected and 16 px accepted; C10: the inclusive edge
// accepted and 2 px of overflow rejected). A check that asserted only the
// failing side could not tell "rejects tiny rects" from "rejects everything".
//
// Expected values intentionally use literals rather than Geometry.MinimumSize or
// a monitor-derived constant: the oracle comes from the spec, so a change to the
// implementation's constant must show up as a failure here.
using SnipVid.Logic;

namespace SnipVid.Tests
{
    /// <summary>
    /// Acceptance checks for <see cref="Geometry"/>: one test method per work
    /// order criterion, one assertion per method, so the criterion map is
    /// one-to-one with the report.
    /// </summary>
    public static class GeometryTests
    {
        /// <summary>The single 1920x1080 monitor this machine has (DESIGN.md section 4).</summary>
        private static readonly PxRect Monitor = new PxRect(0, 0, 1920, 1080);

        /// <summary>
        /// Runs the documented pipeline - normalise, translate, scale, clamp,
        /// floor to even - for a drag on the test monitor.
        /// </summary>
        /// <param name="x1">Drag start X in DIP.</param>
        /// <param name="y1">Drag start Y in DIP.</param>
        /// <param name="x2">Drag end X in DIP.</param>
        /// <param name="y2">Drag end Y in DIP.</param>
        /// <param name="scale">Monitor DPI scale: 1.0 for 96 dpi, 1.5 for 144 dpi.</param>
        /// <returns>Monitor-relative physical pixels.</returns>
        /// <remarks>Side effects: none.</remarks>
        private static PxRect Physical(double x1, double y1, double x2, double y2, double scale)
        {
            DipRect selection = Geometry.NormaliseDrag(x1, y1, x2, y2);
            return Geometry.ToPhysical(selection, 0.0, 0.0, Monitor, scale);
        }

        /// <summary>C1 (AC2): a 640x450 DIP drag at 100 % is 640x450 physical px.</summary>
        /// <remarks>640 = 740 - 100 and 450 = 650 - 200 by hand; both already even.</remarks>
        public static void TestC1_DragAtUnitScaleIsPixelForPixel()
        {
            PxRect actual = Physical(100, 200, 740, 650, 1.0);
            Harness.Check(
                "C1 drag (100,200)->(740,650) at 1.0 gives X=100 Y=200 W=640 H=450",
                actual == new PxRect(100, 200, 640, 450),
                "expected X=100 Y=200 W=640 H=450, actual " + actual);
        }

        /// <summary>C2: drag direction is irrelevant; a rectangle is defined by its two corners.</summary>
        /// <remarks>
        /// Three permutations are checked, including the both-axes-reversed drag,
        /// because reversing only one axis is the easy case to get right.
        /// </remarks>
        public static void TestC2_DragDirectionDoesNotChangeTheRect()
        {
            PxRect expected = new PxRect(100, 200, 640, 450);
            PxRect reversedX = Physical(740, 200, 100, 650, 1.0);
            PxRect reversedY = Physical(100, 650, 740, 200, 1.0);
            PxRect reversedBoth = Physical(740, 650, 100, 200, 1.0);

            Harness.Check(
                "C2 right-to-left and bottom-to-top drags equal the left-to-right drag",
                reversedX == expected && reversedY == expected && reversedBoth == expected,
                "expected all " + expected + ", actual X-only-reversed " + reversedX
                    + ", Y-only-reversed " + reversedY + ", both-reversed " + reversedBoth);
        }

        /// <summary>C3 (AC8): an odd size floors to even, because yuv420p needs even dimensions.</summary>
        /// <remarks>641 and 451 are odd by construction; 640 and 450 are the next even size down.</remarks>
        public static void TestC3_OddSizeFloorsToEvenDimensions()
        {
            PxRect actual = Physical(0, 0, 641, 451, 1.0);
            Harness.Check(
                "C3 641x451 at 1.0 becomes 640x450 (yuv420p even rule)",
                actual.W == 640 && actual.H == 450,
                "expected W=640 H=450, actual " + actual);
        }

        /// <summary>C4: a small odd size floors to even without any rounding up.</summary>
        public static void TestC4_SmallOddSizeFloorsToEven()
        {
            PxRect actual = Physical(0, 0, 17, 19, 1.0);
            Harness.Check(
                "C4 17x19 at 1.0 becomes 16x18",
                actual.W == 16 && actual.H == 18,
                "expected W=16 H=18, actual " + actual);
        }

        /// <summary>C5: the 16 px minimum, tested on both sides so the threshold is pinned.</summary>
        /// <remarks>
        /// 15 floors to 14 before the rule is applied, so the rejection is the
        /// minimum-size rule and not an accident of flooring; 16 stays usable.
        /// </remarks>
        public static void TestC5_MinimumSizeIsSixteenPixels()
        {
            PxRect tooNarrow = Physical(0, 0, 15, 450, 1.0);
            PxRect atBoundary = Physical(0, 0, 16, 450, 1.0);
            bool narrowOk = Geometry.IsUsable(tooNarrow, Monitor);
            bool boundaryOk = Geometry.IsUsable(atBoundary, Monitor);

            Harness.Check(
                "C5 15x450 is unusable, 16x450 is usable (16 px minimum)",
                !narrowOk && boundaryOk,
                "expected 15x450 rejected and 16x450 accepted, actual 15x450 -> "
                    + tooNarrow + " usable=" + narrowOk + ", 16x450 -> "
                    + atBoundary + " usable=" + boundaryOk);
        }

        /// <summary>C6: the scale step happens after the monitor origin is subtracted.</summary>
        /// <remarks>
        /// 150 = 100 x 1.5 for X and Y, 300 = 200 x 1.5 for W, 150 = 100 x 1.5 for H.
        /// The second half drags the identical rectangle on a monitor whose DIP
        /// origin is (1920, 0): 2020 - 1920 = 100 DIP, so the monitor-relative
        /// result must be byte-identical. Without it, "subtract the monitor
        /// origin" could be deleted and every criteria check would still pass on
        /// this single-monitor machine.
        /// </remarks>
        public static void TestC6_ScaledDragConvertsDipToPhysical()
        {
            PxRect actual = Physical(100, 100, 300, 200, 1.5);
            DipRect shifted = Geometry.NormaliseDrag(2020, 100, 2220, 200);
            PxRect translated = Geometry.ToPhysical(shifted, 1920, 0, Monitor, 1.5);

            Harness.Check(
                "C6 DIP (100,100)->(300,200) at 1.5 gives X=150 Y=150 W=300 H=150, also after an origin shift",
                actual == new PxRect(150, 150, 300, 150) && translated == actual,
                "expected X=150 Y=150 W=300 H=150 at origin 0 and after a 1920 DIP origin shift, actual "
                    + actual + " and " + translated);
        }

        /// <summary>
        /// C7: the floor is applied to scaled physical pixels, not to DIP before
        /// scaling. 101 x 1.25 = 126.25, so the width is 126.
        /// </summary>
        /// <remarks>
        /// This is the scaled-laptop criterion: rounding the product (or scaling
        /// after flooring the DIP width) yields a different rectangle and a
        /// capture that is one pixel off the selection.
        /// </remarks>
        public static void TestC7_FloorHappensAfterScaling()
        {
            PxRect actual = Physical(0, 0, 101, 200, 1.25);
            Harness.Check(
                "C7 DIP width 101 at 1.25 gives W=126, not 127",
                actual.W == 126,
                "expected W=126 (floor of 126.25), actual " + actual);
        }

        /// <summary>C8: a selection crossing the monitor edge is clamped, then still made even.</summary>
        /// <remarks>
        /// 319 physical px from 1801 would end at 2120, 200 px outside the
        /// monitor; the clamp leaves 119 px, which floors to 118, so the final
        /// right edge is 1918 and never exceeds 1920.
        /// </remarks>
        public static void TestC8_RectCrossingMonitorEdgeIsClamped()
        {
            PxRect actual = Physical(1801, 100, 2120, 200, 1.0);
            bool inside = actual.Right <= Monitor.Right && actual.Bottom <= Monitor.Bottom;
            bool even = (actual.X & 1) == 0 && (actual.Y & 1) == 0
                        && (actual.W & 1) == 0 && (actual.H & 1) == 0;

            Harness.Check(
                "C8 rect crossing the right edge clamps to X=1800 W=118 (inside 1920, still even)",
                actual == new PxRect(1800, 100, 118, 100) && inside && even,
                "expected X=1800 Y=100 W=118 H=100 inside and even, actual " + actual
                    + " inside=" + inside + " even=" + even);
        }

        /// <summary>
        /// C8 (left half): a selection starting left of the monitor is clamped to
        /// the edge and shrinks; it must not slide back inside.
        /// </summary>
        /// <remarks>
        /// The drag starts 100 DIP left of the monitor, so the width loses those
        /// 100 px (300 -> 200). Sliding instead of shrinking would record pixels
        /// the user never selected, and a negative X would be rejected later by
        /// IsUsable with no explanation.
        /// </remarks>
        public static void TestC8b_LeftEdgeClampShrinksInsteadOfSliding()
        {
            PxRect actual = Physical(-100, 100, 200, 300, 1.0);
            Harness.Check(
                "C8b selection starting left of the monitor clamps to X=0 W=200",
                actual == new PxRect(0, 100, 200, 200),
                "expected X=0 Y=100 W=200 H=200, actual " + actual);
        }

        /// <summary>C8 (top half): the Y axis uses the same clamp as the X axis.</summary>
        /// <remarks>
        /// 80 DIP above the monitor origin: H loses 80 px (280 -> 200) and Y
        /// becomes 0 rather than staying negative.
        /// </remarks>
        public static void TestC8c_TopEdgeClampShrinksInsteadOfSliding()
        {
            PxRect actual = Physical(100, -80, 500, 200, 1.0);
            Harness.Check(
                "C8c selection starting above the monitor clamps to Y=0 H=200",
                actual == new PxRect(100, 0, 400, 200),
                "expected X=100 Y=0 W=400 H=200, actual " + actual);
        }

        /// <summary>C8 (bottom half): bottom overflow is cut exactly like right overflow.</summary>
        /// <remarks>
        /// 1200 px tall bottom edge on a 1080 monitor leaves H = 180 from Y = 900,
        /// so the rectangle stays inside and even.
        /// </remarks>
        public static void TestC8d_BottomEdgeClampCutsTheOverflow()
        {
            PxRect actual = Physical(100, 900, 500, 1200, 1.0);
            Harness.Check(
                "C8d selection overflowing the bottom clamps to Y=900 H=180",
                actual == new PxRect(100, 900, 400, 180) && actual.Bottom <= Monitor.Bottom,
                "expected X=100 Y=900 W=400 H=180 with Bottom<=1080, actual " + actual);
        }

        /// <summary>C9: an odd origin floors left/up, so the right edge never moves right.</summary>
        /// <remarks>
        /// X = 101 becomes 100 and a 200 px width stays 200, so the right edge
        /// moves from 301 to 300. Rounding to nearest would also give 100 here,
        /// which is why the right-edge inequality is asserted as well.
        /// </remarks>
        public static void TestC9_OddOriginFloorsLeftNeverRight()
        {
            PxRect actual = Physical(101, 100, 301, 300, 1.0);
            bool evenOrigin = (actual.X & 1) == 0 && (actual.Y & 1) == 0;
            bool neverRight = actual.Right <= 301 && actual.Bottom <= 300;

            Harness.Check(
                "C9 odd origin X=101 floors to X=100 and the right edge shrinks to 300",
                actual.X == 100 && actual.Right == 300 && evenOrigin && neverRight,
                "expected X=100 with Right=300 (<= 301), actual " + actual
                    + " evenOrigin=" + evenOrigin + " neverRight=" + neverRight);
        }

        /// <summary>C10: the monitor edge itself is valid; 2 px beyond it is not.</summary>
        /// <remarks>
        /// A full-monitor selection must survive unchanged, so the bounds check
        /// is inclusive (Right == monitor.Right is legal). The overflowing rect
        /// proves IsUsable is not a constant true.
        /// </remarks>
        public static void TestC10_FullMonitorSelectionIsValid()
        {
            PxRect full = Physical(0, 0, 1920, 1080, 1.0);
            PxRect overflowing = new PxRect(0, 0, 1922, 1080);
            bool unchanged = full == new PxRect(0, 0, 1920, 1080);
            bool accepted = Geometry.IsUsable(full, Monitor);
            bool rejected = !Geometry.IsUsable(overflowing, Monitor);

            Harness.Check(
                "C10 full 1920x1080 selection accepted unchanged; 1922 wide rejected",
                unchanged && accepted && rejected,
                "expected " + full + " unchanged and usable, actual accepted=" + accepted
                    + "; expected 1922 wide rejected, actual rejected=" + rejected);
        }
    }
}
