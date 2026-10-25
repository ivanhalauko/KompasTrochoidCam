using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using KompasTrochoidCam.Models;

namespace KompasTrochoidCam.Services
{
    /// <summary>
    /// Подключение к запущенному КОМПАС-3D через внешний COM API 7.
    /// Чтение выполняется поздним связыванием (reflection), поэтому ссылки на
    /// библиотеки КОМПАС не требуются при сборке.
    /// </summary>
    public class KompasService
    {
        private object? _kompasApp;
        private object? _activeDocument;

        // Нативная функция Windows для извлечения активного COM-объекта из ROT.
        [DllImport("oleaut32.dll", PreserveSig = false)]
        private static extern void GetActiveObject(ref Guid rclsid, IntPtr pReserved, [MarshalAs(UnmanagedType.Interface)] out object ppunk);

        public bool IsConnected => _kompasApp != null;

        public string ActiveDocumentName => GetActiveDocName();

        private object? GetProperty(object? target, string propertyName, object[]? args = null)
        {
            if (target == null) return null;
            try
            {
                return target.GetType().InvokeMember(
                    propertyName,
                    BindingFlags.GetProperty | BindingFlags.Public | BindingFlags.Instance,
                    null,
                    target,
                    args);
            }
            catch
            {
                return null;
            }
        }

        private static object? GetItem(object? collection, int index)
        {
            if (collection == null) return null;
            try
            {
                return collection.GetType().InvokeMember(
                    "Item",
                    BindingFlags.InvokeMethod | BindingFlags.Public | BindingFlags.Instance,
                    null,
                    collection,
                    new object[] { index });
            }
            catch
            {
                return null;
            }
        }

        private static int GetCount(object? collection)
        {
            if (collection == null) return 0;
            try
            {
                object? count = collection.GetType().InvokeMember(
                    "Count",
                    BindingFlags.GetProperty | BindingFlags.Public | BindingFlags.Instance,
                    null,
                    collection,
                    null);
                return count == null ? 0 : Convert.ToInt32(count, CultureInfo.InvariantCulture);
            }
            catch
            {
                return 0;
            }
        }

        private static double ToDouble(object? value)
        {
            if (value == null) return 0;
            try { return Convert.ToDouble(value, CultureInfo.InvariantCulture); }
            catch { return 0; }
        }

        private static int ToInt(object? value)
        {
            if (value == null) return 0;
            try { return Convert.ToInt32(value, CultureInfo.InvariantCulture); }
            catch { return 0; }
        }

        private static TrochoidEngine.Point2D ToPoint2D(object? point)
            => new TrochoidEngine.Point2D(ToDouble(GetMember(point, "X")), ToDouble(GetMember(point, "Y")));

        private static object? GetMember(object? target, string name)
        {
            if (target == null) return null;
            try
            {
                return target.GetType().InvokeMember(
                    name,
                    BindingFlags.GetProperty | BindingFlags.Public | BindingFlags.Instance,
                    null,
                    target,
                    null);
            }
            catch
            {
                return null;
            }
        }

        public bool Connect(out string errorMessage)
        {
            errorMessage = "Неизвестная ошибка";
            try
            {
                // GUID интерфейса "Kompas.Application.7" (одинаков для версий 17–23).
                Guid clsid = new Guid("E26CE0D3-162F-4A93-87CE-52E2010A3816");

                try
                {
                    GetActiveObject(ref clsid, IntPtr.Zero, out object kompasObject);
                    _kompasApp = kompasObject;
                }
                catch (Exception ex)
                {
                    errorMessage = $"КОМПАС-3D не запущен в системе или заблокирован Windows: {ex.Message}";
                    return false;
                }

                if (_kompasApp == null)
                {
                    errorMessage = "Не удалось связаться с COM-сервером АСКОН.";
                    return false;
                }

                try
                {
                    _activeDocument = GetProperty(_kompasApp, "ActiveDocument");
                }
                catch (Exception ex)
                {
                    errorMessage = $"Связь установлена, но произошёл сбой чтения ActiveDocument: {ex.Message}";
                    return false;
                }

                if (_activeDocument == null)
                {
                    errorMessage = "КОМПАС успешно подключён, но в нём не открыта ни одна деталь (.m3d)!";
                    return false;
                }

                errorMessage = "Успешно подключено";
                return true;
            }
            catch (Exception ex)
            {
                errorMessage = $"Критический сбой: {ex.Message}";
                return false;
            }
        }

