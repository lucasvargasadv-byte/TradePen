using System.Windows;
using System.Windows.Media;

namespace TraderPen.Tools
{
    internal static class StrokeSmoother
    {
        public static PointCollection Smooth(PointCollection source, int strength)
        {
            if (source.Count < 3 || strength <= 0)
            {
                return new PointCollection(source);
            }

            var original = new Point[source.Count];
            source.CopyTo(original, 0);
            var current = new PointCollection(source);
            int passes = strength == 1 ? 3 : 7;

            for (int pass = 0; pass < passes; pass++)
            {
                var next = new PointCollection(current.Count)
                {
                    current[0]
                };

                for (int i = 1; i < current.Count - 1; i++)
                {
                    if (IsCorner(original[i - 1], original[i], original[i + 1]))
                    {
                        next.Add(current[i]);
                        continue;
                    }

                    Point previous = current[i - 1];
                    Point point = current[i];
                    Point following = current[i + 1];
                    next.Add(new Point(
                        (previous.X + (2 * point.X) + following.X) / 4,
                        (previous.Y + (2 * point.Y) + following.Y) / 4));
                }

                next.Add(current[^1]);
                current = next;
            }

            return current;
        }

        private static bool IsCorner(Point previous, Point current, Point next)
        {
            Vector incoming = current - previous;
            Vector outgoing = next - current;
            if (incoming.Length < 0.001 || outgoing.Length < 0.001)
            {
                return true;
            }

            incoming.Normalize();
            outgoing.Normalize();
            return Vector.Multiply(incoming, outgoing) < 0.55;
        }
    }
}
