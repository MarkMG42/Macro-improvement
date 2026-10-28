using System;
using System.Collections.Generic;
using System.Linq;

namespace AutoSizeDrilling
{
    // All geometry is in sheet metres; tolerances are specified in model metres.
    public sealed class Point2
    {
        public double X, Y;
        public Point2(double x, double y) { X = x; Y = y; }
    }

    public sealed class LineInfo
    {
        public Point2 A, B;
        public object Entity;
        public LineInfo(Point2 a, Point2 b, object entity = null) { A = a; B = b; Entity = entity; }
    }

    public sealed class Hole
    {
        public double X, Y, Radius;
        public object Entity;
        public Hole(double x, double y, double radius, object entity = null)
        { X = x; Y = y; Radius = radius; Entity = entity; }
    }

    public sealed class Bounds
    {
        public double Left, Right, Bottom, Top;
        public LineInfo LeftEdge, RightEdge, BottomEdge, TopEdge;
    }

    public sealed class Geometry
    {
        public const double ModelTolerance = 0.00005; // 0.05 mm, as in the original
        public const double ModelDiameterTolerance = 0.000001; // 0.001 mm
        public double Scale { get; }
        public double Tolerance => ModelTolerance * Scale;
        public double DiameterTolerance => ModelDiameterTolerance * Scale;

        public Geometry(double scale)
        {
            if (!Finite(scale) || scale <= 0) throw new ArgumentException("Некорректный масштаб вида.");
            Scale = scale;
        }

        public static bool Finite(double x) => !double.IsNaN(x) && !double.IsInfinity(x);

        public static bool FullCircle(double uMin, double uMax) =>
            Finite(uMin) && Finite(uMax) && Math.Abs(Math.Abs(uMax - uMin) - 2 * Math.PI) <= 1e-6;

        public bool FaceOn(double[] center, double[] normalPoint)
        {
            double dx = normalPoint[0] - center[0], dy = normalPoint[1] - center[1];
            double dz = normalPoint[2] - center[2];
            double length = Math.Sqrt(dx * dx + dy * dy + dz * dz);
            return Finite(length) && length > 0 && Math.Sqrt(dx * dx + dy * dy) / length <= 1e-6;
        }

        public Bounds FindBounds(IReadOnlyList<LineInfo> lines, IEnumerable<Point2> curvedExtrema = null)
        {
            if (lines.Count == 0) throw new InvalidOperationException("Нет прямолинейного контура.");
            if (lines.Any(l => !Finite(l.A.X) || !Finite(l.A.Y) || !Finite(l.B.X) || !Finite(l.B.Y)))
                throw new InvalidOperationException("Некорректные координаты контура.");
            var points = lines.SelectMany(l => new[] { l.A, l.B }).Concat(curvedExtrema ?? Enumerable.Empty<Point2>()).ToList();
            if (points.Any(p => !Finite(p.X) || !Finite(p.Y))) throw new InvalidOperationException("Некорректные экстремумы контура.");
            var b = new Bounds {
                Left = points.Min(p => p.X), Right = points.Max(p => p.X),
                Bottom = points.Min(p => p.Y), Top = points.Max(p => p.Y)
            };
            // Pick the actual extreme first. A long nearby inner edge must not outrank
            // a short outer edge, as can happen around channel flanges/fillets.
            b.LeftEdge = Boundary(lines, true, b.Left);
            b.RightEdge = Boundary(lines, true, b.Right);
            b.BottomEdge = Boundary(lines, false, b.Bottom);
            b.TopEdge = Boundary(lines, false, b.Top);
            if (b.Right - b.Left <= Tolerance || b.Top - b.Bottom <= Tolerance ||
                b.LeftEdge == null || b.RightEdge == null || b.BottomEdge == null || b.TopEdge == null)
                throw new InvalidOperationException("Неоднозначный контур: нужен ортогональный вид с четырьмя крайними прямыми рёбрами. Поворот или косой торец автоматически не размеряются.");
            return b;
        }

