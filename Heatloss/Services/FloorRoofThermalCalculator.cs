using System;
using System.Collections.Generic;
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
                Floor match = FindFloor(revitRoom, bbox);

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
                var match = FindFloor(revitRoom, bbox);

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
                foreach (var entry in RoofCache)
                {
                    var rb = entry.Box;
                    if (rb == null) continue;
                    if (!BoxesOverlapXY(rb, bbox)) continue;
                    // Кровля — выше комнаты
                    if (rb.Min.Z < bbox.Max.Z - 0.5) continue;

                    double u = ComputeU(entry.Element.RoofType?.GetCompoundStructure());
                    if (u > 0)
                    {
                        Logger.Debug($"[Roof U] {roomData.Name}: U={u:F3} (из {entry.Element.RoofType?.Name})");
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

        // ─────────────────────────────────────────────────────────
        //  КЭШИ КОНСТРУКЦИЙ
        //
        //  Перекрытия и кровли раньше запрашивались ЗАНОВО на каждое помещение:
        //  три метода (U пола, R слоёв пола, U кровли) — три полных обхода
        //  категории с вызовом get_BoundingBox у каждого элемента. Габарит
        //  в Revit не хранится готовым, он вычисляется по геометрии, и цена
        //  выходила «помещения × перекрытия»: на большом проекте (отзыв
        //  сетевиков 2026-08-26, расчёт около трёх часов) это десятки миллионов
        //  вычислений габарита на ровном месте.
        //
        //  Кэш живёт ровно один расчёт: CalculationEngine, а с ним и этот класс,
        //  создаются заново на каждое нажатие «Рассчитать теплопотери», поэтому
        //  правка модели между расчётами подхватывается.
        // ─────────────────────────────────────────────────────────

        /// <summary>Элемент с уже вычисленным габаритом.</summary>
        private sealed class Boxed<T> where T : Element
        {
            public T Element;
            public BoundingBoxXYZ Box;
        }

        private Dictionary<int, List<Boxed<Floor>>> _floorsByLevel;
        private List<Boxed<RoofBase>> _roofs;

        /// <summary>Перекрытия, разложенные по уровню; порядок внутри уровня — как у коллектора.</summary>
        private Dictionary<int, List<Boxed<Floor>>> FloorsByLevel
        {
            get
            {
                if (_floorsByLevel != null) return _floorsByLevel;

                _floorsByLevel = new Dictionary<int, List<Boxed<Floor>>>();
                foreach (var floor in new FilteredElementCollector(_document)
                             .OfCategory(BuiltInCategory.OST_Floors)
                             .WhereElementIsNotElementType()
                             .OfClass(typeof(Floor))
                             .Cast<Floor>())
                {
                    if (floor.LevelId == null) continue;
                    int levelKey = floor.LevelId.IntegerValue;

                    List<Boxed<Floor>> list;
                    if (!_floorsByLevel.TryGetValue(levelKey, out list))
                    {
                        list = new List<Boxed<Floor>>();
                        _floorsByLevel[levelKey] = list;
                    }

                    list.Add(new Boxed<Floor> { Element = floor, Box = floor.get_BoundingBox(null) });
                }

                Logger.Debug($"[Кэш] перекрытий: {_floorsByLevel.Values.Sum(v => v.Count)} " +
                             $"на {_floorsByLevel.Count} уровнях");
                return _floorsByLevel;
            }
        }

        /// <summary>Кровли в порядке коллектора — порядок решает, какая найдётся первой.</summary>
        private List<Boxed<RoofBase>> RoofCache
        {
            get
            {
                if (_roofs != null) return _roofs;

                _roofs = new FilteredElementCollector(_document)
                    .OfCategory(BuiltInCategory.OST_Roofs)
                    .WhereElementIsNotElementType()
                    .OfClass(typeof(RoofBase))
                    .Cast<RoofBase>()
                    .Select(r => new Boxed<RoofBase> { Element = r, Box = r.get_BoundingBox(null) })
                    .ToList();

                Logger.Debug($"[Кэш] кровель: {_roofs.Count}");
                return _roofs;
            }
        }

        /// <summary>
        /// Перекрытие помещения: первое по порядку коллектора, у которого совпадает
        /// уровень и габарит перекрывается с габаритом помещения в плане.
        /// Отбор по уровню сделан ключом словаря, а не проверкой на каждом элементе:
        /// результат тот же, обход — только по своему этажу.
        /// </summary>
        private Floor FindFloor(Room revitRoom, BoundingBoxXYZ roomBox)
        {
            if (revitRoom.LevelId == null) return null;

            List<Boxed<Floor>> candidates;
            if (!FloorsByLevel.TryGetValue(revitRoom.LevelId.IntegerValue, out candidates))
                return null;

            foreach (var entry in candidates)
            {
                if (BoxesOverlapXY(entry.Box, roomBox))
                    return entry.Element;
            }

            return null;
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

        // SameLevel(ElementId, ElementId) больше не нужен: совпадение уровня
        // обеспечивает ключ словаря FloorsByLevel (см. FindFloor).

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
