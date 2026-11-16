// Geometry - DIP to physical-pixel conversion for the capture region (DESIGN.md section 4).
//
// WHY this file exists: the overlay works in 96-dpi logical units (DIP) while
// ddagrab wants physical pixels with even width and height (yuv420p). This
// machine is 100 % scale so the pipeline is currently identity - which is
// exactly why it is written and tested now: on a scaled laptop an untested
// scale step would silently record the wrong rectangle.
//
// Order is fixed and must not be rearranged: translate -> scale -> clamp ->
// floor to even. Flooring last is what keeps the result both inside the monitor
// and even (AC8); flooring first lets the up-to-2-px width drop push the right
// edge back over the monitor edge or back to an odd value.
//
// Frames: the input selection is DIP in the virtual-screen frame as WPF reports
// it; the output is monitor-relative physical pixels, the frame ddagrab's
// offset_x/offset_y consume. A monitor rectangle is therefore used for its Size
// only, because the monitor origin has already been subtracted.
//
// No WPF, no P/Invoke, no I/O, no clock: test.ps1 compiles this file without any
// Windows reference, so a test physically cannot reach the desktop (DESIGN.md
// section 11).
using System;

namespace SnipVid.Logic
{
    /// <summary>
    /// An axis-aligned rectangle in physical pixels.
    /// </summary>
    /// <remarks>
    /// Immutable value type. X/Y is the top-left corner, W/H the size, edges are
    /// exclusive so Right = X + W; the monitor-local rectangle (0,0,1920,1080)
    /// therefore exactly covers a 1920x1080 monitor.
    /// No validation and no throwing: negative or oversized values are
    /// representable and are rejected by <see cref="Geometry.IsUsable"/>, because
    /// this value flows through clamping code that must tolerate a degenerate
    /// drag rather than fail the capture.
    /// </remarks>
    public struct PxRect
    {
        /// <summary>Left edge, physical pixels (monitor-relative after conversion).</summary>
        public readonly int X;

        /// <summary>Top edge, physical pixels (monitor-relative after conversion).</summary>
        public readonly int Y;

        /// <summary>Width in physical pixels; even for anything this app records.</summary>
        public readonly int W;

        /// <summary>Height in physical pixels; even for anything this app records.</summary>
        public readonly int H;

        /// <summary>
        /// Initialises a rectangle from a corner and a size.
        /// </summary>
        /// <param name="x">Left edge in physical pixels.</param>
        /// <param name="y">Top edge in physical pixels.</param>
        /// <param name="w">Width in physical pixels.</param>
        /// <param name="h">Height in physical pixels.</param>
        /// <remarks>Side effects: none. Accepts any int, including negatives.</remarks>
        public PxRect(int x, int y, int w, int h)
        {
            X = x;
            Y = y;
            W = w;
            H = h;
        }

        /// <summary>Exclusive right edge, X + W, in the same frame as X.</summary>
        public int Right
        {
            get { return X + W; }
        }

        /// <summary>Exclusive bottom edge, Y + H, in the same frame as Y.</summary>
        public int Bottom
        {
            get { return Y + H; }
        }

        /// <summary>Value equality over all four fields.</summary>
        /// <param name="other">Rectangle to compare with.</param>
        /// <returns>True when X, Y, W and H all match.</returns>
        /// <remarks>Side effects: none.</remarks>
        public bool Equals(PxRect other)
        {
            return X == other.X && Y == other.Y && W == other.W && H == other.H;
        }

        /// <summary>Value equality against any object.</summary>
        /// <param name="obj">Candidate; a boxed PxRect compares by value.</param>
        /// <returns>True for a PxRect with identical fields, false otherwise.</returns>
        /// <remarks>Side effects: none. A non-PxRect argument returns false, never throws.</remarks>
        public override bool Equals(object obj)
        {
            return obj is PxRect && Equals((PxRect)obj);
        }

