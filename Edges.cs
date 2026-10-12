using System;
using static SharedMouse.Native;

namespace SharedMouse
{
    /// <summary>A side of a screen. On the wire: 0 left, 1 right, 2 top, 3 bottom.</summary>
    enum Side : byte { Left, Right, Top, Bottom }

    /// <summary>What one mouse move does at an outer screen edge.</summary>
    enum EdgeMove
    {
        Away,  // ends up off the edge
        Along, // stays on the edge without pushing past it (sideways, or too little to make a pixel)
        Past,  // pushes past the edge
    }

    static class Edges
    {
        public static Side Opposite(Side s) => s == Side.Left ? Side.Right : s == Side.Right ? Side.Left : s == Side.Top ? Side.Bottom : Side.Top;

        public static Side Parse(string s)
        {
            switch (s)
            {
                case "left": case "l": return Side.Left;
                case "right": case "r": return Side.Right;
                case "top": case "t": case "up": case "above": return Side.Top;
                case "bottom": case "b": case "down": case "below": case "under": return Side.Bottom;
                default: throw new ArgumentException("--side must be left, right, top or bottom.");
            }
        }

        /// <summary>True if moving from <paramref name="from"/> to <paramref name="to"/> crosses the outer edge
        /// <paramref name="side"/> of the current monitor, i.e. there is no other monitor of this laptop beyond it.</summary>
        public static bool Crosses(POINT from, POINT to, Side side)
        {
            RECT m = MonitorRect(from);
            switch (side)
            {
                case Side.Left: return to.X < m.Left && !IsOnAnyMonitor(new POINT(m.Left - 1, from.Y));
                case Side.Right: return to.X >= m.Right && !IsOnAnyMonitor(new POINT(m.Right, from.Y));
                case Side.Top: return to.Y < m.Top && !IsOnAnyMonitor(new POINT(from.X, m.Top - 1));
                default: return to.Y >= m.Bottom && !IsOnAnyMonitor(new POINT(from.X, m.Bottom));
            }
        }

        /// <summary>Whether moving from <paramref name="from"/> to <paramref name="to"/> pushes past the outer edge
        /// <paramref name="side"/>, stays on it, or leaves it.</summary>
        public static EdgeMove Classify(POINT from, POINT to, Side side)
        {
            if (Crosses(from, to, side))
                return EdgeMove.Past;
            RECT m = MonitorRect(from);
            bool onEdge = side == Side.Left ? to.X == m.Left : side == Side.Right ? to.X == m.Right - 1 :
                          side == Side.Top ? to.Y == m.Top : to.Y == m.Bottom - 1;
            return onEdge ? EdgeMove.Along : EdgeMove.Away;
        }

        /// <summary>Where along the edge the point is: 0..1, top-to-bottom for left/right edges, left-to-right for top/bottom.</summary>
        public static float Along(RECT m, POINT p, Side side) =>
            side == Side.Left || side == Side.Right ? (p.Y - m.Top) / (float)m.Height : (p.X - m.Left) / (float)m.Width;

        /// <summary>The point <paramref name="inset"/> pixels inside edge <paramref name="side"/> of monitor m, at fraction t along it.</summary>
        public static POINT Inside(RECT m, Side side, float t, int inset)
        {
            t = float.IsNaN(t) ? 0.5f : Math.Max(0, Math.Min(1, t));
            int x = m.Left + (int)(t * (m.Width - 1)), y = m.Top + (int)(t * (m.Height - 1));
            switch (side)
            {
                case Side.Left: return new POINT(m.Left + inset, y);
                case Side.Right: return new POINT(m.Right - 1 - inset, y);
                case Side.Top: return new POINT(x, m.Top + inset);
                default: return new POINT(x, m.Bottom - 1 - inset);
            }
        }

        /// <summary>The monitor on the outer <paramref name="side"/> of this laptop's desktop.</summary>
        public static RECT OuterMonitor(Side side)
        {
            RECT vs = VirtualScreen();
            int mx = vs.Left + vs.Width / 2, my = vs.Top + vs.Height / 2;
            switch (side)
            {
                case Side.Left: return MonitorRect(new POINT(vs.Left, my));
                case Side.Right: return MonitorRect(new POINT(vs.Right - 1, my));
                case Side.Top: return MonitorRect(new POINT(mx, vs.Top));
                default: return MonitorRect(new POINT(mx, vs.Bottom - 1));
            }
        }
    }

    /// <summary>
    /// "Push through" the edge: switching only happens after the mouse keeps pushing against the edge for a moment,
    /// so flicking to browser tabs at the top or the taskbar at the bottom doesn't jump to the other laptop.
    /// A mouse sends up to 1000 tiny moves a second and many of them don't push past the edge on their own
    /// (a bit of sideways movement, or less than a pixel), so those don't start over. What does: leaving the edge,
    /// pausing, or sliding along the edge more than pushing into it (choosing a browser tab).
    /// </summary>
    sealed class EdgePush
    {
        const int MaxGapMs = 100;        // no push past the edge for this long starts over
        const int SlideTolerancePx = 16; // how much further the mouse may move along the edge than into it
        bool pushing;
        int since, last;
        long pushed, slid; // pixels moved into / along the edge since the push started

        public void Reset() => pushing = false;

        /// <summary>Call for every mouse move from <paramref name="from"/> (the cursor) to <paramref name="to"/> (unclipped).</summary>
        /// <returns>True once edge <paramref name="side"/> has been pushed continuously for <paramref name="delayMs"/>.</returns>
        public bool Update(POINT from, POINT to, Side side, int delayMs)
        {
            EdgeMove move = Edges.Classify(from, to, side);
            if (move == EdgeMove.Away)
            {
                pushing = false;
                return false;
            }
            bool horizontalEdge = side == Side.Top || side == Side.Bottom;
            int along = Math.Abs(horizontalEdge ? to.X - from.X : to.Y - from.Y);
            int now = Environment.TickCount;
            if (move == EdgeMove.Past && (!pushing || unchecked(now - last) > MaxGapMs))
            {
                pushing = true;
                since = now;
                pushed = slid = 0;
            }
            if (!pushing)
                return false;

            slid += along;
            if (move == EdgeMove.Past)
            {
                last = now;
                pushed += side == Side.Left ? from.X - to.X : side == Side.Right ? to.X - from.X :
                          side == Side.Top ? from.Y - to.Y : to.Y - from.Y;
            }
            if (slid > pushed + SlideTolerancePx)
            {
                pushing = false;
                return false;
            }
            if (move == EdgeMove.Along || unchecked(now - since) < delayMs)
                return false;
            pushing = false;
            return true;
        }
    }
}
