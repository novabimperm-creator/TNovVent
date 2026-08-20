using System;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;

namespace QOVETER.Services
{
    /// <summary>
    /// Определение стороны света для стены или помещения с учётом TrueNorth.
    /// Заменяет некорректные методы, которые брали угол по центру BoundingBox комнаты
    /// относительно (0,0) проекта — это давало бессмысленный результат.
    ///
    /// Алгоритм:
    /// 1. Берём нормаль наружу к стене (Wall.Orientation для стены, нормаль граничного
    ///    сегмента для помещения).
    /// 2. Поправляем угол на ProjectLocation.Angle, чтобы перейти от Project North к True North.
    /// 3. Конвертируем угол в стандартный compass bearing (0=Север, 90=Восток,...).
    /// 4. Классифицируем в один из 8 секторов.
    /// </summary>
    public class OrientationCalculator
    {
        private readonly double _trueNorthAngleRad;

        public OrientationCalculator(Document document)
        {
            _trueNorthAngleRad = ReadTrueNorthAngle(document);

            // Угол между Project North и True North пишем в журнал: от него зависят
            // ВСЕ стороны света, а знак `pos.Angle` проверен только на типовом
            // расположении площадки. Без этой строки спор «север или юг» упирается
            // в то, что величину никто не видел.
            Logger.Info(
                $"TrueNorth: угол Project→True = {_trueNorthAngleRad * 180.0 / Math.PI:F2}° " +
                $"({_trueNorthAngleRad:F4} рад). Сторона света для нормали (0;1) = " +
                $"«{FromNormal(new XYZ(0, 1, 0))}», для (1;0) = «{FromNormal(new XYZ(1, 0, 0))}»");
        }

        private static double ReadTrueNorthAngle(Document doc)
        {
            if (doc == null) return 0;
            try
            {
                var loc = doc.ActiveProjectLocation;
                if (loc == null) return 0;
                var pos = loc.GetProjectPosition(XYZ.Zero);
                // pos.Angle — угол между Project North и True North, в радианах.
                // Знак зависит от того, как ориентирована модель; алгоритм ниже
                // проверен на типовом расположении (Project North = True North при Angle=0).
                return pos?.Angle ?? 0;
            }
            catch (Exception ex)
            {
                Logger.Warn("OrientationCalculator: не удалось прочитать TrueNorth", ex);
                return 0;
            }
        }

        /// <summary>Сторона света для нормали (XY, в координатах модели/Project).</summary>
        public string FromNormal(XYZ projectNormal)
        {
            if (projectNormal == null) return "Север";
            double len = Math.Sqrt(projectNormal.X * projectNormal.X + projectNormal.Y * projectNormal.Y);
            if (len < 1e-6) return "Север";

            double angleProjectFromX = Math.Atan2(projectNormal.Y, projectNormal.X);
            double angleTrueFromX = angleProjectFromX + _trueNorthAngleRad;

            // bearing меряется от Севера (+Y True) по часовой стрелке.
            double bearingDeg = 90.0 - angleTrueFromX * 180.0 / Math.PI;
            bearingDeg = Normalize360(bearingDeg);
            return BearingToCardinal(bearingDeg);
        }

        /// <summary>Сторона света для стены — по её наружной нормали.</summary>
        public string FromWall(Wall wall)
        {
            if (wall == null) return "Север";
            try
            {
                return FromNormal(wall.Orientation);
            }
            catch (Exception ex)
            {
                Logger.Debug($"OrientationCalculator.FromWall: {ex.Message}");
                return "Север";
            }
        }

        /// <summary>
        /// Сторона света помещения — по нормали первой наружной стены, в которой есть окно
        /// (приоритет), иначе любой первой наружной стены границы.
        /// </summary>
        public string FromRoom(Room room)
        {
            if (room == null) return "Север";
            try
            {
                var doc = room.Document;
                var boundaries = room.GetBoundarySegments(new SpatialElementBoundaryOptions());
                if (boundaries == null) return "Север";

                Wall firstExternalWall = null;
                Wall firstWallWithWindow = null;

                foreach (var loop in boundaries)
                {
                    foreach (var seg in loop)
                    {
                        var wall = doc.GetElement(seg.ElementId) as Wall;
                        if (wall == null) continue;

                        // Функция стены живёт на ТИПЕ: на экземпляре get_Parameter
                        // возвращает null, и проверка не срабатывала никогда —
                        // firstExternalWall оставался пустым, а метод всегда отдавал
                        // «Север». Ошибка была замаскирована тем, что ориентацию
                        // помещения потом перезаписывает доминирующая стена из
                        // GeometryCollector; здесь она проявлялась только у помещений,
                        // где стены собрать не удалось.
                        bool isExternal = false;
                        try
                        {
                            var fp = wall.WallType?.get_Parameter(BuiltInParameter.FUNCTION_PARAM);
                            isExternal = fp?.AsInteger() == (int)WallFunction.Exterior;
                        }
                        catch (Exception ex) { Logger.Debug($"FUNCTION_PARAM: {ex.Message}"); }

                        if (!isExternal) continue;
                        if (firstExternalWall == null) firstExternalWall = wall;

                        // Проверка: есть ли окно в этой стене?
                        if (firstWallWithWindow == null)
                        {
                            var inserts = wall.FindInserts(true, false, true, false);
                            foreach (var insertId in inserts)
                            {
                                var inserted = doc.GetElement(insertId);
                                if (inserted?.Category?.Id?.IntegerValue == (int)BuiltInCategory.OST_Windows)
                                {
                                    firstWallWithWindow = wall;
                                    break;
                                }
                            }
                        }
                    }
                }

                var preferred = firstWallWithWindow ?? firstExternalWall;
                return preferred != null ? FromWall(preferred) : "Север";
            }
            catch (Exception ex)
            {
                Logger.Warn($"OrientationCalculator.FromRoom: ошибка для {room?.Name}", ex);
                return "Север";
            }
        }

        // ─── Вспомогательные ───────────────────────────────────────

        private static double Normalize360(double deg)
        {
            while (deg < 0) deg += 360;
            while (deg >= 360) deg -= 360;
            return deg;
        }

        private static string BearingToCardinal(double bearingDeg)
        {
            if (bearingDeg >= 337.5 || bearingDeg < 22.5)  return "Север";
            if (bearingDeg < 67.5)                          return "Северо-Восток";
            if (bearingDeg < 112.5)                         return "Восток";
            if (bearingDeg < 157.5)                         return "Юго-Восток";
            if (bearingDeg < 202.5)                         return "Юг";
            if (bearingDeg < 247.5)                         return "Юго-Запад";
            if (bearingDeg < 292.5)                         return "Запад";
            return "Северо-Запад";
        }
    }
}