        /// <summary>Hash over the four fields, consistent with Equals.</summary>
        /// <returns>A hash code; equal rectangles hash equally.</returns>
        /// <remarks>Side effects: none.</remarks>
        public override int GetHashCode()
        {
            return X ^ (Y << 8) ^ (W << 16) ^ (H << 24);
        }

        /// <summary>
        /// Diagnostic form used in test failure messages and logs.
        /// </summary>
        /// <returns>Exactly "X=.. Y=.. W=.. H=..", formatted with the invariant culture.</returns>
        /// <remarks>
        /// Side effects: none. Culture-invariant so test-failure text and log
        /// lines are identical on every machine, matching DipRect.ToString.
        /// </remarks>
        public override string ToString()
        {
            return string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                "X={0} Y={1} W={2} H={3}", X, Y, W, H);
        }

        /// <summary>Value equality operator.</summary>
        /// <param name="a">Left operand.</param>
        /// <param name="b">Right operand.</param>
        /// <returns>True when both rectangles are identical.</returns>
        /// <remarks>Side effects: none.</remarks>
        public static bool operator ==(PxRect a, PxRect b)
        {
            return a.Equals(b);
        }

        /// <summary>Value inequality operator.</summary>
        /// <param name="a">Left operand.</param>
        /// <param name="b">Right operand.</param>
        /// <returns>True when any of the four fields differ.</returns>
        /// <remarks>Side effects: none.</remarks>
        public static bool operator !=(PxRect a, PxRect b)
        {
            return !a.Equals(b);
        }
    }

    /// <summary>
    /// An axis-aligned rectangle in device-independent pixels (96 dpi units),
    /// ordered so X/Y is the top-left corner and W/H are non-negative.
    /// </summary>
    /// <remarks>
    /// DIP values stay double because WPF reports fractional logical units and
    /// rounding them before the scale step would lose up to one physical pixel
    /// at 125 %/150 % scale. No validation, no throwing, no side effects: an
    /// empty or reversed rectangle is representable only by hand, since
    /// <see cref="Geometry.NormaliseDrag"/> always produces an ordered one.
    /// </remarks>
    public struct DipRect
    {
        /// <summary>Left edge in DIP.</summary>
        public readonly double X;

        /// <summary>Top edge in DIP.</summary>
        public readonly double Y;

        /// <summary>Width in DIP; never negative when produced by NormaliseDrag.</summary>
        public readonly double W;

        /// <summary>Height in DIP; never negative when produced by NormaliseDrag.</summary>
        public readonly double H;

        /// <summary>
        /// Initialises a rectangle from a corner and a size.
        /// </summary>
        /// <param name="x">Left edge in DIP.</param>
        /// <param name="y">Top edge in DIP.</param>
        /// <param name="w">Width in DIP.</param>
        /// <param name="h">Height in DIP.</param>
        /// <remarks>Side effects: none. Values are stored verbatim.</remarks>
        public DipRect(double x, double y, double w, double h)
        {
            X = x;
            Y = y;
            W = w;
            H = h;
        }

        /// <summary>Right edge, X + W, in DIP.</summary>
        public double Right
        {
            get { return X + W; }
        }

        /// <summary>Bottom edge, Y + H, in DIP.</summary>
        public double Bottom
        {
            get { return Y + H; }
        }

        /// <summary>
        /// Diagnostic form used in test failure messages and logs.
        /// </summary>
        /// <returns>Exactly "X=.. Y=.. W=.. H=.." in invariant culture.</returns>
        /// <remarks>
        /// Side effects: none. Formatting is culture-invariant on purpose so a
        /// log line from a machine with comma decimal separators is still
        /// readable next to one with dots.
        /// </remarks>
        public override string ToString()
        {
            return string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                "X={0} Y={1} W={2} H={3}", X, Y, W, H);
        }
    }

    /// <summary>
    /// Pure coordinate math for the capture region: DIP drag in, even
    /// monitor-relative physical pixels out (DESIGN.md section 4).
    /// </summary>
    /// <remarks>
    /// Every method is total and side-effect free: nonsense input yields a
    /// rectangle that <see cref="IsUsable"/> rejects, never an exception, so a
    /// rejected drag can show a message instead of tearing down the process.
    /// </remarks>
    public static class Geometry
    {
        /// <summary>
        /// Smallest width or height, in physical pixels, that may be recorded.
        /// </summary>
        /// <remarks>
        /// DESIGN.md section 4 rejects anything under 16 px. Public so the
        /// overlay can grey out Record at exactly the threshold this module
        /// enforces, instead of duplicating the number.
        /// </remarks>
        public const int MinimumSize = 16;

        /// <summary>
        /// Orders a drag into a rectangle regardless of which corner it started
        /// from.
        /// </summary>
        /// <param name="startX">Drag origin X in DIP (mouse-down point).</param>
        /// <param name="startY">Drag origin Y in DIP.</param>
        /// <param name="endX">Drag end X in DIP (current pointer position).</param>
        /// <param name="endY">Drag end Y in DIP.</param>
        /// <returns>X/Y at the top-left, W/H non-negative, still in DIP.</returns>
        /// <remarks>
        /// Side effects: none. Never throws; equal start and end produce a
        /// zero-size rectangle, which IsUsable will reject.
        /// A right-to-left drag must produce the identical rectangle, so the
        /// caller never has to branch on drag direction.
        /// </remarks>
        public static DipRect NormaliseDrag(double startX, double startY, double endX, double endY)
        {
            // Min/Abs rather than a direction branch: the rectangle is defined by
            // its two corners, so every drag direction collapses to one case.
            return new DipRect(
                Math.Min(startX, endX),
                Math.Min(startY, endY),
                Math.Abs(endX - startX),
                Math.Abs(endY - startY));
        }

        /// <summary>
        /// Converts a DIP selection to the physical pixels given to ffmpeg.
        /// </summary>
        /// <param name="selection">Ordered DIP rectangle, from NormaliseDrag.</param>
        /// <param name="monitorOriginX">Monitor origin X in DIP, subtracted first.</param>
        /// <param name="monitorOriginY">Monitor origin Y in DIP, subtracted first.</param>
        /// <param name="monitorBounds">
        /// The monitor's physical rectangle. Only its W/H are used: the result is
        /// monitor-relative, so the origin is already subtracted and 0,0 is the
        /// monitor's top-left corner.
        /// </param>
        /// <param name="dpiScale">Monitor scale, 1.0 at 96 dpi, 1.5 at 144 dpi.</param>
        /// <returns>
        /// Monitor-relative physical pixels with even X/Y/W/H; the value ddagrab
        /// takes as offset_x/offset_y/video_size.
        /// </returns>
        /// <remarks>
        /// Order is fixed by DESIGN.md section 4: subtract origin, multiply by
        /// scale, clamp to the monitor, floor to even. Flooring last keeps the
        /// rectangle inside the monitor and even at the same time.
        /// Side effects: none. Never throws: a selection entirely off the monitor
        /// or a non-positive scale yields a degenerate rectangle that IsUsable
        /// rejects, because the caller has to report that rather than crash
        /// mid-capture.
        /// </remarks>
        public static PxRect ToPhysical(
            DipRect selection,
            double monitorOriginX,
            double monitorOriginY,
            PxRect monitorBounds,
            double dpiScale)
        {
            // Step 1+2: translate to monitor-local, then scale. Each edge is
            // mapped independently; scaling X and W separately would round twice
            // and could differ from scaling the right edge by a pixel.
            double left = (selection.X - monitorOriginX) * dpiScale;
            double top = (selection.Y - monitorOriginY) * dpiScale;
            double right = (selection.Right - monitorOriginX) * dpiScale;
            double bottom = (selection.Bottom - monitorOriginY) * dpiScale;

            // Step 3: clamp to the monitor. Both edges are clamped, so a
            // selection that starts outside the monitor shrinks instead of
            // sliding back in, and a selection wholly outside collapses to zero
            // size for IsUsable to reject.
            double maxRight = monitorBounds.W;
            double maxBottom = monitorBounds.H;
            left = Clamp(left, 0.0, maxRight);
            right = Clamp(right, 0.0, maxRight);
            top = Clamp(top, 0.0, maxBottom);
            bottom = Clamp(bottom, 0.0, maxBottom);
            if (right < left)
            {
                right = left;
            }
            if (bottom < top)
            {
                bottom = top;
            }

            // Step 4: floor to even, last. Flooring the size as well as the
            // origin means the floored right edge is <= the clamped one, so the
            // rectangle can only ever move left/up, never back over the edge.
            return new PxRect(
                FloorToEven(left),
                FloorToEven(top),
                FloorToEven(right - left),
                FloorToEven(bottom - top));
        }

        /// <summary>
        /// Floors a physical-pixel coordinate or size down to the next even integer.
        /// </summary>
        /// <param name="value">Coordinate or size in physical pixels.</param>
        /// <returns>The largest even integer less than or equal to value.</returns>
        /// <remarks>
        /// Side effects: none. Uses Math.Floor first so the bitmask only ever
        /// clears the low bit; applying the mask directly to a negative number
        /// would round the wrong way. Values are monitor-sized in practice, so
        /// the int cast cannot overflow.
        /// </remarks>
        private static int FloorToEven(double value)
        {
            return (int)Math.Floor(value) & ~1;
        }

        /// <summary>
        /// Constrains a value to an inclusive range.
        /// </summary>
        /// <param name="value">Candidate value.</param>
        /// <param name="min">Inclusive lower bound.</param>
        /// <param name="max">Inclusive upper bound.</param>
        /// <returns>value clamped into [min, max]; the bounds are assumed ordered (0 &lt;= max).</returns>
        /// <remarks>
        /// Side effects: none. NaN is deliberately unguarded: both comparisons are
        /// false, so NaN passes through Clamp unchanged and FloorToEven then maps
        /// it to 0 through the int cast, leaving a degenerate rectangle that
        /// IsUsable rejects. Throwing here would turn a harmless degenerate drag
        /// into a crash mid-capture, and no criterion needs NaN handled otherwise.
        /// </remarks>
        private static double Clamp(double value, double min, double max)
        {
            if (value < min)
            {
                return min;
            }
            if (value > max)
            {
                return max;
            }
            return value;
        }

        /// <summary>
        /// Decides whether a physical rectangle may be recorded.
        /// </summary>
        /// <param name="rect">Physical rectangle, monitor-relative, from ToPhysical.</param>
        /// <param name="monitorBounds">
        /// The monitor's physical rectangle; only W/H are used, matching the
        /// frame ToPhysical returns. Convert an absolute-screen rect by
        /// subtracting the monitor origin before calling.
        /// </param>
        /// <returns>
        /// True when W and H are at least 16 px and the rectangle lies inside the
        /// monitor, with the monitor's right/bottom edge counting as inside.
        /// </returns>
        /// <remarks>
        /// Side effects: none. Never throws: the answer for a negative size or a
        /// rectangle outside the monitor is simply false.
        /// The edges are inclusive so a full-monitor selection (W = monitor.W)
        /// is legal rather than off by one - the same boundary ddagrab accepts.
        /// </remarks>
        public static bool IsUsable(PxRect rect, PxRect monitorBounds)
        {
            return rect.W >= MinimumSize
                && rect.H >= MinimumSize
                && rect.X >= 0
                && rect.Y >= 0
                && rect.Right <= monitorBounds.W
                && rect.Bottom <= monitorBounds.H;
        }
    }
}