        private LineInfo Boundary(IEnumerable<LineInfo> lines, bool vertical, double extreme)
        {
            Func<LineInfo, double> offset = l => Math.Abs((vertical ? l.A.X + l.B.X : l.A.Y + l.B.Y) / 2 - extreme);
            return lines.Where(l => {
                double length = Distance(l.A, l.B);
                double cross = Math.Abs(vertical ? l.A.X - l.B.X : l.A.Y - l.B.Y);
                return length > Tolerance && cross <= length * 1e-6 &&
                    offset(l) <= DiameterTolerance;
            }).OrderBy(offset).ThenByDescending(l => Distance(l.A, l.B)).FirstOrDefault();
        }

        public List<Hole> PrepareHoles(IEnumerable<Hole> candidates, Bounds bounds)
        {
            var result = new List<Hole>();
            foreach (var h in candidates.OrderBy(h => h.X).ThenBy(h => h.Y).ThenBy(h => h.Radius))
            {
                if (!Finite(h.X) || !Finite(h.Y) || !Finite(h.Radius) || h.Radius <= 0)
                    throw new InvalidOperationException("Некорректная окружность.");
                if (h.X <= bounds.Left + Tolerance || h.X >= bounds.Right - Tolerance ||
                    h.Y <= bounds.Bottom + Tolerance || h.Y >= bounds.Top - Tolerance) continue;
                var sameCenter = result.Find(p => Math.Abs(p.X - h.X) <= Tolerance && Math.Abs(p.Y - h.Y) <= Tolerance);
                if (sameCenter != null)
                {
                    if (Math.Abs(2 * sameCenter.Radius - 2 * h.Radius) > DiameterTolerance)
                        throw new InvalidOperationException("Концентрические окружности разных диаметров: возможна зенковка. Выбор отверстия вручную обязателен.");
                    continue; // coincident front/back rims are one projected hole
                }
                result.Add(h);
            }
            return result;
        }

        // Projected circular curve p(u) = centre + cosine*cos(u) + sine*sin(u).
        // Include endpoints and exact coordinate extrema, even on partial arcs.
        public static List<Point2> CircleExtrema(Point2 center, Point2 cosine, Point2 sine, double min, double max)
        {
            if (!Finite(min) || !Finite(max) || max < min || max - min > 2 * Math.PI + 1e-6)
                throw new InvalidOperationException("Неподдерживаемый интервал кругового ребра.");
            var parameters = new List<double> { min, max };
            foreach (double root in new[] { Math.Atan2(sine.X, cosine.X), Math.Atan2(sine.Y, cosine.Y) })
            {
                double first = Math.Ceiling((min - root) / Math.PI);
                for (int k = 0; k < 4; k++)
                {
                    double u = root + (first + k) * Math.PI;
                    if (u <= max) parameters.Add(u);
                }
            }
            return parameters.Select(u => new Point2(center.X + cosine.X * Math.Cos(u) + sine.X * Math.Sin(u),
                center.Y + cosine.Y * Math.Cos(u) + sine.Y * Math.Sin(u))).ToList();
        }

        public List<Hole> Positions(IEnumerable<Hole> holes, bool horizontal)
        {
            Func<Hole, double> key = h => horizontal ? h.X : h.Y;
            var result = new List<Hole>();
            foreach (var h in holes.OrderBy(key).ThenBy(h => horizontal ? h.Y : h.X))
                if (!result.Any(p => Math.Abs(key(p) - key(h)) <= Tolerance)) result.Add(h);
            return result;
        }

        public List<List<Hole>> DiameterGroups(IEnumerable<Hole> holes)
        {
            var groups = new List<List<Hole>>();
            foreach (var h in holes.OrderBy(h => h.Radius).ThenBy(h => h.X).ThenBy(h => h.Y))
            {
                var group = groups.Find(g => Math.Abs(2 * g[0].Radius - 2 * h.Radius) <= DiameterTolerance);
                if (group == null) groups.Add(new List<Hole> { h }); else group.Add(h);
            }
            return groups;
        }

        public bool MatchesModelValue(double actual, double expectedSheetValue) =>
            Finite(actual) && Math.Abs(actual - expectedSheetValue / Scale) <= ModelDiameterTolerance;

        private static double Distance(Point2 a, Point2 b) =>
            Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
    }
}
