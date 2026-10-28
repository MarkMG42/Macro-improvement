using System;
using System.Collections.Generic;
using System.Linq;
using AutoSizeDrilling;

// Dependency-free executable regression suite. Any failure returns exit code 1.
internal static class Program
{
    private static int passed, failed;
    private static void Check(bool condition, string message = "Assertion failed")
    { if (!condition) throw new Exception(message); }
    private static void Throws(Action action)
    {
        try { action(); } catch (InvalidOperationException) { return; }
        throw new Exception("Expected rejection of ambiguous geometry");
    }
    private static void Test(string name, Action test)
    {
        try { test(); passed++; Console.WriteLine("PASS " + name); }
        catch (Exception ex) { failed++; Console.WriteLine("FAIL " + name + ": " + ex.Message); }
    }
    private static List<LineInfo> Rectangle(double scale = 1) => new List<LineInfo> {
        new LineInfo(new Point2(0,0), new Point2(.2*scale,0)),
        new LineInfo(new Point2(.2*scale,0), new Point2(.2*scale,.1*scale)),
        new LineInfo(new Point2(.2*scale,.1*scale), new Point2(0,.1*scale)),
        new LineInfo(new Point2(0,.1*scale), new Point2(0,0))
    };
    private static List<Hole> Rows() => new[] { .02, .05 }.SelectMany(y =>
        new[] { .02, .08, .15 }.Select(x => new Hole(x,y,.004))).ToList();

