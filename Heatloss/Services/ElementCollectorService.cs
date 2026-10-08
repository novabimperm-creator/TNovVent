using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using QOVETER.Models;
using System;
using System.Collections.Generic;
using System.Linq;

using static TNovCommon.ElementIdCompat;

namespace QOVETER.Services
{
    public class ElementCollectorService
    {
        private readonly Document _document;
        private readonly OrientationCalculator _orientationCalculator;

        /// <summary>Типовое окно, подставляемое когда габариты в модели не читаются.</summary>
        private const double DefaultWindowWidthM  = 1.2;
        private const double DefaultWindowHeightM = 1.5;

        private static readonly string[] WidthParameterNames =
            { "Ширина", "Width", "A_Размер_Ширина", "Ширина проёма" };

        private static readonly string[] HeightParameterNames =
            { "Высота", "Height", "A_Размер_Высота", "Высота проёма" };

        /// <summary>
        /// Сколько окон получили типовые габариты вместо прочитанных из модели.
        /// Читается после сбора: подстановка «в среднем правдоподобного» окна
        /// не должна проходить незамеченной — площадь остекления входит и в потери
        /// через окна, и в вычет из площади стен.
        /// </summary>
        public int WindowsWithDefaultSize { get; private set; }

        /// <summary>
        /// Сколько «окон» оказались проёмами под витраж и пропущены: остекление
        /// там считает сегмент витражной стены, а проём поверх него — двойной счёт.
        /// </summary>
        public int WindowsCoveredByCurtain { get; private set; }

        public ElementCollectorService(Document document)
        {
            _document = document;
            _orientationCalculator = new OrientationCalculator(document);
        }

        /// <summary>
        /// Габарит элемента в метрах: параметр экземпляра → тот же параметр типа →
        /// запасной BuiltInParameter → русские и английские имена на экземпляре и типе.
        /// 0 означает «в модели не нашлось».
        /// </summary>
        private static double ReadDimensionM(FamilyInstance fi, BuiltInParameter primary,
                                             BuiltInParameter fallback, string[] names)
        {
            var candidates = new List<Parameter>
            {
                fi.get_Parameter(primary),
                fi.Symbol?.get_Parameter(primary),
                fi.get_Parameter(fallback),
                fi.Symbol?.get_Parameter(fallback)
            };

            foreach (var name in names)
            {
                candidates.Add(fi.LookupParameter(name));
                candidates.Add(fi.Symbol?.LookupParameter(name));
            }

            foreach (var p in candidates)
            {
                if (p == null || !p.HasValue || p.StorageType != StorageType.Double) continue;
                double raw = p.AsDouble();
                if (raw <= 0) continue;
                return UnitUtils.ConvertFromInternalUnits(raw, UnitTypeId.Meters);
            }

            return 0;
        }

        /// <summary>
        /// Фаза помещения. Нужна везде, где вызывается <c>GetRoomAtPoint</c>:
        /// без явной фазы Revit ищет помещение в ПОСЛЕДНЕЙ фазе документа, и если
        /// помещения размещены в другой (реконструкция, «Существующие»), метод
        /// вернёт null для любой точки. Молча: окна и двери просто не найдутся
        /// ни у одного помещения, площадь остекления окажется нулевой везде.
        ///
        /// Единственная реализация на проект: <c>GeometryCollector</c> фазу
        /// передавал, а <c>IsElementInRoom</c> — нет, и это была не разница
        /// в намерениях, а недосмотр.
        /// </summary>
        public static Phase GetRoomPhase(Room room)
        {
            if (room == null) return null;
            try
            {
                var phaseParam = room.get_Parameter(BuiltInParameter.ROOM_PHASE);
                if (phaseParam == null || phaseParam.AsElementId() == ElementId.InvalidElementId)
                    return null;
                return room.Document?.GetElement(phaseParam.AsElementId()) as Phase;
            }
            catch (Exception ex)
            {
                Logger.Debug($"GetRoomPhase: фаза помещения не прочитана: {ex.Message}");
                return null;
            }
        }

