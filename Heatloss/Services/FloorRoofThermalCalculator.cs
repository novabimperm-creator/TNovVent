using System;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using QOVETER.Models;

namespace QOVETER.Services
{
    /// <summary>
    /// Расчёт U-значений пола и кровли помещения по реальной конструкции из модели.
    /// Поиск Floor/Roof — по уровню комнаты и пересечению bbox в плане.
    /// При отсутствии данных — fallback на нормативные значения из <see cref="ThermalConstants"/>.
    /// </summary>
    public class FloorRoofThermalCalculator
    {
        private readonly Document _document;
        private readonly WallThermalCalculator _wallCalculator;

        public FloorRoofThermalCalculator(Document document)
        {
            _document = document ?? throw new ArgumentNullException(nameof(document));
            _wallCalculator = new WallThermalCalculator(document);
        }

        /// <summary>
        /// U пола помещения, Вт/(м²·К). Читает CompoundStructure напольной плиты,
        /// если её удалось сопоставить с помещением; иначе — нормативное значение.
        /// </summary>
        public double GetFloorUValue(RoomData roomData)
        {
            var revitRoom = ResolveRoom(roomData);
            double fallback = GetFloorFallback(roomData);
            if (revitRoom == null) return fallback;

            var bbox = revitRoom.get_BoundingBox(null);
            if (bbox == null) return fallback;

            try
            {
                var floors = new FilteredElementCollector(_document)
                    .OfCategory(BuiltInCategory.OST_Floors)
                    .WhereElementIsNotElementType()
                    .OfClass(typeof(Floor))
                    .Cast<Floor>();

                Floor match = floors.FirstOrDefault(f =>
                    SameLevel(f.LevelId, revitRoom.LevelId)
                    && BoxesOverlapXY(f.get_BoundingBox(null), bbox));

                if (match != null && match.FloorType != null)
                {
                    double u = ComputeU(match.FloorType.GetCompoundStructure());
                    if (u > 0)
                    {
                        Logger.Debug($"[Floor U] {roomData.Name}: U={u:F3} (из FloorType {match.FloorType.Name})");
                        return u;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"GetFloorUValue: ошибка для {roomData.Name}", ex);
            }

            return fallback;
        }

        /// <summary>
        /// Термическое сопротивление СЛОЁВ пола, м²·°С/Вт — без Rsi+Rse.
        ///
        /// <para>Нужно зональному методу: в формуле (Г.16) СП 50.13330.2024
        /// конструкция пола входит слагаемым δ_ут/λ_ут, а сопротивления
        /// теплоотдаче в зональной методике не участвуют вовсе — сопротивление
        /// зоны уже описывает путь теплоты через грунт целиком.</para>
        ///
        /// <para>Ноль — конструкцию пола в модели найти не удалось. Тогда считается
        /// голая зона, то есть потери ВЫШЕ: оценка в запас, и она пишется в журнал.</para>
        /// </summary>
        public double GetFloorLayersR(RoomData roomData)
        {
            var revitRoom = ResolveRoom(roomData);
            if (revitRoom == null) return 0;

            var bbox = revitRoom.get_BoundingBox(null);
            if (bbox == null) return 0;

            try
            {
                var match = new FilteredElementCollector(_document)
                    .OfCategory(BuiltInCategory.OST_Floors)
                    .WhereElementIsNotElementType()
                    .OfClass(typeof(Floor))
                    .Cast<Floor>()
                    .FirstOrDefault(f => SameLevel(f.LevelId, revitRoom.LevelId)
                                      && BoxesOverlapXY(f.get_BoundingBox(null), bbox));

                var cs = match?.FloorType?.GetCompoundStructure();
                if (cs == null) return 0;

                int layers;
                string description;
                double r = _wallCalculator.CalculateLayeredR(cs, out layers, out description);

                if (r > 0)
                    Logger.Debug($"[Грунт] {roomData.Name}: R слоёв пола = {r:F2} ({description})");

                return r > 0 ? r : 0;
            }
            catch (Exception ex)
            {
                Logger.Debug($"GetFloorLayersR: {roomData.Name}: {ex.Message}");
                return 0;
            }
        }

        /// <summary>
        /// U кровли/потолка помещения, Вт/(м²·К). Аналогично GetFloorUValue.
        /// </summary>
        public double GetRoofUValue(RoomData roomData)
        {
            var revitRoom = ResolveRoom(roomData);
            double fallback = GetRoofFallback(roomData);
            if (revitRoom == null) return fallback;

            var bbox = revitRoom.get_BoundingBox(null);
            if (bbox == null) return fallback;

            try
            {
                var roofs = new FilteredElementCollector(_document)
                    .OfCategory(BuiltInCategory.OST_Roofs)
                    .WhereElementIsNotElementType()
                    .OfClass(typeof(RoofBase))
                    .Cast<RoofBase>();

                foreach (var roof in roofs)
                {
                    var rb = roof.get_BoundingBox(null);
                    if (rb == null) continue;
                    if (!BoxesOverlapXY(rb, bbox)) continue;
                    // Кровля — выше комнаты
                    if (rb.Min.Z < bbox.Max.Z - 0.5) continue;

                    double u = ComputeU(roof.RoofType?.GetCompoundStructure());
                    if (u > 0)
                    {
                        Logger.Debug($"[Roof U] {roomData.Name}: U={u:F3} (из {roof.RoofType?.Name})");
                        return u;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"GetRoofUValue: ошибка для {roomData.Name}", ex);
            }

            return fallback;
        }

        private double ComputeU(CompoundStructure cs)
        {
            if (cs == null) return 0;
            double rLayers = _wallCalculator.CalculateLayeredR(cs, out _, out _);
            if (rLayers <= 0.05) return 0;
            return 1.0 / (rLayers + ThermalConstants.RsiPlusRse);
        }

        private Room ResolveRoom(RoomData roomData)
        {
            try
            {
                if (roomData.RoomElementId != null && roomData.RoomElementId != ElementId.InvalidElementId)
                    return _document.GetElement(roomData.RoomElementId) as Room;
                return _document.GetElement(new ElementId(roomData.Id)) as Room;
            }
            catch (Exception ex)
            {
                Logger.Debug($"ResolveRoom: {ex.Message}");
                return null;
            }
        }

        private static bool SameLevel(ElementId a, ElementId b)
        {
            if (a == null || b == null) return false;
            return a == b || a.IntegerValue == b.IntegerValue;
        }

        private static bool BoxesOverlapXY(BoundingBoxXYZ a, BoundingBoxXYZ b)
        {
            if (a == null || b == null) return false;
            return a.Min.X <= b.Max.X && a.Max.X >= b.Min.X
                && a.Min.Y <= b.Max.Y && a.Max.Y >= b.Min.Y;
        }

        // ─── Fallback по нормативу — единая таблица в ThermalConstants ───

        private static double GetFloorFallback(RoomData room) =>
            ThermalConstants.FloorUFallback(room.Category);

        private static double GetRoofFallback(RoomData room) =>
            ThermalConstants.RoofUFallback(room.Name, room.Type);
    }
}