        public string GetActiveDocName()
        {
            if (_activeDocument == null) return "Нет подключения";
            try
            {
                var nameObj = GetProperty(_activeDocument, "Name");
                return nameObj?.ToString() ?? "Без имени";
            }
            catch
            {
                return "Ошибка чтения имени";
            }
        }

        /// <summary>Перечитывает указатель на активный документ (пользователь мог переключить файл).</summary>
        private void RefreshActiveDocument()
        {
            if (_kompasApp == null) return;
            var document = GetProperty(_kompasApp, "ActiveDocument");
            if (document != null) _activeDocument = document;
        }

        /// <summary>
        /// Читает всю геометрию активной детали: плоские грани с контурами и рёбра тел.
        /// </summary>
        public PartGeometry ReadPartGeometry(out string message)
        {
            var geometry = new PartGeometry
            {
                LoadedAt = DateTime.Now,
                Source = "kompas"
            };

            if (_kompasApp == null)
            {
                message = "Нет подключения к КОМПАС-3D";
                return geometry;
            }

            RefreshActiveDocument();

            if (_activeDocument == null)
            {
                message = "В КОМПАС-3D не открыт документ";
                return geometry;
            }

            geometry.DocumentName = GetActiveDocName();

            try
            {
                object? topPart = GetProperty(_activeDocument, "TopPart");
                object? bodies = GetProperty(topPart, "Bodies");
                int bodyCount = GetCount(bodies);

                if (bodyCount == 0)
                {
                    message = "В документе нет тел. Откройте деталь .m3d с твёрдым телом.";
                    return geometry;
                }

                int faceId = 0;
                int edgeId = 0;
                var loopEdges = new List<PartEdge>();

                for (int bodyIndex = 0; bodyIndex < bodyCount; bodyIndex++)
                {
                    object? body = GetItem(bodies, bodyIndex);
                    if (body == null) continue;

                    object? faces = GetProperty(body, "Faces");
                    int faceCount = GetCount(faces);

                    for (int faceIndex = 0; faceIndex < faceCount; faceIndex++)
                    {
                        object? face = GetItem(faces, faceIndex);
                        if (face == null) continue;
                        var partFace = ReadFace(face, ++faceId, loopEdges);
                        if (partFace != null) geometry.Faces.Add(partFace);
                    }

                    object? bodyEdges = GetProperty(body, "Edges");
                    int bodyEdgeCount = GetCount(bodyEdges);

                    for (int edgeIndex = 0; edgeIndex < bodyEdgeCount; edgeIndex++)
                    {
                        object? edge = GetItem(bodyEdges, edgeIndex);
                        var partEdge = ReadEdge(edge, ++edgeId, -1);
                        if (partEdge != null) geometry.Edges.Add(partEdge);
                    }
                }

                // Если тело не отдало коллекцию рёбер, берём рёбра из контуров граней.
                if (geometry.Edges.Count == 0 && loopEdges.Count > 0)
                {
                    foreach (var edge in loopEdges)
                    {
                        edge.Id = ++edgeId;
                        geometry.Edges.Add(edge);
                    }
                }

                geometry.RecalculateBounds();

                message = geometry.IsEmpty
                    ? "В активном документе не найдено плоских граней и рёбер."
                    : $"Геометрия загружена: граней {geometry.Faces.Count}, рёбер {geometry.Edges.Count}";

                return geometry;
            }
            catch (Exception ex)
            {
                message = $"Ошибка чтения геометрии: {ex.Message}";
                return geometry;
            }
        }

