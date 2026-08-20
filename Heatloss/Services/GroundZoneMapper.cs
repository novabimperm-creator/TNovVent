using System;
using System.Collections.Generic;
using System.Linq;

namespace QOVETER.Services
{
    /// <summary>Точка в плане, м.</summary>
    public struct Point2D
    {
        public double X;
        public double Y;

        public Point2D(double x, double y) { X = x; Y = y; }
    }

    /// <summary>Отрезок контура здания в плане, м.</summary>
    public struct Segment2D
    {
        public Point2D A;
        public Point2D B;

        public Segment2D(Point2D a, Point2D b) { A = a; B = b; }

        public Segment2D(double ax, double ay, double bx, double by)
        {
            A = new Point2D(ax, ay);
            B = new Point2D(bx, by);
        }
    }

    /// <summary>
    /// Раскладывает пол помещения по зонам зонального метода
    /// (СП 50.13330.2024, Г.7) — по расстоянию до контура здания.
    ///
    /// <para><b>Почему сеткой, а не построением полос.</b> Зоны — это полосы шириной
    /// 2 м вдоль контура ЗДАНИЯ, а считаем мы ПОМЕЩЕНИЯМИ. Помещение может лежать
    /// целиком в третьей зоне, может пересекать сразу три, а его собственные стены
    /// к контуру отношения не имеют. Точное построение потребовало бы смещения
    /// многоугольника контура внутрь (offset) с разрешением самопересечений —
    /// отдельная библиотека и отдельный класс ошибок. Численное интегрирование
    /// по сетке даёт тот же ответ с управляемой точностью и без единой библиотеки:
    /// шаг 0,25 м — это 16 проб на квадратный метр.</para>
    ///
    /// <para><b>Площадь не берётся из сетки.</b> Сетка даёт ДОЛИ зон, а сами
    /// площади считаются от площади помещения из модели. Иначе к погрешности
    /// зонирования добавилась бы погрешность площади — величины, которая в модели
    /// известна точно и уже используется всем остальным расчётом.</para>
    ///
    /// <para>Класс намеренно не знает про Revit: контур и многоугольник помещения
    /// собирает <see cref="GeometryCollector"/>, а раскладка по зонам проверяется
    /// автотестами на синтетике.</para>
    /// </summary>
    public static class GroundZoneMapper
    {
        /// <summary>Шаг сетки по умолчанию, м.</summary>
        public const double DefaultCellM = 0.25;

        /// <summary>
        /// Дальше этого расстояния от контура всё равно четвёртая зона, поэтому
        /// отрезки контура, отстоящие от помещения дальше, в расчёт не берутся.
        /// Это не приближение, а точное следствие правила «в четвёртую зону
        /// относят всё, не попавшее в остальные три».
        /// </summary>
        public static double ZoneIVDistanceM => GroundContact.ZoneWidthM * (GroundContact.ZoneCount - 1);

