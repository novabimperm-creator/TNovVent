using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using QOVETER.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace QOVETER.Services
{
    /// <summary>
    /// Сканер элементов этажа — ДИАГНОСТИКА, и только она.
    ///
    /// Собирает окна, стены и двери уровня, читает их параметры и ищет аномалии
    /// (окна больше стен, стены без сопротивления, нулевые габариты). Запускается
    /// при выборе этажа в комбобоксе, результат кэшируется по уровню.
    ///
    /// ⚠ НИЧЕГО НЕ ПИШЕТ В <see cref="RoomData"/>. Раньше писал: метод
    /// <c>EnrichRoomData</c> перезаписывал у окон помещения площадь, габариты
    /// и U-значение, а у помещения — <c>WindowArea</c>, причём в тех самых объектах,
    /// которые уходят в движок. Следствие: смена этажа в комбобоксе — действие,
    /// которое инженер считает просмотром, — меняла входные данные расчёта, а из-за
    /// кэша результат зависел от того, какие этажи успели открыть до нажатия
    /// «Рассчитать». Вдобавок подставляемое U читалось без конверсии единиц
    /// и было занижено в 5,7 раза.
    ///
    /// Габариты, площадь и U окна собираются ОДИН раз, в
    /// <see cref="ElementCollectorService"/> при сборе помещений. Второй источник
    /// правды здесь не нужен — если скан видит расхождение, он обязан ЗАЯВИТЬ
    /// о нём аномалией, а не молча исправить.
    /// </summary>
    public class LevelElementScanner
    {
        private readonly Document _document;
        private readonly WallThermalCalculator _wallCalculator;
        private readonly Dictionary<int, LevelScanResult> _cache = new Dictionary<int, LevelScanResult>();

        public LevelElementScanner(Document document)
        {
            _document = document;
            _wallCalculator = new WallThermalCalculator(document);
        }

        /// <summary>
        /// Сканирует уровень: собирает все окна, стены и двери,
        /// читает их реальные параметры и проверяет аномалии.
        /// Результат кэшируется для повторного использования.
        /// </summary>
        public LevelScanResult ScanLevel(ElementId levelId, List<RoomData> rooms)
        {
            int key = levelId.IntegerValue;

            // Кэш: не сканируем повторно
            if (_cache.ContainsKey(key))
                return _cache[key];

            var result = new LevelScanResult { LevelId = key };

            try
            {
                Logger.Info($"[LevelScanner] Начало сканирования уровня {key}...");

                // 1. Сканируем ВСЕ окна на уровне
                ScanWindows(levelId, result);

                // 2. Сканируем ВСЕ наружные стены на уровне
                ScanWalls(levelId, result);

                // 3. Сканируем ВСЕ двери на уровне
                ScanDoors(levelId, result);

                // 4. Проверяем аномалии. Данные помещений НЕ трогаем — см. комментарий
                //    к классу: скан заявляет о расхождении, а не правит его молча.
                ValidateData(rooms, result);

                Logger.Info(
                    $"[LevelScanner] Завершено: " +
                    $"окон={result.Windows.Count} стен={result.Walls.Count} дверей={result.Doors.Count} " +
                    $"аномалий={result.Anomalies.Count}");

                _cache[key] = result;
            }
            catch (Exception ex)
            {
                Logger.Error("[LevelScanner] Ошибка сканирования уровня", ex);
            }

            return result;
        }

        /// <summary>
        /// Очистить кэш (при перезагрузке данных).
        /// </summary>
        public void ClearCache()
        {
            _cache.Clear();
        }

        // ═══════════════════════════════════════════════════════════════
        //  СКАНИРОВАНИЕ ОКОН
        // ═══════════════════════════════════════════════════════════════

        private void ScanWindows(ElementId levelId, LevelScanResult result)
        {
            var collector = new FilteredElementCollector(_document)
                .OfCategory(BuiltInCategory.OST_Windows)
                .WhereElementIsNotElementType();

            foreach (FamilyInstance win in collector)
            {
                try
                {
                    // Фильтр по уровню
                    if (!IsOnLevel(win, levelId)) continue;

                    var data = new ScannedElement
                    {
                        ElementId   = win.Id.IntegerValue,
                        Category    = "Окно",
                        TypeName    = win.Name,
                        HostId      = win.Host?.Id?.IntegerValue ?? -1
                    };

                    // Ширина
                    var wp = win.get_Parameter(BuiltInParameter.FAMILY_WIDTH_PARAM);
                    data.Width = wp != null && wp.AsDouble() > 0
                        ? UnitUtils.ConvertFromInternalUnits(wp.AsDouble(), UnitTypeId.Meters)
                        : 0;

                    // Высота
                    var hp = win.get_Parameter(BuiltInParameter.FAMILY_HEIGHT_PARAM);
                    data.Height = hp != null && hp.AsDouble() > 0
                        ? UnitUtils.ConvertFromInternalUnits(hp.AsDouble(), UnitTypeId.Meters)
                        : 0;

                    // Площадь = Width × Height (НЕ HOST_AREA_COMPUTED!)
                    data.Area = data.Width * data.Height;

                    // U-value из аналитических параметров
                    data.UValue = TryReadUValue(win);

                    // Расположение
                    if (win.Location is LocationPoint lp)
                        data.LocationPoint = lp.Point;

                    // Дополнительные параметры из семейства
                    ReadCustomParameters(win, data);

                    result.Windows.Add(data);

                    Logger.Debug(
                        $"  [Win] {data.TypeName} W={data.Width:F2}м H={data.Height:F2}м " +
                        $"S={data.Area:F2}м² U={data.UValue:F3} Host={data.HostId}");
                }
                catch (Exception ex)
                {
                    Logger.Error($"[Win] Ошибка обработки окна {win.Id.IntegerValue}", ex);
                }
            }
        }

        // ═══════════════════════════════════════════════════════════════
        //  СКАНИРОВАНИЕ СТЕН
        // ═══════════════════════════════════════════════════════════════

        private void ScanWalls(ElementId levelId, LevelScanResult result)
        {
            var collector = new FilteredElementCollector(_document)
                .OfCategory(BuiltInCategory.OST_Walls)
                .WhereElementIsNotElementType();

            foreach (Wall wall in collector)
            {
                try
                {
                    // Фильтр по уровню
                    var baseLevelParam = wall.get_Parameter(BuiltInParameter.WALL_BASE_CONSTRAINT);
                    if (baseLevelParam == null) continue;
                    if (baseLevelParam.AsElementId() != levelId) continue;

                    var data = new ScannedElement
                    {
                        ElementId = wall.Id.IntegerValue,
                        Category  = "Стена",
                        TypeName  = wall.Name
                    };

                    // Длина
                    if (wall.Location is LocationCurve lc)
                        data.Length = UnitUtils.ConvertFromInternalUnits(lc.Curve.Length, UnitTypeId.Meters);

                    // Высота
                    var wallHeight = wall.get_Parameter(BuiltInParameter.WALL_USER_HEIGHT_PARAM);
                    data.Height = wallHeight != null && wallHeight.AsDouble() > 0
                        ? UnitUtils.ConvertFromInternalUnits(wallHeight.AsDouble(), UnitTypeId.Meters)
                        : 3.0;

                    data.Area = data.Length * data.Height;

                    // R-value через единый WallThermalCalculator
                    var thermal = _wallCalculator.Calculate(wall);
                    data.RValue = thermal.RValue;
                    data.UValue = thermal.UValue;

                    // Толщина
                    var thParam = wall.get_Parameter(BuiltInParameter.WALL_ATTR_WIDTH_PARAM);
                    if (thParam != null)
                        data.Thickness = UnitUtils.ConvertFromInternalUnits(thParam.AsDouble(), UnitTypeId.Meters);

                    // Функция стены (наружная/несущая/внутренняя) — параметр ТИПА,
                    // на экземпляре его нет: раньше колонка в диагностике всегда пустовала.
                    var funcParam = wall.WallType?.get_Parameter(BuiltInParameter.FUNCTION_PARAM);
                    if (funcParam != null)
                        data.Function = funcParam.AsValueString();

                    result.Walls.Add(data);

                    Logger.Debug(
                        $"  [Wall] {data.TypeName} L={data.Length:F2}м H={data.Height:F2}м " +
                        $"S={data.Area:F2}м² R={data.RValue:F3} th={data.Thickness*1000:F0}мм func={data.Function}");
                }
                catch (Exception ex)
                {
                    Logger.Error($"[Wall] Ошибка обработки стены {wall.Id.IntegerValue}", ex);
                }
            }
        }

        // ═══════════════════════════════════════════════════════════════
        //  СКАНИРОВАНИЕ ДВЕРЕЙ
        // ═══════════════════════════════════════════════════════════════

        private void ScanDoors(ElementId levelId, LevelScanResult result)
        {
            var collector = new FilteredElementCollector(_document)
                .OfCategory(BuiltInCategory.OST_Doors)
                .WhereElementIsNotElementType();

            foreach (FamilyInstance door in collector)
            {
                try
                {
                    if (!IsOnLevel(door, levelId)) continue;

                    var data = new ScannedElement
                    {
                        ElementId = door.Id.IntegerValue,
                        Category  = "Дверь",
                        TypeName  = door.Name,
                        HostId    = door.Host?.Id?.IntegerValue ?? -1
                    };

                    var wp = door.get_Parameter(BuiltInParameter.FAMILY_WIDTH_PARAM);
                    data.Width = wp != null && wp.AsDouble() > 0
                        ? UnitUtils.ConvertFromInternalUnits(wp.AsDouble(), UnitTypeId.Meters)
                        : 0.9;

                    var hp = door.get_Parameter(BuiltInParameter.FAMILY_HEIGHT_PARAM);
                    data.Height = hp != null && hp.AsDouble() > 0
                        ? UnitUtils.ConvertFromInternalUnits(hp.AsDouble(), UnitTypeId.Meters)
                        : 2.1;

                    data.Area = data.Width * data.Height;

                    if (door.Location is LocationPoint lp)
                        data.LocationPoint = lp.Point;

                    result.Doors.Add(data);

                    Logger.Debug(
                        $"  [Door] {data.TypeName} W={data.Width:F2}м H={data.Height:F2}м S={data.Area:F2}м²");
                }
                catch (Exception ex)
                {
                    Logger.Error($"[Door] Ошибка обработки двери {door.Id.IntegerValue}", ex);
                }
            }
        }

        // ═══════════════════════════════════════════════════════════════
        //  ВАЛИДАЦИЯ И АНОМАЛИИ
        // ═══════════════════════════════════════════════════════════════

        private void ValidateData(List<RoomData> rooms, LevelScanResult scan)
        {
            foreach (var room in rooms)
            {
                // Окно больше нетто-стены? room.WallArea — нетто (за вычетом проёмов).
                if (room.WindowArea > room.WallArea + 1)
                {
                    scan.Anomalies.Add(
                        $"⚠ {room.Name}: площадь окон ({room.WindowArea:F1}м²) " +
                        $"превышает площадь нетто-стен ({room.WallArea:F1}м²) — проверьте границы помещения");
                }

                // Нет окон на наружной стене длиннее 2м?
                if (room.NumberOfExternalWalls > 0 && room.WindowArea < 0.01 && room.WallArea > 3)
                {
                    scan.Anomalies.Add(
                        $"ℹ {room.Name}: наружные стены {room.WallArea:F1}м², но окон не найдено");
                }

                // Нулевое/малое 1/U_avg стен
                if (room.AverageInverseUValue < 0.1 && room.WallArea > 0)
                {
                    scan.Anomalies.Add(
                        $"⚠ {room.Name}: сопротивление стен очень мало (1/U_avg={room.AverageInverseUValue:F3} м²·К/Вт), " +
                        $"проверьте состав стены в Revit");
                }

                // Площадь окна подозрительно большая (>50% площади стены)
                double grossWall = room.WallArea + room.WindowArea;
                if (grossWall > 0 && room.WindowArea / grossWall > 0.7)
                {
                    scan.Anomalies.Add(
                        $"⚠ {room.Name}: окна занимают {room.WindowArea / grossWall * 100:F0}% " +
                        $"площади наружных стен — проверьте корректность");
                }

                // Расхождение между сканом уровня и тем, что собрано у помещения.
                // Раньше на этом месте стояло молчаливое ИСПРАВЛЕНИЕ (EnrichRoomData):
                // площадь и U окна перезаписывались данными скана прямо в объектах
                // расчёта. Теперь расхождение объявляется — решает инженер.
                foreach (var win in room.Windows)
                {
                    if (win.Id == null) continue;
                    var scanned = scan.Windows.FirstOrDefault(s => s.ElementId == win.Id.IntegerValue);
                    if (scanned == null) continue;

                    if (scanned.Area > 0 && Math.Abs(win.Area - scanned.Area) > 0.05)
                    {
                        scan.Anomalies.Add(
                            $"⚠ {room.Name}: окно {win.TypeName} — площадь в расчёте " +
                            $"{win.Area:F2} м², по параметрам семейства {scanned.Area:F2} м². " +
                            "Проверьте, откуда берутся габариты окна");
                    }
                }
            }

            // Окна с непрочитанными габаритами — это про МОДЕЛЬ, а не про помещение,
            // поэтому проверка идёт по скану уровня. У собранных окон нулевых размеров
            // не бывает: ElementCollectorService подставляет умолчание 1,2 × 1,5 м,
            // и прежняя проверка по room.Windows не срабатывала никогда.
            var noSize = scan.Windows.Where(w => w.Width <= 0 || w.Height <= 0).ToList();
            if (noSize.Count > 0)
            {
                scan.Anomalies.Add(
                    $"⚠ Габариты не прочитаны у {noSize.Count} окон уровня " +
                    $"({string.Join(", ", noSize.Select(w => w.TypeName).Distinct().Take(5))}) — " +
                    "в расчёте им подставлено типовое окно 1,2 × 1,5 м");
            }

            // Логируем аномалии
            foreach (var a in scan.Anomalies)
                Logger.Warn($"[АНОМАЛИЯ] {a}");
        }

        // ═══════════════════════════════════════════════════════════════
        //  ВСПОМОГАТЕЛЬНЫЕ МЕТОДЫ
        // ═══════════════════════════════════════════════════════════════

        private bool IsOnLevel(FamilyInstance fi, ElementId levelId)
        {
            // Способ 1: LevelId элемента
            if (fi.LevelId == levelId) return true;

            // Способ 2: уровень хост-элемента
            if (fi.Host is Wall wall)
            {
                var baseLevelParam = wall.get_Parameter(BuiltInParameter.WALL_BASE_CONSTRAINT);
                if (baseLevelParam != null && baseLevelParam.AsElementId() == levelId)
                    return true;
            }

            // Способ 3: Schedule Level
            var scheduleLevel = fi.get_Parameter(BuiltInParameter.FAMILY_LEVEL_PARAM);
            if (scheduleLevel != null && scheduleLevel.AsElementId() == levelId)
                return true;

            return false;
        }

        /// <summary>
        /// U элемента из параметров Revit, Вт/(м²·К) — для диагностики.
        ///
        /// Было: <c>1.0 / rParam.AsDouble()</c> по внутренним единицам Revit, то есть
        /// U занижался в 5,678 раза; а список пользовательских имён включал
        /// «Теплопроводность» — λ [Вт/(м·К)], совсем другую величину, — и подставлял
        /// её как U без всякой проверки. Пока результат уходил в <c>EnrichRoomData</c>,
        /// он ещё и перезаписывал U окон в расчёте.
        /// Теперь единицы и правдоподобность — на <see cref="RevitThermalParameter"/>,
        /// а λ из списка убрана: величину нельзя подменять другой величиной.
        /// </summary>
        private double TryReadUValue(FamilyInstance element)
        {
            string owner = $"Скан: {element.Name}";

            // 1. Аналитический R → U = 1/R
            var rParam = element.get_Parameter(BuiltInParameter.ANALYTICAL_THERMAL_RESISTANCE);
            double rSI;
            if (RevitThermalParameter.TryReadResistance(rParam, owner, out rSI))
                return 1.0 / rSI;

            // 2. Пользовательские параметры — именно U, а не λ и не R
            string[] uNames = { "U-value", "Uvalue", "К_теплопередачи",
                                "Коэффициент теплопередачи" };
            foreach (var name in uNames)
            {
                double uSI;
                if (RevitThermalParameter.TryReadUValue(element.LookupParameter(name), owner, out uSI))
                    return uSI;
            }

            return 0; // не найдено — fallback в CalculationEngine
        }

        private void ReadCustomParameters(FamilyInstance fi, ScannedElement data)
        {
            // Читаем доп. параметры, которые могут быть полезны
            string[] paramNames = { "N_Эт.Номер", "A_Размер_Длина", "Ширина", "Высота", "Марка" };
            foreach (var name in paramNames)
            {
                var p = fi.LookupParameter(name);
                if (p != null && p.HasValue)
                {
                    data.CustomParams[name] = p.AsValueString() ?? p.AsString() ?? p.AsDouble().ToString("F2");
                }
            }
        }
    }

    // ═══════════════════════════════════════════════════════════════
    //  МОДЕЛИ ДАННЫХ СКАНА
    // ═══════════════════════════════════════════════════════════════

    public class LevelScanResult
    {
        public int LevelId { get; set; }
        public List<ScannedElement> Windows   { get; set; } = new List<ScannedElement>();
        public List<ScannedElement> Walls     { get; set; } = new List<ScannedElement>();
        public List<ScannedElement> Doors     { get; set; } = new List<ScannedElement>();
        public List<string>        Anomalies { get; set; } = new List<string>();
        public DateTime ScannedAt { get; set; } = DateTime.Now;

        public string GetSummary()
        {
            return $"Окон: {Windows.Count}, Стен: {Walls.Count}, Дверей: {Doors.Count}" +
                   (Anomalies.Count > 0 ? $", ⚠ Аномалий: {Anomalies.Count}" : "");
        }
    }

    public class ScannedElement
    {
        public int    ElementId  { get; set; }
        public string Category   { get; set; } // Окно / Стена / Дверь
        public string TypeName   { get; set; }
        public double Width      { get; set; }
        public double Height     { get; set; }
        public double Length     { get; set; }
        public double Area       { get; set; }
        public double Thickness  { get; set; }
        public double RValue     { get; set; }
        public double UValue     { get; set; }
        public int    HostId     { get; set; } = -1;
        public string Function   { get; set; } // Наружная/Внутренняя
        public Autodesk.Revit.DB.XYZ LocationPoint { get; set; }
        public Dictionary<string, string> CustomParams { get; set; } = new Dictionary<string, string>();
    }
}