        private PartFace? ReadFace(object face, int id, List<PartEdge> loopEdges)
        {
            object? surface = GetProperty(face, "Geometry");
            int surfaceType = ToInt(GetProperty(surface, "SurfaceType"));

            // 1 = плоская поверхность. Неизвестный тип (0) тоже пробуем прочитать.
            if (surfaceType != 1 && surfaceType != 0) return null;

            object? origin = GetProperty(surface, "Origin");
            double z = ToDouble(GetMember(origin, "Z"));

            double normalX = 0, normalY = 0, normalZ = 1;
            object? normal = GetProperty(surface, "Normal");
            if (normal != null)
            {
                normalX = ToDouble(GetMember(normal, "X"));
                normalY = ToDouble(GetMember(normal, "Y"));
                normalZ = ToDouble(GetMember(normal, "Z"));
            }
            else
            {
                // Запасной путь: нормаль = AxisX × AxisY.
                var axisX = GetProperty(surface, "AxisX");
                var axisY = GetProperty(surface, "AxisY");
                if (axisX != null && axisY != null)
                {
                    double ax = ToDouble(GetMember(axisX, "X")), ay = ToDouble(GetMember(axisX, "Y")), az = ToDouble(GetMember(axisX, "Z"));
                    double bx = ToDouble(GetMember(axisY, "X")), by = ToDouble(GetMember(axisY, "Y")), bz = ToDouble(GetMember(axisY, "Z"));
                    normalX = ay * bz - az * by;
                    normalY = az * bx - ax * bz;
                    normalZ = ax * by - ay * bx;

                    double length = Math.Sqrt(normalX * normalX + normalY * normalY + normalZ * normalZ);
                    if (length > 1e-9)
                    {
                        normalX /= length; normalY /= length; normalZ /= length;
                    }
                    else
                    {
                        normalX = normalY = 0; normalZ = 1;
                    }
                }
            }

            var partFace = new PartFace
            {
                Id = id,
                Name = $"Грань #{id}",
                Z = z,
                NormalX = normalX,
                NormalY = normalY,
                NormalZ = normalZ
            };

            object? loops = GetProperty(face, "Loops");
            int loopCount = GetCount(loops);

            for (int loopIndex = 0; loopIndex < loopCount; loopIndex++)
            {
                object? loop = GetItem(loops, loopIndex);
                if (loop == null) continue;

                bool isOuter = loopIndex == 0;
                object? outerFlag = GetProperty(loop, "IsOuter");
                if (outerFlag is bool flag) isOuter = flag;

                var partLoop = new PartLoop { IsOuter = isOuter };
                object? edges = GetProperty(loop, "Edges");
                int edgeCount = GetCount(edges);

                for (int edgeIndex = 0; edgeIndex < edgeCount; edgeIndex++)
                {
                    object? edge = GetItem(edges, edgeIndex);
                    var partEdge = ReadEdge(edge, 0, id);
                    if (partEdge == null) continue;

                    loopEdges.Add(partEdge);

                    var sampled = GeometryMath.SampleEdge(partEdge);
                    foreach (var point in sampled)
                    {
                        if (partLoop.Points.Count == 0 || !GeometryMath.SamePoint(partLoop.Points[partLoop.Points.Count - 1], point))
                            partLoop.Points.Add(point);
                    }
                }

                var normalized = GeometryMath.NormalizeLoop(partLoop.Points);
                if (normalized.Count >= 3)
                {
                    partLoop.Points = normalized;
                    partFace.Loops.Add(partLoop);
                }
            }

            if (partFace.Loops.Count == 0) return null;

            partFace.Area = GeometryMath.FaceArea(partFace);
            return partFace;
        }

