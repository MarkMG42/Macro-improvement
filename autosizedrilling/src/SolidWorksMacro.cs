using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace AutoSizeDrilling
{
    // Preserve the original VSTA entry point and GUID. This is not a standalone EXE.
    [ComVisible(true)]
    [Guid("fcaaccd5-4cd6-4682-a2d6-5f5c3d34c3d8")]
    public class SolidWorksMacro
    {
        public SldWorks swApp;
        public void main() { new AutoSizeDrill().Run(swApp); }
        public string Execute(string strDebug)
        {
            try { main(); return ""; }
            catch (Exception ex) { return ex.ToString(); }
        }
    }

    public sealed class AutoSizeDrill
    {
        private ModelDoc2 model;
        private View view;
        private MathUtility math;
        private bool assembly;
        private Geometry geometry;
        private readonly List<DisplayDimension> created = new List<DisplayDimension>();
        private readonly Dictionary<object, string> entityIds = new Dictionary<object, string>();
        private readonly Dictionary<DisplayDimension, double> expectedValues = new Dictionary<DisplayDimension, double>();
        private DiagnosticLog log;
        private string phase;
        private bool? originalProjectedDimensions;

        public void Run(SldWorks application = null)
        {
            log = new DiagnosticLog();
            try
            {
                // Use the instance supplied by VSTA rather than another running SW instance.
                var app = application ?? (SldWorks)Marshal.GetActiveObject("SldWorks.Application");
                model = app.ActiveDoc as ModelDoc2;
                if (model == null || model.GetType() != (int)swDocumentTypes_e.swDocDRAWING)
                    throw new InvalidOperationException("Откройте чертёж и выберите один чертёжный вид.");
                var selection = (SelectionMgr)model.SelectionManager;
                if (selection.GetSelectedObjectCount2(-1) != 1 ||
                    (view = selection.GetSelectedObject6(1, -1) as View) == null)
                    throw new InvalidOperationException("Выберите только один чертёжный вид.");
                if (view.ReferencedDocument == null)
                    throw new InvalidOperationException("Загрузите модель чертёжного вида (не отсоединённый чертёж).");
                assembly = view.ReferencedDocument.GetType() == (int)swDocumentTypes_e.swDocASSEMBLY;
                math = app.GetMathUtility() as MathUtility;
                if (math == null) throw new InvalidOperationException("Недоступен MathUtility.");
                geometry = new Geometry(view.ScaleDecimal);
                log.Write("scale=" + DiagnosticLog.Number(geometry.Scale) + "; assembly=" + assembly);
                originalProjectedDimensions = view.ProjectedDimensions;
                log.Write("ProjectedDimensions before=" + originalProjectedDimensions.Value);
                // Horizontal/vertical creation alone does not override a view's true-size mode.
                // Keep the selected view projected on success; restoring true-size would
                // reintroduce the user's discrepancy. Other drawing views are untouched.
                view.ProjectedDimensions = true;
                if (!view.ProjectedDimensions)
                    throw new InvalidOperationException("Не удалось включить проекционные размеры выбранного вида.");
                model.EditRebuild3();
                log.Write("ProjectedDimensions enforced=true");
                phase = "before rebuild";
                AddDimensions();
                model.EditRebuild3();
                phase = "after rebuild";
                foreach (var expected in expectedValues.ToArray()) Validate(expected.Key, expected.Value);
                model.GraphicsRedraw2();
                log.Write("SUCCESS: validated " + created.Count + " dimensions after rebuild");
            }
            catch (Exception ex)
            {
                bool rolledBack = Rollback();
                bool modeRestored = RestoreViewMode();
                log.Write("FAILURE: " + ex + "; rollback=" + rolledBack);
                string message = "AutoSizeDrilling: " + ex.Message +
                    (rolledBack ? "\nНовые размеры отменены; существующие размеры не удалялись." :
                    "\nНе все новые размеры удалось удалить. Проверьте чертёж перед сохранением.") +
                    (modeRestored ? "" : "\nНе удалось восстановить исходный режим размеров вида; проверьте его вручную.") +
                    (log.FilePath == null ? "\nНе удалось сохранить диагностический журнал." : "\nЖурнал: " + log.FilePath);
                Debug.WriteLine(ex);
                // No WinForms dependency; display through the existing SolidWorks host.
                var app = application;
                if (app != null) app.SendMsgToUser(message);
                else throw new InvalidOperationException(message, ex);
            }
            finally
            {
                try { if (model != null) model.ClearSelection2(true); }
                finally
                {
                    log.Dispose();
                    created.Clear();
                    entityIds.Clear();
                    expectedValues.Clear();
                    originalProjectedDimensions = null;
                    view = null;
                    math = null;
                    model = null;
                }
            }
        }

        private double[] Transform(double[] p, Component2 component)
        {
            var point = (MathPoint)math.CreatePoint(p);
            if (assembly)
            {
                var transform = component.Transform2;
                if (transform == null) throw new InvalidOperationException("Нет преобразования компонента сборки.");
                point = (MathPoint)point.MultiplyTransform(transform);
            }
            var viewTransform = view.ModelToViewTransform;
            if (viewTransform == null) throw new InvalidOperationException("Нет преобразования вида.");
            return (double[])((MathPoint)point.MultiplyTransform(viewTransform)).ArrayData;
        }

        private void ReadGeometry(out List<LineInfo> lines, out List<Hole> holes, out List<Point2> extrema)
        {
            lines = new List<LineInfo>();
            holes = new List<Hole>();
            extrema = new List<Point2>();
            var components = view.GetVisibleComponents() as object[];
            if (components == null || components.Length != 1)
                throw new InvalidOperationException("Нужен вид одной детали/одного видимого компонента. Размеры между деталями сборки не создаются.");
            var component = (Component2)components[0];
            var edges = view.GetVisibleEntities2(component, (int)swViewEntityType_e.swViewEntityType_Edge) as object[];
            if (edges == null) throw new InvalidOperationException("Нет доступных рёбер вида.");
            foreach (Edge edge in edges)
            {
                string id = "E" + (entityIds.Count + 1);
                entityIds[edge] = id;
                var curve = (Curve)edge.GetCurve();
                if (curve == null) throw new InvalidOperationException("Не удалось прочитать кривую ребра.");
                if (curve.IsLine())
                {
                    var a = edge.GetStartVertex() as Vertex;
                    var b = edge.GetEndVertex() as Vertex;
                    if (a == null || b == null) throw new InvalidOperationException("Не удалось прочитать концы прямого ребра.");
                    var p = Transform((double[])a.GetPoint(), component);
                    var q = Transform((double[])b.GetPoint(), component);
                    log.Write("LINE " + id + " view-start=(" + DiagnosticLog.Point(p) + ") view-end=(" + DiagnosticLog.Point(q) + ")");
                    lines.Add(new LineInfo(new Point2(p[0], p[1]), new Point2(q[0], q[1]), edge));
                }
                else if (curve.IsCircle())
                {
                    var parameters = edge.GetCurveParams3();
                    if (parameters == null) throw new InvalidOperationException("Не удалось проверить замкнутость окружности.");
                    var circle = (double[])curve.CircleParams;
                    var center = Transform(new[] { circle[0], circle[1], circle[2] }, component);
                    log.Write("CIRCLE " + id + " view-center=(" + DiagnosticLog.Point(center) + ") model-radius=" +
                        DiagnosticLog.Number(circle[6]) + " u=(" + DiagnosticLog.Number(parameters.UMinValue) + "," +
                        DiagnosticLog.Number(parameters.UMaxValue) + ")");
                    var at0 = Transform(((double[])curve.Evaluate2(0, 0)).Take(3).ToArray(), component);
                    var at90 = Transform(((double[])curve.Evaluate2(Math.PI / 2, 0)).Take(3).ToArray(), component);
                    extrema.AddRange(Geometry.CircleExtrema(new Point2(center[0], center[1]),
                        new Point2(at0[0] - center[0], at0[1] - center[1]),
                        new Point2(at90[0] - center[0], at90[1] - center[1]), parameters.UMinValue, parameters.UMaxValue));
                    if (!Geometry.FullCircle(parameters.UMinValue, parameters.UMaxValue)) continue;
                    var normal = Transform(new[] { circle[0] + circle[3], circle[1] + circle[4], circle[2] + circle[5] }, component);
                    if (!geometry.FaceOn(center, normal)) continue;
                    // A reversed cylindrical face is an inner wall. Bosses/fillets are not holes.
                    var faces = edge.GetTwoAdjacentFaces2() as object[];
                    bool innerCylinder = false;
                    if (faces == null) throw new InvalidOperationException("Не удалось прочитать грани окружности.");
                    foreach (Face2 face in faces)
                    {
                        if (face == null) continue;
                        var surface = face.GetSurface() as Surface;
                        if (surface != null && surface.IsCylinder() && face.FaceInSurfaceSense()) innerCylinder = true;
                    }
                    if (innerCylinder) holes.Add(new Hole(center[0], center[1], circle[6] * geometry.Scale, edge));
                }
                else
                {
                    // Unsupported splines/ellipses may define the true outer contour.
                    throw new InvalidOperationException("Вид содержит сплайн/эллипс или другую неподдерживаемую кривую. Автоматический габарит неоднозначен.");
                }
            }
        }

        private void AddDimensions()
        {
            ReadGeometry(out var lines, out var candidates, out var extrema);
            var b = geometry.FindBounds(lines, extrema);
            log.Write("DATUM left=" + EntityId(b.LeftEdge.Entity) + " right=" + EntityId(b.RightEdge.Entity) +
                " bottom=" + EntityId(b.BottomEdge.Entity) + " top=" + EntityId(b.TopEdge.Entity));
            var holes = geometry.PrepareHoles(candidates, b);
            var xPositions = geometry.Positions(holes, true);
            var yPositions = geometry.Positions(holes, false);
            for (int i = 0; i < xPositions.Count; i++)
            {
                var h = xPositions[i];
                AddLinear(b.LeftEdge.Entity, h.Entity, true, (b.Left + h.X) / 2,
                    b.Bottom - 0.01 - i * 0.008, h.X - b.Left, true);
            }
            AddLinear(b.LeftEdge.Entity, b.RightEdge.Entity, true, (b.Left + b.Right) / 2,
                b.Bottom - 0.01 - xPositions.Count * 0.008, b.Right - b.Left, false);
            for (int i = 0; i < yPositions.Count; i++)
            {
                var h = yPositions[i];
                AddLinear(b.BottomEdge.Entity, h.Entity, false, b.Right + 0.01 + i * 0.008,
                    (b.Bottom + h.Y) / 2, h.Y - b.Bottom, true);
            }
            AddLinear(b.BottomEdge.Entity, b.TopEdge.Entity, false,
                b.Right + 0.01 + yPositions.Count * 0.008, (b.Bottom + b.Top) / 2,
                b.Top - b.Bottom, false);
            var groups = geometry.DiameterGroups(holes);
            for (int i = 0; i < groups.Count; i++)
            {
                var h = groups[i][0];
                model.ClearSelection2(true);
                Select(h.Entity, false);
                var display = model.AddDiameterDimension2(h.X, b.Top + 0.01 + i * 0.008, 0) as DisplayDimension;
                Register(display);
                if (display.Type2 != (int)swDimensionType_e.swDiameterDimension)
                    throw new InvalidOperationException("SolidWorks создал размер другого типа вместо диаметра.");
                Validate(display, 2 * h.Radius);
                if (groups[i].Count > 1)
                {
                    string prefix = groups[i].Count + " X ";
                    display.SetText((int)swDimensionTextParts_e.swDimensionTextPrefix, prefix);
                    if (display.GetText((int)swDimensionTextParts_e.swDimensionTextPrefix) != prefix)
                        throw new InvalidOperationException("Не удалось установить количество отверстий.");
                }
            }
            // Keep explicit placement; do not invoke an unverified late-bound auto-arrange API.
        }

        private void Select(object entity, bool append)
        {
            // The same model edge can occur in multiple drawing views.
            log.Write("SELECT " + EntityId(entity) + " append=" + append);
            if (!view.SelectEntity(entity, append)) throw new InvalidOperationException("Не удалось выбрать ребро в нужном виде.");
        }

        private string EntityId(object entity) => entityIds.TryGetValue(entity, out var id) ? id : "unknown";

        private void Register(DisplayDimension display)
        {
            if (display == null) throw new InvalidOperationException("SolidWorks не создал размер.");
            created.Add(display);
            model.ClearSelection2(true);
        }

        private void AddLinear(object a, object b, bool horizontal, double x, double y, double expected, bool toCircle)
        {
            log.Write("CREATE " + (horizontal ? "AddHorizontalDimension2" : "AddVerticalDimension2") +
                " datum=" + EntityId(a) + " target=" + EntityId(b) + " expected_model_mm=" +
                DiagnosticLog.Number(expected / geometry.Scale * 1000));
            model.ClearSelection2(true);
            Select(a, false);
            Select(b, true);
            var display = (horizontal ? model.AddHorizontalDimension2(x, y, 0) :
                model.AddVerticalDimension2(x, y, 0)) as DisplayDimension;
            Register(display);
            var dim = display.GetDimension2(0);
            if (dim == null) throw new InvalidOperationException("Нет данных созданного размера.");
            if (toCircle)
            {
                // A line-circle pair has only one arc. Set all supported arc slots to centre;
                // Validate below still rejects a tangent or an unexpected attachment.
                for (int index = 0; index < 2; index++)
                {
                    int condition = dim.GetArcEndCondition(index);
                    if (condition >= (int)swArcEndCondition_e.swArcEndConditionCenter &&
                        condition <= (int)swArcEndCondition_e.swArcEndConditionMax)
                        dim.SetArcEndCondition(index, (int)swArcEndCondition_e.swArcEndConditionCenter);
                }
            }
            Validate(display, expected);
        }

        private void Validate(DisplayDimension display, double expected)
        {
            var dim = display.GetDimension2(0);
            if (dim == null) throw new InvalidOperationException("Нет данных созданного размера.");
            double actual = dim.SystemValue;
            log.Write("VALIDATE " + phase + " index=" + created.IndexOf(display) + " type=" + display.Type2 +
                " expected_model_mm=" + DiagnosticLog.Number(expected / geometry.Scale * 1000) +
                " actual_model_mm=" + DiagnosticLog.Number(actual * 1000));
            if (!geometry.MatchesModelValue(actual, expected))
                throw new InvalidOperationException(string.Format(
                    "Созданный размер не совпал с геометрией: ожидалось {0:0.######} мм, получено {1:0.######} мм. Возможна привязка к касательной или неподдерживаемый вид.",
                    expected / geometry.Scale * 1000, actual * 1000));
            expectedValues[display] = expected;
        }

        private bool Rollback()
        {
            bool ok = true;
            for (int i = created.Count - 1; i >= 0; i--)
            {
                try
                {
                    model.ClearSelection2(true);
                    var annotation = created[i].GetAnnotation() as Annotation;
                    if (annotation == null || !annotation.Select3(false, null) || !model.DeleteSelection(false)) ok = false;
                }
                catch (Exception ex) { Debug.WriteLine(ex); ok = false; }
            }
            return ok;
        }

        private bool RestoreViewMode()
        {
            if (!originalProjectedDimensions.HasValue || view == null) return true;
            try
            {
                view.ProjectedDimensions = originalProjectedDimensions.Value;
                model.EditRebuild3();
                model.GraphicsRedraw2();
                bool restored = view.ProjectedDimensions == originalProjectedDimensions.Value;
                log.Write("ProjectedDimensions restored=" + restored + "; original=" + originalProjectedDimensions.Value);
                return restored;
            }
            catch (Exception ex) { log.Write("Restore view mode failed: " + ex); return false; }
        }
    }
}