    public static int Main()
    {
        var g = new Geometry(1);
        var bounds = g.FindBounds(Rectangle());
        Test("single row preserves three X positions and one Y", () => {
            var h = g.PrepareHoles(Rows().Take(3), bounds);
            Check(g.Positions(h,true).Count == 3 && g.Positions(h,false).Count == 1);
            Check(g.DiameterGroups(h).Single().Count == 3);
        });
        Test("two rows: six holes, three X, two Y", () => {
            var h = g.PrepareHoles(Rows(), bounds);
            Check(h.Count == 6 && g.Positions(h,true).Count == 3 && g.Positions(h,false).Count == 2);
            Check(g.DiameterGroups(h).Single().Count == 6);
        });
        Test("mixed diameters receive separate counts", () => {
            var h = new[] { new Hole(.02,.02,.004), new Hole(.08,.02,.005), new Hole(.15,.02,.004) };
            var groups = g.DiameterGroups(g.PrepareHoles(h,bounds));
            Check(groups.Count == 2 && groups[0].Count == 2 && groups[1].Count == 1);
        });
        Test("same X with different Y and diameter is not lost", () => {
            var h = g.PrepareHoles(new[] { new Hole(.02,.02,.004), new Hole(.02,.05,.005) }, bounds);
            Check(h.Count == 2 && g.DiameterGroups(h).Count == 2);
        });
        Test("coincident front and back rims are not counted twice", () => {
            var h = Rows(); h.AddRange(Rows());
            Check(g.PrepareHoles(h,bounds).Count == 6);
        });
        Test("concentric counterbore is rejected before dimension creation", () =>
            Throws(() => g.PrepareHoles(new[] {new Hole(.02,.02,.004),new Hole(.02,.02,.006)},bounds)));
        Test("hole processing is independent of enumeration order", () => {
            var a = g.PrepareHoles(Rows(),bounds);
            var b = g.PrepareHoles(Rows().AsEnumerable().Reverse(),bounds);
            Check(a.Zip(b).All(p => p.First.X == p.Second.X && p.First.Y == p.Second.Y));
        });
        Test("scale 1:100 preserves positions 1 mm apart", () => {
            var small = new Geometry(.01);
            var h = small.PrepareHoles(new[] {new Hole(.0002,.0002,.00004),new Hole(.00021,.0002,.00004)},small.FindBounds(Rectangle(.01)));
            Check(small.Positions(h,true).Count == 2);
        });
        Test("model tolerance is invariant for 1:1, 1:10, 1:100, 2:1", () => {
            foreach (var scale in new[] {1.0,.1,.01,2.0}) {
                var s = new Geometry(scale);
                var h = Rows().Select(p => new Hole(p.X*scale,p.Y*scale,p.Radius*scale));
                var normalized = s.PrepareHoles(h,s.FindBounds(Rectangle(scale)));
                Check(normalized.Count == 6 && s.DiameterGroups(normalized).Single().Count == 6);
                Check(s.MatchesModelValue(.02,.02*scale));
            }
        });
        Test("arcs cannot pass as full circles", () => {
            Check(Geometry.FullCircle(1,1+2*Math.PI));
            Check(!Geometry.FullCircle(0,Math.PI/2) && !Geometry.FullCircle(0,Math.PI));
            Check(!Geometry.FullCircle(0,double.NaN));
        });
        Test("oblique circle rejected, reversed axis accepted", () => {
            Check(g.FaceOn(new[] {0.0,0.0,0.0},new[] {0.0,0.0,-1.0}));
            Check(!g.FaceOn(new[] {0.0,0.0,0.0},new[] {0.0,.1,1.0}));
        });
        Test("internal line cannot replace missing oblique outer boundary", () => {
            var lines = Rectangle();
            lines[3] = new LineInfo(new Point2(0,0),new Point2(.01,.1));
            lines.Add(new LineInfo(new Point2(.02,0),new Point2(.02,.1)));
            Throws(() => g.FindBounds(lines));
        });
        Test("rotated rectangle is rejected", () => {
            var lines = Rectangle();
            Func<Point2, Point2> rotate = p => new Point2((p.X-p.Y)/Math.Sqrt(2),(p.X+p.Y)/Math.Sqrt(2));
            Throws(() => g.FindBounds(lines.Select(l => new LineInfo(rotate(l.A),rotate(l.B))).ToList()));
        });
        Test("curved protrusion cannot silently shrink outer dimensions", () => {
            var extrema = Geometry.CircleExtrema(new Point2(.19,.05),new Point2(.03,0),new Point2(0,.03),0,2*Math.PI);
            Throws(() => g.FindBounds(Rectangle(),extrema));
        });
        Test("partial arc extrema do not use full supporting circle", () => {
            var points = Geometry.CircleExtrema(new Point2(0,0),new Point2(1,0),new Point2(0,1),0,Math.PI/2);
            Check(points.Min(p => p.X) >= -1e-10 && points.Min(p => p.Y) >= -1e-10);
            Check(Math.Abs(points.Max(p => p.X)-1) < 1e-10 && Math.Abs(points.Max(p => p.Y)-1) < 1e-10);
        });
        Test("diameter tolerance does not merge distinct close sizes", () => {
            Check(g.DiameterGroups(new[] { new Hole(.02,.02,.004),new Hole(.08,.02,.00401) }).Count == 2);
        });
        Test("tangent value is rejected, centre value passes", () => {
            Check(g.MatchesModelValue(.02,.02));
            Check(!g.MatchesModelValue(.016,.02));
            Check(!g.MatchesModelValue(double.NaN,.02));
        });
        Test("invalid scale cannot silently fall back to 1:1", () => {
            foreach (var scale in new[] {0.0,-1,double.NaN,double.PositiveInfinity}) {
                try { new Geometry(scale); throw new Exception("Invalid scale accepted"); }
                catch (ArgumentException) { }
            }
        });
        Test("no holes still allows overall dimensions", () => {
            var h = g.PrepareHoles(Array.Empty<Hole>(),bounds);
            Check(h.Count == 0 && bounds.Right == .2 && bounds.Top == .1);
        });
        Test("outside and boundary centres are excluded", () => {
            Check(g.PrepareHoles(new[] {new Hole(0,.05,.004),new Hole(.3,.05,.004)},bounds).Count == 0);
        });
        Test("channel: short outer datum wins over long nearby inner edge", () => {
            var lines = Rectangle();
            var outer = new LineInfo(new Point2(0,0),new Point2(0,.005));
            lines[3] = outer;
            lines.Add(new LineInfo(new Point2(.00004,0),new Point2(.00004,.1)));
            Check(ReferenceEquals(g.FindBounds(lines).LeftEdge,outer));
            lines.Reverse();
            Check(ReferenceEquals(g.FindBounds(lines).LeftEdge,outer));
        });
        Test("channel: missing outer datum cannot be replaced by close inner edge", () => {
            var lines = Rectangle();
            lines[3] = new LineInfo(new Point2(.00004,0),new Point2(.00004,.1));
            Throws(() => g.FindBounds(lines));
        });
        Test("channel datum selection is unchanged by drawing scale", () => {
            foreach (double scale in new[] {1.0,.1,.01,2.0}) {
                var lines = Rectangle(scale);
                var outer = new LineInfo(new Point2(0,0),new Point2(0,.005*scale));
                lines[3] = outer;
                lines.Add(new LineInfo(new Point2(.00004*scale,0),new Point2(.00004*scale,.1*scale)));
                Check(ReferenceEquals(new Geometry(scale).FindBounds(lines).LeftEdge,outer));
            }
        });
        Console.WriteLine($"{passed} passed, {failed} failed");
        return failed == 0 ? 0 : 1;
    }
}