        private PartEdge? ReadEdge(object? edge, int id, int faceId)
        {
            if (edge == null) return null;

            object? edgeGeometry = GetProperty(edge, "Geometry");
            object? startPoint = GetProperty(edgeGeometry, "PointStart");
            object? endPoint = GetProperty(edgeGeometry, "PointEnd");

            if (startPoint == null && endPoint == null) return null;

            var start = ToPoint2D(startPoint ?? endPoint);
            var end = ToPoint2D(endPoint ?? startPoint);

            var partEdge = new PartEdge
            {
                Id = id,
                Name = $"Ребро #{id}",
                Kind = PartCurveKind.Line,
                Start = start,
                End = end,
                Z = ToDouble(GetMember(startPoint ?? endPoint, "Z")),
                FaceId = faceId
            };

            object? center = GetProperty(edgeGeometry, "Center");
            object? radiusValue = GetProperty(edgeGeometry, "Radius");

            if (center != null && radiusValue != null)
            {
                double radius = ToDouble(radiusValue);
                if (radius > 1e-6)
                {
                    partEdge.Kind = PartCurveKind.Arc;
                    partEdge.Center = ToPoint2D(center);
                    partEdge.Radius = radius;
                    partEdge.Clockwise = true;

                    double a0 = Math.Atan2(start.Y - partEdge.Center.Y, start.X - partEdge.Center.X);
                    double a1 = Math.Atan2(end.Y - partEdge.Center.Y, end.X - partEdge.Center.X);
                    double ccw = GeometryMath.NormalizeAngle(a1 - a0);

                    bool fullCircle = GeometryMath.SamePoint(start, end, 1e-3);
                    if (fullCircle)
                    {
                        partEdge.Sweep = Math.PI * 2;
                    }
                    else if (ccw <= Math.PI)
                    {
                        // Кратчайшая дуга против часовой.
                        partEdge.Clockwise = false;
                        partEdge.Sweep = ccw;
                    }
                    else
                    {
                        // Кратчайшая дуга по часовой.
                        partEdge.Clockwise = true;
                        partEdge.Sweep = Math.PI * 2 - ccw;
                    }
                }
            }

            return partEdge;
        }

        /// <summary>
        /// Устаревший сценарий: контур выделенной плоской грани.
        /// Оставлен для совместимости; основной путь — <see cref="ReadPartGeometry"/>.
        /// </summary>
        public List<TrochoidEngine.Point2D> GetSelectedFaceContours(out double faceZ)
        {
            faceZ = 0.0;
            var points = new List<TrochoidEngine.Point2D>();
            if (_activeDocument == null) return points;

            try
            {
                object? selectionManager = GetProperty(_activeDocument, "SelectionManager");
                object? selectedObjects = GetProperty(selectionManager, "SelectedObjects");
                int count = GetCount(selectedObjects);
                if (count == 0) return points;

                object? selectedObject = GetItem(selectedObjects, 0);
                if (selectedObject == null) return points;

                int objType = ToInt(GetProperty(selectedObject, "ObjectType"));
                if (objType != 4) return points; // oType_Face

                object? surface = GetProperty(selectedObject, "Geometry");
                int surfaceType = ToInt(GetProperty(surface, "SurfaceType"));
                if (surfaceType != 1) return points;

                object? planeOrigin = GetProperty(surface, "Origin");
                faceZ = ToDouble(GetMember(planeOrigin, "Z"));

                object? loops = GetProperty(selectedObject, "Loops");
                int loopsCount = GetCount(loops);
                if (loopsCount == 0) return points;

                object? outerLoop = GetItem(loops, 0);
                object? edges = GetProperty(outerLoop, "Edges");
                int edgesCount = GetCount(edges);

                for (int i = 0; i < edgesCount; i++)
                {
                    object? edge = GetItem(edges, i);
                    object? edgeGeometry = GetProperty(edge, "Geometry");
                    object? startPoint = GetProperty(edgeGeometry, "PointStart");
                    if (startPoint == null) continue;
                    points.Add(ToPoint2D(startPoint));
                }

                if (points.Count > 0) points.Add(points[0]);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Ошибка парсинга грани: {ex.Message}");
            }

            return points;
        }
    }
}