        // Сбор стен помещения ЗДЕСЬ НЕ ЖИВЁТ. Он в
        // GeometryCollector.CalculateRoomAreasFromBoundary: там граница разрешается
        // до несущей стены за отделкой, вычитаются проёмы и берётся U из
        // WallThermalCalculator. Прежние CollectWallsForRoom / CreateWallInfo /
        // CalculateWallRValue считали R по грубым эмпирическим числам
        // (0.8 / 0.5 / 1.5 / 2.5 по ключевому слову в имени), никем не вызывались
        // и удалены 2026-08-06: второй источник правды по U стены — прямой путь
        // к молча разошедшимся цифрам.

        public List<WindowInfo> CollectWindowsForRoom(Room room)
        {
            var windows = new List<WindowInfo>();

            try
            {
                var bbox = room.get_BoundingBox(null);
                if (bbox == null)
                {
                    Logger.Debug($"[Windows] {room.Name}: bbox=null, окна не ищем");
                    return windows;
                }

                var outline = new Outline(bbox.Min, bbox.Max);
                var filter = new BoundingBoxIntersectsFilter(outline);

                var windowCollector = new FilteredElementCollector(_document)
                    .OfCategory(BuiltInCategory.OST_Windows)
                    .WherePasses(filter)
                    .WhereElementIsNotElementType()
                    .Cast<FamilyInstance>()
                    .ToList();

                int candidates = windowCollector.Count;
                int matched = 0;
                var boundaryCurtainIds = BoundaryCurtainWallIds(room);
                foreach (var window in windowCollector)
                {
                    // Вложенные общие семейства окна лежат в той же категории:
                    // в московской модели на ~490 настоящих окон приходится
                    // 4 278 вложенных — откосы, створки, наличники, подоконники,
                    // отливы. Попав в room.Windows, такой элемент считается
                    // остеклением с U стеклопакета (на скрине проектировщика
                    // 2026-10-07 виден «Наличник Подрезка 45 градусов, 0,07 м²»),
                    // а створка с реальными габаритами удвоила бы площадь окна.
                    if (window.SuperComponent != null) continue;

                    if (IsElementInRoom(window, room))
                    {
                        // Проём под витраж — не окно, ЕСЛИ этот витраж сам образует
                        // сегмент границы помещения: тогда остекление уже посчитано
                        // сегментом целиком (IsCurtainGlazing), и проём поверх него —
                        // двойной счёт. У помещения 1-11 так выходило 45 м² «окон»
                        // при физических 23 («Окна вычтено» ≠ «Окна всего»).
                        //
                        // ⚠ Требование «витраж есть в границе ЭТОГО помещения»
                        // обязательно: у лоджий той же модели холодный витраж стоит
                        // В ПРОЁМЕ кирпичной стены и границу не образует — там
                        // окно-проём единственный носитель остекления, и пропустить
                        // его значило бы объявить лоджию закрытой кирпичом.
                        if (IsCoveredByCurtainWall(window, boundaryCurtainIds))
                        {
                            WindowsCoveredByCurtain++;
                            Logger.Debug(
                                $"[Окно] {window.Name} (id {window.Id.IntegerValue}): " +
                                "перед проёмом витраж из границы помещения — остекление " +
                                "считает его сегмент, окно пропущено");
                            continue;
                        }

                        var windowInfo = CreateWindowInfo(window);
                        windows.Add(windowInfo);
                        matched++;
                    }
                }
                Logger.Debug($"[Windows] {room.Name}: bbox-кандидатов={candidates}, в комнате={matched}");
            }
            catch (Exception ex)
            {
                Logger.Error($"Ошибка сбора окон для помещения {room.Name}", ex);
            }

            return windows;
        }