        /// <summary>
        /// Площади зон пола помещения, м².
        /// </summary>
        /// <param name="polygon">Контур помещения в плане (внешний обход), м.</param>
        /// <param name="contour">Отрезки контура ЗДАНИЯ в плане, м.</param>
        /// <param name="areaM2">Площадь помещения из модели, м² — на неё нормируются доли.</param>
        /// <param name="effectiveBandM">
        /// Эффективная полоса для пола ниже уровня земли (половина средней высоты
        /// стен в грунте), м. Ноль — пол на уровне земли.
        /// </param>
        /// <param name="cellM">Шаг сетки, м.</param>
        public static List<GroundZoneArea> FloorZones(IList<Point2D> polygon, IList<Segment2D> contour,
                                                      double areaM2, double effectiveBandM = 0,
                                                      double cellM = DefaultCellM)
        {
            var empty = new List<GroundZoneArea>();
            if (areaM2 <= 0) return empty;

            // Контура нет — зонировать не от чего. Молчаливо назначить четвёртую
            // зону нельзя: это была бы самая тёплая зона, то есть занижение потерь
            // на ровном месте. Пусть решает вызывающий.
            if (contour == null || contour.Count == 0) return empty;
            if (polygon == null || polygon.Count < 3) return empty;

            if (cellM <= 0) cellM = DefaultCellM;

            double minX = polygon.Min(p => p.X), maxX = polygon.Max(p => p.X);
            double minY = polygon.Min(p => p.Y), maxY = polygon.Max(p => p.Y);

            // Только те отрезки, что могут повлиять на зону: остальные дальше
            // третьей полосы, а там всё равно четвёртая зона.
            double reach = ZoneIVDistanceM;
            var relevant = contour
                .Where(s => DistanceToBox(s, minX, minY, maxX, maxY) <= reach)
                .ToList();

            var counts = new int[GroundContact.ZoneCount + 1];
            int total = 0;

            for (double x = minX + cellM / 2; x < maxX; x += cellM)
            {
                for (double y = minY + cellM / 2; y < maxY; y += cellM)
                {
                    var point = new Point2D(x, y);
                    if (!IsInside(polygon, point)) continue;

                    double distance = relevant.Count == 0
                        ? double.MaxValue
                        : relevant.Min(s => DistanceToSegment(point, s));

                    counts[GroundContact.ZoneOf(distance, effectiveBandM)]++;
                    total++;
                }
            }

            // Помещение мельче шага сетки (кладовая 0,8 × 0,6 м) — ни одна проба
            // внутрь не попала. Считаем его целиком по центру тяжести: это точнее,
            // чем потерять помещение, и честнее, чем измельчать сетку ради
            // единичных случаев.
            if (total == 0)
            {
                var centroid = new Point2D(polygon.Average(p => p.X), polygon.Average(p => p.Y));
                double distance = relevant.Count == 0
                    ? double.MaxValue
                    : relevant.Min(s => DistanceToSegment(centroid, s));

                return new List<GroundZoneArea>
                    { new GroundZoneArea(GroundContact.ZoneOf(distance, effectiveBandM), areaM2) };
            }

            var result = new List<GroundZoneArea>();
            for (int zone = 1; zone <= GroundContact.ZoneCount; zone++)
            {
                if (counts[zone] == 0) continue;
                result.Add(new GroundZoneArea(zone, areaM2 * counts[zone] / total));
            }

            return result;
        }

        /// <summary>Точка внутри многоугольника — лучевой алгоритм.</summary>
        public static bool IsInside(IList<Point2D> polygon, Point2D point)
        {
            bool inside = false;
            for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
            {
                double xi = polygon[i].X, yi = polygon[i].Y;
                double xj = polygon[j].X, yj = polygon[j].Y;

                bool crosses = (yi > point.Y) != (yj > point.Y) &&
                               point.X < (xj - xi) * (point.Y - yi) / (yj - yi) + xi;
                if (crosses) inside = !inside;
            }
            return inside;
        }

        /// <summary>Расстояние от точки до отрезка, м.</summary>
        public static double DistanceToSegment(Point2D p, Segment2D s)
        {
            double dx = s.B.X - s.A.X, dy = s.B.Y - s.A.Y;
            double lengthSquared = dx * dx + dy * dy;

            if (lengthSquared < 1e-12)
                return Math.Sqrt((p.X - s.A.X) * (p.X - s.A.X) + (p.Y - s.A.Y) * (p.Y - s.A.Y));

            double t = ((p.X - s.A.X) * dx + (p.Y - s.A.Y) * dy) / lengthSquared;
            t = t < 0 ? 0 : (t > 1 ? 1 : t);

            double px = s.A.X + t * dx - p.X;
            double py = s.A.Y + t * dy - p.Y;
            return Math.Sqrt(px * px + py * py);
        }

        /// <summary>
        /// НИЖНЯЯ оценка расстояния от отрезка до любой точки прямоугольника, м.
        ///
        /// <para>Именно нижняя, а не «примерная»: отсев обязан быть безопасным.
        /// Считается как расстояние от ЦЕНТРА прямоугольника до отрезка минус
        /// половина его диагонали — дальше этого ни одна точка прямоугольника
        /// к отрезку подойти не может. Проверка по концам отрезка была бы
        /// быстрее, но неверна: длинный отрезок контура может пройти вплотную
        /// к маленькому помещению, оставив оба конца далеко.</para>
        /// </summary>
        private static double DistanceToBox(Segment2D s, double minX, double minY,
                                            double maxX, double maxY)
        {
            var center = new Point2D((minX + maxX) / 2, (minY + maxY) / 2);
            double halfDiagonal = Math.Sqrt(
                (maxX - minX) * (maxX - minX) + (maxY - minY) * (maxY - minY)) / 2;

            return Math.Max(0, DistanceToSegment(center, s) - halfDiagonal);
        }
    }
}