        public List<DoorInfo> CollectDoorsForRoom(Room room)
        {
            var doors = new List<DoorInfo>();
            
            try
            {
                var bbox = room.get_BoundingBox(null);
                if (bbox == null) return doors;
                
                var outline = new Outline(bbox.Min, bbox.Max);
                var filter = new BoundingBoxIntersectsFilter(outline);
                
                var doorCollector = new FilteredElementCollector(_document)
                    .OfCategory(BuiltInCategory.OST_Doors)
                    .WherePasses(filter)
                    .WhereElementIsNotElementType();
                
                foreach (FamilyInstance door in doorCollector)
                {
                    // Вложенные общие семейства тоже лежат в категории «Двери»:
                    // в московской модели это 736 «Ручка Нажимная» и 357 «Полотно
                    // Глухое» на 473 настоящих двери. Дверь — только верхний
                    // уровень; ручка, попав сюда, получила бы габариты-заглушки
                    // 0,9 × 2,1 и удвоила бы дверные потери.
                    if (door.SuperComponent != null) continue;

                    if (IsElementInRoom(door, room))
                    {
                        var doorInfo = CreateDoorInfo(door);
                        doors.Add(doorInfo);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Ошибка сбора дверей для помещения {room.Name}", ex);
            }
            
            return doors;
        }

        /// <summary>
        /// Id витражных стен, образующих сегменты границы помещения. Только они
        /// имеют право «накрыть» окно-проём: их площадь уже входит в остекление
        /// помещения сегментом (IsCurtainGlazing в GeometryCollector).
        /// </summary>
        private List<ElementId> BoundaryCurtainWallIds(Room room)
        {
            var ids = new List<ElementId>();
            try
            {
                var loops = room.GetBoundarySegments(new SpatialElementBoundaryOptions());
                if (loops == null) return ids;
                foreach (var loop in loops)
                    foreach (var seg in loop)
                    {
                        var wall = _document.GetElement(seg.ElementId) as Wall;
                        if (wall?.WallType?.Kind == WallKind.Curtain && !ids.Contains(wall.Id))
                            ids.Add(wall.Id);
                    }
            }
            catch (Exception ex)
            {
                Logger.Debug($"BoundaryCurtainWallIds: {ex.Message}");
            }
            return ids;
        }

        /// <summary>
        /// Перед окном (в пределах толщины стены с зазором) стоит витражная стена
        /// ИЗ ГРАНИЦЫ этого помещения, параллельная хост-стене окна. Такое «окно» —
        /// проём под витраж: остекление уже посчитано сегментом витража.
        ///
        /// <para>Проверка параллельности обязательна: у углового окна в выборку
        /// по габариту попадает и перпендикулярный витраж соседнего фасада —
        /// без неё настоящее окно рядом с витражом молча бы выпало.</para>
        ///
        /// <para>Допуск 0,8 м — толщина несущей стены плюс зазор до витража.</para>
        /// </summary>
        private bool IsCoveredByCurtainWall(FamilyInstance window, List<ElementId> boundaryCurtainIds)
        {
            if (boundaryCurtainIds == null || boundaryCurtainIds.Count == 0) return false;
            try
            {
                var lp = (window.Location as LocationPoint)?.Point;
                if (lp == null) return false;

                var hostCurve = ((window.Host as Wall)?.Location as LocationCurve)?.Curve;
                XYZ hostDir = null;
                if (hostCurve != null)
                    hostDir = (hostCurve.GetEndPoint(1) - hostCurve.GetEndPoint(0)).Normalize();

                double maxDistFt = UnitUtils.ConvertToInternalUnits(0.8, UnitTypeId.Meters);
                foreach (var id in boundaryCurtainIds)
                {
                    var curtain = _document.GetElement(id) as Wall;
                    var curve = (curtain?.Location as LocationCurve)?.Curve;
                    if (curve == null) continue;

                    if (hostDir != null)
                    {
                        XYZ dir = (curve.GetEndPoint(1) - curve.GetEndPoint(0)).Normalize();
                        if (Math.Abs(dir.DotProduct(hostDir)) < 0.7) continue; // не параллелен
                    }

                    // Расстояние — В ПЛАНЕ: точка вставки окна стоит выше кривой
                    // стены (подоконник), и 3D-расстояние добавляло бы к зазору
                    // высоту подоконника — окно от пола проходило бы порог,
                    // окно с подоконником 0,9 м уже нет.
                    XYZ flat = new XYZ(lp.X, lp.Y, curve.GetEndPoint(0).Z);
                    var proj = curve.Project(flat);
                    if (proj != null && proj.Distance <= maxDistFt) return true;
                }
            }
            catch (Exception ex)
            {
                Logger.Debug($"IsCoveredByCurtainWall: {ex.Message}");
            }
            return false;
        }

        private WindowInfo CreateWindowInfo(FamilyInstance window)
        {
            var windowInfo = new WindowInfo
            {
                Id       = window.Id,
                TypeName = window.Name,
                Location = window.Location
            };

            // ── Габариты ────────────────────────────────────────────────────────
            // Ищем по всей цепочке: экземпляр → тип → русские имена параметров.
            // Раньше читался ТОЛЬКО FAMILY_WIDTH_PARAM/FAMILY_HEIGHT_PARAM на
            // экземпляре, а у многих российских семейств размеры лежат на ТИПЕ либо
            // в параметрах «Ширина»/«Высота». Не нашлось — молча подставлялось
            // окно 1,2 × 1,5 м, и в журнал не уходило ни строки: площадь остекления
            // получалась выдуманной, а выглядела посчитанной.
            double width  = ReadDimensionM(window, BuiltInParameter.FAMILY_WIDTH_PARAM,
                                           BuiltInParameter.WINDOW_WIDTH,  WidthParameterNames);
            double height = ReadDimensionM(window, BuiltInParameter.FAMILY_HEIGHT_PARAM,
                                           BuiltInParameter.WINDOW_HEIGHT, HeightParameterNames);

            if (width <= 0 || height <= 0)
            {
                WindowsWithDefaultSize++;
                Logger.Debug(
                    $"[Окно] {window.Name} (id {window.Id.IntValue()}): габариты не прочитаны " +
                    $"(Ш={width:F2} В={height:F2}) — принято типовое окно " +
                    $"{DefaultWindowWidthM} × {DefaultWindowHeightM} м");
                if (width  <= 0) width  = DefaultWindowWidthM;
                if (height <= 0) height = DefaultWindowHeightM;
            }

            windowInfo.Width  = width;
            windowInfo.Height = height;

            // Площадь окна = Width × Height из параметров семейства.
            // ⚠ НЕ используем HOST_AREA_COMPUTED и LookupParameter("Площадь") — оба
            //   возвращают площадь СТЕНЫ-хоста, а не самого окна!
            windowInfo.Area = windowInfo.Width * windowInfo.Height;

            windowInfo.GlassType   = DetermineGlassType(window);
            windowInfo.Chambers    = DetermineChamberCount(window);
            // 1️⃣ Пробуем считать U из параметров Revit (точнее чем по числу камер)
            double uRevit = TryGetWindowUValueFromRevit(window);
            windowInfo.UValue = uRevit > 0 ? uRevit : GetWindowUValue(windowInfo.GlassType, windowInfo.Chambers);
            windowInfo.HostWallId  = window.Host?.Id;
            // Ориентация окна берётся от его хост-стены (нормаль через TrueNorth)
            windowInfo.Orientation = window.Host is Wall hostWall
                ? _orientationCalculator.FromWall(hostWall)
                : "Север";
            // XYZ для пространственной фильтрации
            if (window.Location is LocationPoint winLp)
                windowInfo.LocationXYZ = winLp.Point;

            return windowInfo;
        }

        /// <summary>
        /// Пробуем прочитать U-значение окна из параметров семейства Revit.
        /// Если найдено R-значение → возвращаем 1/R. Если нет → 0 (fallback на таблицу).
        ///
        /// Единицы — через <see cref="RevitThermalParameter"/>, единственное место
        /// в проекте, где решается вопрос «конвертировать или нет»: типизированный
        /// параметр приходит во внутренних единицах Revit, безразмерный — уже в СИ.
        /// Раньше конверсия стояла здесь безусловно, а в двух других чтениях
        /// её не было вовсе.
        /// </summary>
        private double TryGetWindowUValueFromRevit(FamilyInstance window)
        {
            string owner = $"Окно {window.Name}";

            // Российские параметры сопротивления теплопередаче
            string[] rParamNames = {
                "СП.Сопротивление", "R_Значение", "RT", "Сопротивление теплопередаче",
                "R-factor", "Rw", "Ro", "Ro_тр"
            };
            foreach (var name in rParamNames)
            {
                var p = window.LookupParameter(name) ?? window.Symbol?.LookupParameter(name);
                double rSI;
                if (RevitThermalParameter.TryReadResistance(p, owner, out rSI))
                    return 1.0 / rSI;
            }

            // Аналитический коэффициент Revit MEP.
            // В Revit 2027 из API убрали ANALYTICAL_HEAT_TRANSFER_COEFFICIENT (U).
            // Остался ANALYTICAL_THERMAL_RESISTANCE (R) — обратная величина, U = 1/R,
            // читаем её тем же путём, что и российские параметры сопротивления выше.
#if R2022
            var uParam = window.get_Parameter(BuiltInParameter.ANALYTICAL_HEAT_TRANSFER_COEFFICIENT);
            double uSI;
            if (RevitThermalParameter.TryReadUValue(uParam, owner, out uSI))
                return uSI;
#else
            var rParam = window.get_Parameter(BuiltInParameter.ANALYTICAL_THERMAL_RESISTANCE);
            double rAnalyticalSI;
            if (RevitThermalParameter.TryReadResistance(rParam, owner, out rAnalyticalSI))
                return 1.0 / rAnalyticalSI;
#endif

            return 0; // Не найдено
        }

        private DoorInfo CreateDoorInfo(FamilyInstance door)
        {
            var doorInfo = new DoorInfo
            {
                Id       = door.Id,
                TypeName = door.Name,
                Location = door.Location
            };

            // Габариты — той же цепочкой, что у окон: экземпляр → тип → русские
            // имена. DOOR_WIDTH/DOOR_HEIGHT — параметры ТИПА (у pmN-семейств
            // московской модели на экземпляре их нет вовсе), а прежний код читал
            // экземпляр и на null молча брал AsDouble() = 0: все двери дома
            // выходили «0,00 м²», не теряли ни ватта и не вычитались из стен.
            // Та же ловушка, что FUNCTION_PARAM и WALL_ATTR_WIDTH_PARAM.
            doorInfo.Width  = ReadDimensionM(door, BuiltInParameter.DOOR_WIDTH,
                                             BuiltInParameter.FAMILY_WIDTH_PARAM,  WidthParameterNames);
            doorInfo.Height = ReadDimensionM(door, BuiltInParameter.DOOR_HEIGHT,
                                             BuiltInParameter.FAMILY_HEIGHT_PARAM, HeightParameterNames);

            if (doorInfo.Width <= 0 || doorInfo.Height <= 0)
            {
                Logger.Debug(
                    $"[Дверь] {door.Name} (id {door.Id.IntegerValue}): габариты не прочитаны " +
                    $"(Ш={doorInfo.Width:F2} В={doorInfo.Height:F2}) — принята дверь 0,9 × 2,1 м");
                if (doorInfo.Width  <= 0) doorInfo.Width  = 0.9;
                if (doorInfo.Height <= 0) doorInfo.Height = 2.1;
            }

            // Площадь в м²
            doorInfo.Area = doorInfo.Width * doorInfo.Height;

            doorInfo.Material     = DetermineDoorMaterial(door);
            doorInfo.IsExternal   = IsExternalDoor(door);
            doorInfo.UValue       = GetDoorUValue(doorInfo.Material);
            doorInfo.HostWallId   = door.Host?.Id;
            doorInfo.DoorType     = DetermineDoorType(door);
            // XYZ для пространственной фильтрации
            if (door.Location is LocationPoint doorLp)
                doorInfo.LocationXYZ = doorLp.Point;

            return doorInfo;
        }

        private bool IsElementInRoom(Element element, Room room)
        {
            var phase = GetRoomPhase(room);
            try
            {
                // Окна/двери расположены В теле стены, поэтому их LocationPoint
                // обычно NOT inside any room. Используем Host-стену и пробуем с обеих сторон.
                if (element is FamilyInstance fi && fi.Host is Wall hostWall)
                {
                    // Проверяем: принадлежит ли хост-стена данному помещению?
                    // Достаточно проверить FromRoom/ToRoom для двери,
                    // или просто — проверить с обеих сторон стены
                    var loc = fi.Location as LocationPoint;
                    if (loc != null)
                    {
                        XYZ pt = loc.Point;
                        // Нормаль стены
                        var wallCurve = (hostWall.Location as LocationCurve)?.Curve;
                        if (wallCurve != null)
                        {
                            XYZ tan = (wallCurve.GetEndPoint(1) - wallCurve.GetEndPoint(0)).Normalize();
                            XYZ n1  = new XYZ( tan.Y, -tan.X, 0);
                            XYZ n2  = new XYZ(-tan.Y,  tan.X, 0);
                            double offset = 1.0; // ~30 см в футах

                            XYZ probe1 = new XYZ(pt.X + n1.X * offset, pt.Y + n1.Y * offset, pt.Z + 1.0);
                            XYZ probe2 = new XYZ(pt.X + n2.X * offset, pt.Y + n2.Y * offset, pt.Z + 1.0);

                            var r1 = RoomAt(probe1, phase);
                            var r2 = RoomAt(probe2, phase);

                            return (r1 != null && r1.Id == room.Id) ||
                                   (r2 != null && r2.Id == room.Id);
                        }
                    }
                }

                // Fallback для элементов без хост-стены
                var location = element.Location;
                if (location == null) return false;

                XYZ point = null;
                if (location is LocationPoint locPoint)
                    point = locPoint.Point;
                else if (location is LocationCurve locCurve)
                    point = locCurve.Curve.Evaluate(0.5, true);

                if (point == null) return false;

                var roomAtPoint = RoomAt(new XYZ(point.X, point.Y, point.Z + 1.0), phase);
                return roomAtPoint?.Id == room.Id;
            }
            catch (Exception ex)
            {
                Logger.Warn($"IsElementInRoom: ошибка для {element?.Id?.IntValue()}", ex);
                return false;
            }
        }

        /// <summary>Помещение в точке с учётом фазы; без фазы — как раньше.</summary>
        private Room RoomAt(XYZ point, Phase phase)
        {
            return phase != null
                ? _document.GetRoomAtPoint(point, phase)
                : _document.GetRoomAtPoint(point);
        }

        /// <summary>
        /// Наружная ли стена. Осталась ради <see cref="IsExternalDoor"/>: наружность
        /// двери определяется по её хост-стене. Наружность стен ПОМЕЩЕНИЯ считает
        /// GeometryCollector — по границе, с разрешением отделочного слоя.
        /// </summary>
        private bool IsExternalWall(Wall wall)
        {
            // 1. Проверка по параметру "Наружная"
            var externalParam = wall.LookupParameter("Наружная");
            if (externalParam != null && externalParam.HasValue)
                return externalParam.AsInteger() == 1;

            // 2. Функция стены. Читается С ТИПА: FUNCTION_PARAM — параметр WallType,
            //    и на экземпляре get_Parameter возвращает null. Из-за этого проверка
            //    молча не срабатывала никогда, и наружность двери определялась
            //    только по имени стены-хоста: дверь в стене «Монолит 200» или
            //    «Отделка Штук15» считалась внутренней, теряя и Q через дверь,
            //    и надбавку β на врывание холодного воздуха.
            try
            {
                var functionParam = wall.WallType?.get_Parameter(BuiltInParameter.FUNCTION_PARAM);
                if (functionParam != null && functionParam.HasValue)
                    return functionParam.AsInteger() == (int)WallFunction.Exterior;
            }
            catch (Exception ex)
            {
                Logger.Debug($"IsExternalWall: функция типа стены недоступна: {ex.Message}");
            }

            // 3. Проверка по имени
            string wallName = wall.Name?.ToLower() ?? "";
            return wallName.Contains("наруж") || 
                   wallName.Contains("external") || 
                   wallName.Contains("внеш") ||
                   wallName.Contains("фасад");
        }

        private string DetermineGlassType(FamilyInstance window)
        {
            string name = window.Name.ToLower();
            if (name.Contains("однокамер")) return "Однокамерный";
            if (name.Contains("двухкамер")) return "Двухкамерный";
            if (name.Contains("трехкамер")) return "Трехкамерный";
            if (name.Contains("энергосберегающ")) return "Энергосберегающий";
            if (name.Contains("аргон")) return "С аргоном";
            
            return "Двухкамерный";
        }

        private int DetermineChamberCount(FamilyInstance window)
        {
            // 1. По имени экземпляра / типа
            string name = window.Name.ToLower();
            if (name.Contains("однокамер")) return 1;
            if (name.Contains("двухкамер") || name.Contains("двукамер")) return 2;
            if (name.Contains("трехкамер") || name.Contains("трёхкамер")) return 3;

            // 2. По описанию типа и комментариям
            string[] paramNames = { "Описание", "Комментарии", "Описание типа",
                                    "Примечания", "Description", "Type Comments",
                                    "Марка", "Mark" };
            foreach (var pName in paramNames)
            {
                var p = window.LookupParameter(pName) ?? window.Symbol?.LookupParameter(pName);
                if (p != null && p.HasValue)
                {
                    string val = (p.AsString() ?? p.AsValueString() ?? "").ToLower();
                    if (val.Contains("однокамер")) return 1;
                    if (val.Contains("двухкамер") || val.Contains("двукамер")) return 2;
                    if (val.Contains("трехкамер") || val.Contains("трёхкамер")) return 3;
                    // Формула стекла: 4-12-4-12-4 = двухкамерный (3 стекла = 2 камеры)
                    if (System.Text.RegularExpressions.Regex.IsMatch(val, @"\d+-\d+-\d+-\d+-\d+"))
                        return 2;
                    if (System.Text.RegularExpressions.Regex.IsMatch(val, @"\d+-\d+-\d+"))
                        return 1;
                }
            }

            // 3. Анализ материалов откосов — если есть "Четв" в имени (четверть),
            //    это современное окно, скорее всего двухкамерный стеклопакет
            if (name.Contains("четв")) return 2;

            // 4. По умолчанию — двухкамерный (стандарт для современных проектов)
            //    Audytor CO тоже по умолчанию ставит двухкамерный
            return 2;
        }

        private double GetWindowUValue(string glassType, int chambers)
        {
            // Нормативные значения по СП 50.13330.2012 Таблица 3:
            //   R₀ для стеклопакетов в ПВХ/алюминиевом переплёте:
            //   Однокамерный (4М1-16-4М1): R=0.32 → U=3.13; с и-стеклом: R=0.56 → U=1.79
            //   Двухкамерный (4М1-12-4М1-12-4М1): R=0.44 → U=2.27; с аргон+и: R=0.65 → U=1.54
            //   Трёхкамерный: R=0.80+ → U=1.25
            // Используем значения для СОВРЕМЕННЫХ окон (с аргоном / и-стеклом) —
            // это соответствует реальным проектам и методике Audytor CO.
            switch (chambers)
            {
                case 0:  return 1.33; // Неизвестный = современный (и-стекло+аргон, R=0.75)
                case 1:  return 1.80; // Однокамерный с и-стеклом (R=0.56)
                case 2:  return 1.33; // Двухкамерный с и-стеклом+аргон (R=0.75, как Audytor CO)
                case 3:  return 1.00; // Трёхкамерный энергоэффективный (R=1.0)
                default: return 1.33;
            }
        }

        private string DetermineDoorMaterial(FamilyInstance door)
        {
            string name = door.Name.ToLower();
            if (name.Contains("деревян")) return "Дерево";
            if (name.Contains("металл")) return "Металл";
            if (name.Contains("пластик")) return "Пластик";
            if (name.Contains("стеклян")) return "Стекло";
            if (name.Contains("алюмин")) return "Алюминий";
            
            return "Дерево";
        }

        private string DetermineDoorType(FamilyInstance door)
        {
            string name = door.Name.ToLower();
            if (name.Contains("тройн") || name.Contains("трехстворч")) return "Тройные двери с двумя тамбурами";
            if (name.Contains("двойн") || name.Contains("двустворч"))
            {
                if (name.Contains("тамбур") || name.Contains("vestibule"))
                    return "Двойные двери с тамбуром";
                return "Двойные двери без тамбура";
            }
            return "Одинарные двери";
        }

        private bool IsExternalDoor(FamilyInstance door)
        {
            // Сначала — что ПО ОБЕ СТОРОНЫ двери, и только потом функция стены.
            // Функции типов в моделях не следят: в московской модели (2026-10-07)
            // «Перегородка Кирп120 рядовой» помечена Exterior, и дверь санузла
            // в коридор считалась наружной — 251 помещение получало β врывания
            // 0,22·H здания у обычной межкомнатной двери. Если дверь ведёт
            // из отапливаемого помещения в отапливаемое, она внутренняя,
            // что бы ни говорил тип хост-стены.
            try
            {
                var from = door.FromRoom;
                var to   = door.ToRoom;
                if (from != null && to != null)
                {
                    bool fromHeated = IsHeatedSide(from);
                    bool toHeated   = IsHeatedSide(to);
                    if (fromHeated && toHeated) return false;
                    // Одна из сторон — лоджия, лестница, шахта, подвал:
                    // дверь ограждающая, ΔT до соседа поставит движок
                    // через AdjacentCategory при привязке к сегменту.
                    return true;
                }
            }
            catch (Exception ex)
            {
                Logger.Debug($"IsExternalDoor: FromRoom/ToRoom недоступны: {ex.Message}");
            }

            // Помещение только с одной стороны либо стороны не определились —
            // прежняя логика по хост-стене.
            var host = door.Host as Wall;
            if (host != null)
                return IsExternalWall(host);

            return false;
        }

        /// <summary>
        /// Сторона двери — отапливаемое помещение. Неотапливаемой стороной
        /// считаются те же категории, что и у стен (<see
        /// cref="ThermalConstants.UnheatedOrCommonCategories"/> плюс шахта):
        /// дверь на лоджию или в лестничную клетку — ограждающая конструкция.
        /// </summary>
        private static bool IsHeatedSide(Room room)
        {
            string name = room.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString() ?? "";
            var category = RoomCategoryHelper.Detect(name);
            return category != RoomCategory.Shaft &&
                   !ThermalConstants.UnheatedOrCommonCategories.Contains(category);
        }

        private double GetDoorUValue(string material)
        {
            switch (material.ToLower())
            {
                case "дерево": return 2.0;
                case "металл": return 3.0;
                case "пластик": return 1.5;
                case "стекло": return 2.5;
                case "алюминий": return 3.5;
                default: return 2.0;
            }
        }

        // HasVestibule удалён. Он считал, что тамбур есть, если в кубе ±2 м стоит
        // ещё одна дверь, писал результат в DoorInfo.HasVestibule — и это поле
        // не читал НИКТО. Тип двери (а с ним и β) определяется по имени семейства
        // в DetermineDoorType; наличие тамбура из соседства дверей не выводится:
        // две двери рядом бывают и в проходном коридоре. Понадобится — делать
        // по-настоящему, через помещение за дверью.
    }
}