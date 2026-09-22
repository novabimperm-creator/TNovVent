using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using System.Collections.Generic;
using System.Linq;

using static TNovCommon.ElementIdCompat;

namespace QOVETER.Models
{
    public class RoomData
    {
        // Основные свойства
        public int Id { get; set; }
        public string Name { get; set; }
        public string Number { get; set; }
        public double Area { get; set; }
        public double Volume { get; set; }
        public double Height { get; set; } = 3.0;
        public string Type { get; set; }

        /// <summary>
        /// Категория помещения (enum). Источник правды для расчётных коэффициентов.
        /// Заполняется в GeometryCollector.DetermineRoomType одновременно с <c>Type</c>.
        /// </summary>
        public RoomCategory Category { get; set; } = RoomCategory.Other;
        public string Orientation { get; set; }

        /// <summary>
        /// Номер квартиры. Пустая строка — помещение вне квартиры (общедомовое)
        /// либо признак не найден в модели. По ТЗ воздухообмен нормируется на квартиру,
        /// поэтому без этого признака расчёт откатывается к покомнатному приближению.
        /// Заполняется в <c>GeometryCollector.DetermineApartment</c>.
        /// </summary>
        public string Apartment { get; set; }

        public int LevelId { get; set; }

        /// <summary>
        /// Контур помещения в плане, м — внешний обход границы. Нужен зональному
        /// методу (СП 50.13330.2024 Г.7): зоны отсчитываются от контура ЗДАНИЯ,
        /// поэтому одной площади мало, нужна форма и положение.
        /// </summary>
        public List<Services.Point2D> FloorOutline { get; set; } = new List<Services.Point2D>();

        /// <summary>
        /// Пол помещения лежит НА ГРУНТЕ: под ним нет других помещений.
        /// Отличается от <see cref="IsFirstFloor"/> — при частичном подвале
        /// первый этаж стоит и на грунте, и над подвалом одновременно.
        /// </summary>
        public bool FloorOnGround { get; set; }

        /// <summary>Площади зон пола по грунту, м² (СП 50.13330.2024, таблица Г.3).</summary>
        public List<Services.GroundZoneArea> GroundFloorZones { get; set; }

        /// <summary>Площади зон заглублённых стен, м² (таблица Г.4).</summary>
        public List<Services.GroundZoneArea> GroundWallZones { get; set; }

        /// <summary>
        /// Термическое сопротивление конструкции заглублённых стен, м²·°С/Вт —
        /// слагаемое δ_ут/λ_ут формулы (Г.18), средневзвешенное по площади.
        /// </summary>
        public double GroundWallResistance { get; set; }
        public string LevelName { get; set; }
        public int FloorNumber { get; set; } = 1; // Номер этажа
        public double Elevation { get; set; }
        public bool IsCorner { get; set; }
        public bool IsSelected { get; set; } = true;
        public List<System.Windows.Point> BoundaryPoints { get; set; } = new List<System.Windows.Point>();

        // Связанные элементы Revit
        public ElementId RoomElementId { get; set; }
        public ElementId LevelElementId { get; set; }
        public string SourceModel { get; set; }
        public bool IsFromLinkedModel { get; set; }

        // Ограждающие конструкции (реальные данные)
        public List<WallInfo> Walls { get; set; } = new List<WallInfo>();
        public List<WindowInfo> Windows { get; set; } = new List<WindowInfo>();
        public List<DoorInfo> Doors { get; set; } = new List<DoorInfo>();

        // Расчетные площади
        public double WallArea { get; set; }
        public double WindowArea { get; set; }
        public double DoorArea { get; set; }
        public bool HasExternalDoor { get; set; }
        public int NumberOfExternalWalls { get; set; }
        public bool IsFirstFloor { get; set; }
        public bool IsLastFloor { get; set; }

        /// <summary>
        /// Над помещением НЕТ отапливаемого помещения — значит над ним кровля.
        /// Определяется геометрически при сборе из модели (см.
        /// <c>GeometryCollector.DetectRoomsUnderRoof</c>).
        ///
        /// Зачем отдельно от <see cref="IsLastFloor"/>: «верхний этаж» — один номер
        /// на всё здание, а верхний уровень сплошь и рядом технический. На
        /// 76-СУЗДАЛ.23 верхним оказался этаж 16 с ЕДИНСТВЕННЫМ помещением 16,5 м²,
        /// и 355 м² перекрытия 15-го этажа — реально под кровлей — кровельных
        /// потерь не получали вовсе.
        ///
        /// По умолчанию false: фикстуры и синтетические тесты работают по номеру
        /// этажа, как раньше.
        /// </summary>
        public bool IsUnderRoof { get; set; }

        // Теплотехнические характеристики
        public double HeatLossCoefficient { get; set; }

        /// <summary>
        /// Конструкция наружной стены помещения — по ней выбирается таблица СП 230
        /// для расчёта приведённого сопротивления. Заполняется из слоёв
        /// <c>CompoundStructure</c>; чего в модели нет, задаётся вручную.
        /// </summary>
        public WallConstructionProfile WallConstruction { get; set; }

        /// <summary>
        /// Наружных углов помещения, распознанных ГЕОМЕТРИЕЙ границы: выпуклых
        /// (здание выступает наружу) и вогнутых (ниша в фасаде).
        ///
        /// <para><b>Зачем раздельно.</b> СП 230 раздел Г.4: угол — чисто геометрический
        /// элемент, выпуклый теплопотери добавляет, вогнутый ВЫЧИТАЕТ, и таблицы
        /// Г.27/Г.28 дают для него отрицательные Ψ. До 2026-08-20 все углы считались
        /// выпуклыми: число углов бралось как «наружных стен минус одна», а это
        /// вообще не геометрия — у комнаты с окнами на север и на юг стыка между
        /// стенами нет, а угол ей начислялся.</para>
        ///
        /// <para><c>null</c> — геометрии не было (фикстура, синтетика в тестах),
        /// работает прежнее правило по числу наружных стен. Поэтому golden-тесты
        /// эта правка не двигает.</para>
        /// </summary>
        public int? ConvexCorners { get; set; }
        /// <summary>Вогнутых наружных углов, распознанных геометрией. См. <see cref="ConvexCorners"/>.</summary>
        public int? ConcaveCorners { get; set; }

        /// <summary>
        /// Средневзвешенное по площади обратное U-значение наружных стен помещения, м²·К/Вт.
        /// Хранит величину 1/U_avg, где U_avg = Σ(U·A) / ΣA.
        /// Используется в расчёте теплопотерь как: U_eff = 1 / AverageInverseUValue.
        /// Для однородной стены численно совпадает с её R; для неоднородной — отличается
        /// от среднего R, но даёт корректный суммарный поток через стены помещения.
        /// </summary>
        public double AverageInverseUValue { get; set; }

        public string DisplayName => $"{Number} - {Name} ({Area:F1} м²)";

        // Конструктор для создания из Revit элемента
        public static RoomData FromRevitRoom(Room room, string sourceModel = "Текущая модель")
        {
            if (room == null) return null;

            // ─── Площадь: ВСЕГДА внутренние единицы Revit = кв. футы → м² ───────────────
            // Нельзя использовать эвристику (if area > 1000) — площадь в sq.ft может быть
            // любой. Используем строгий UnitUtils (Revit 2022 API).
            double area = 0;
            try
            {
                Parameter areaParam = room.get_Parameter(BuiltInParameter.ROOM_AREA);
                if (areaParam != null && areaParam.HasValue)
                    area = UnitUtils.ConvertFromInternalUnits(areaParam.AsDouble(),
                                                              UnitTypeId.SquareMeters);
            }
            catch { area = 0; }

            // ─── Высота помещения (UnboundedHeight — в футах, конвертируем) ─────────────
            double height = 3.0; // fallback
            try
            {
                double heightFt = room.UnboundedHeight;
                if (heightFt > 0)
                    height = UnitUtils.ConvertFromInternalUnits(heightFt, UnitTypeId.Meters);
            }
            catch { /* оставляем 3.0 */ }

            int floorNumber = ParseFloorNumber(room.Level?.Name);

            return new RoomData
            {
                Id             = room.Id.IntValue(),
                RoomElementId  = room.Id,
                Name           = room.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString() ?? "Без названия",
                Number         = room.Number ?? "0",
                Area           = area,
                Height         = height,
                Volume         = area * height,
                LevelId        = room.Level?.Id?.IntValue() ?? -1,
                LevelName      = room.Level?.Name ?? "Неизвестно",
                FloorNumber    = floorNumber,
                LevelElementId = room.Level?.Id,
                Elevation      = room.Level != null
                    ? UnitUtils.ConvertFromInternalUnits(room.Level.Elevation, UnitTypeId.Meters)
                    : 0,
                SourceModel        = sourceModel,
                IsFromLinkedModel  = sourceModel != "Текущая модель"
            };
        }
        
        /// <summary>
        /// Номер этажа из имени уровня. Учитывает ЗНАК кода уровня.
        ///
        /// В модели 76-СУЗДАЛ.23 уровни названы «-01 -3,250 Подземный этаж» и
        /// «01 -0,130 Этаж 1». Прежняя регулярка <c>\d+</c> брала первые цифры
        /// без знака, и оба уровня давали номер 1: подземный этаж СЛИВАЛСЯ
        /// с первым — и в отчёте «По этажам», и в расчёте, где оба получали
        /// теплопотери через пол в грунт.
        ///
        /// Сначала пробуем код уровня со знаком в начале имени («-01» → −1),
        /// затем — первое число где угодно («Этаж 15» → 15).
        /// </summary>
        public static int ParseFloorNumber(string levelName)
        {
            if (string.IsNullOrWhiteSpace(levelName)) return 1;

            var signed = System.Text.RegularExpressions.Regex.Match(levelName, @"^\s*(-\d+)");
            int parsed;
            if (signed.Success && int.TryParse(signed.Groups[1].Value, out parsed))
                return parsed;

            var any = System.Text.RegularExpressions.Regex.Match(levelName, @"\d+");
            if (any.Success && int.TryParse(any.Value, out parsed))
                return parsed;

            return 1;
        }

        /// <summary>
        /// Помещение подземного этажа. ЕДИНСТВЕННОЕ определение на весь проект:
        /// по нему и снимаются подвальные помещения с расчёта (MainWindow.LoadData),
        /// и выбирается нижний этаж здания (BuildingParameters.AutoDetectFloorRange).
        ///
        /// Зачем одно на двоих: 2026-08-06 эти два правила жили порознь. Автоопределение
        /// брало САМЫЙ НИЖНИЙ уровень по отметке — то есть подвал (FloorNumber = −1), —
        /// а сам подвал тем временем снимался с расчёта. В итоге IsFirstFloor не
        /// доставался никому: первый этаж перестал получать теплопотери через пол,
        /// и в журнале прогона на 76-СУЗДАЛ.23 нет ни одной строки [Floor U].
        /// </summary>
        public bool IsUnderground => FloorNumber < 1;

        // Метод для определения, является ли помещение первым этажом
        public void DetermineFloorProperties(int groundFloorNumber, int topFloorNumber)
        {
            IsFirstFloor = (FloorNumber == groundFloorNumber);
            // Кровля — либо по номеру верхнего этажа, либо по факту отсутствия
            // отапливаемого помещения сверху. Второе точнее: верхний уровень
            // здания часто технический и накрывает лишь малую часть плана.
            IsLastFloor = (FloorNumber == topFloorNumber) || IsUnderRoof;
        }
    }

    /// <summary>
    /// Что находится по ту сторону ограждения. <c>null</c> — наружный воздух.
    /// Иначе категория НЕотапливаемого помещения: лоджия, лестничная клетка,
    /// тамбур, техпомещение. Расчётную температуру для неё движок берёт
    /// из той же таблицы <see cref="RoomTypeTemperatures"/>, что и для самих
    /// помещений: лоджия 5 °C, лестница и тамбур 16 °C.
    ///
    /// Зачем: стена между кухней 19 °C и лоджией 5 °C — ограждающая конструкция,
    /// но ΔT у неё 14, а не 48. До 2026-08-10 такие ограждения не считались ВООБЩЕ
    /// (~292 сегмента на прогон 76-СУЗДАЛ.23), а окна в них не находились —
    /// оконно-балконный блок 4,5 м² выпадал из расчёта помещения целиком.
    /// </summary>
    public interface IAdjacentSurface
    {
        RoomCategory? AdjacentCategory { get; set; }

        /// <summary>
        /// Id ПОМЕЩЕНИЯ по ту сторону ограждения; 0 — там улица либо помещения нет.
        ///
        /// Нужен для теплового баланса неотапливаемого объёма (СП 50.13330 п. 5.2):
        /// тёплую сторону баланса лоджии брать больше неоткуда. У самой лоджии
        /// стены на кухню НЕТ — за ней отапливаемый сосед, и ограждением для лоджии
        /// она не считается; эта стена лежит у КУХНИ. Без ссылки на Id связать
        /// одну с другой нельзя, и температура лоджии осталась бы подставленным
        /// числом. См. <see cref="QOVETER.Services.UnheatedVolumes"/>.
        /// </summary>
        int AdjacentRoomId { get; set; }
    }

    public class WallInfo : IAdjacentSurface
    {
        public ElementId Id { get; set; }
        public string TypeName { get; set; }

        /// <inheritdoc cref="IAdjacentSurface"/>
        public RoomCategory? AdjacentCategory { get; set; }

        /// <inheritdoc cref="IAdjacentSurface"/>
        public int AdjacentRoomId { get; set; }
        public double Length { get; set; }
        public double Height { get; set; }
        public double Area { get; set; }
        public bool IsExternal { get; set; }
        public string Material { get; set; }
        public double Thickness { get; set; }
        public double RValue { get; set; }
        public double UValue { get; set; }
        public string Orientation { get; set; }
        public LocationCurve Location { get; set; }
        public int FloorNumber { get; set; } = 1;

        /// <summary>
        /// Какая доля стены (0…1) находится НИЖЕ уровня земли и считается
        /// зональным методом СП 50.13330.2024 Г.7, а не по ΔT наружного воздуха.
        ///
        /// <para>Доля, а не флаг: у подвала верх стены часто выходит из земли,
        /// и цоколь выше отметки — обычное наружное ограждение. Оставшаяся
        /// часть (1 − доля) считается как всегда, иначе площадь потерялась бы.</para>
        /// </summary>
        public double BuriedFraction { get; set; }

        /// <summary>
        /// U взято ИЗ МОДЕЛИ (слои с теплопроводностью, аналитическое R,
        /// параметр стены), а не подставлено оценкой по имени и толщине.
        ///
        /// Разница принципиальная и её обязан видеть тот, кто подписывает отчёт:
        /// на 76-СУЗДАЛ.23 (прогон 2026-08-12) ИЗ МОДЕЛИ считался 1% площади
        /// ограждений, остальные 99% шли по типовому U = 0,51 — при том, что
        /// утеплитель в модели нарисован отдельными элементами и находится
        /// геометрически, но теплопроводность материалов в нём не заполнена.
        /// </summary>
        public bool ThermalFromModel { get; set; }

        /// <summary>
        /// U посчитано по НОРМАТИВУ: толщина из модели, λ из таблицы
        /// СП 50.13330.2012 прил. Т по материалу, распознанному в имени типа.
        ///
        /// <para>Третье состояние, а не оттенок второго. «По данным модели»,
        /// «по таблице норматива» и «типовое U за неимением ничего» — вещи
        /// разного веса, и тот, кто подписывает отчёт, обязан видеть, сколько
        /// площади приходится на каждое. Сливать первые два нельзя: у одного
        /// за спиной проект, у другого — справочная таблица.</para>
        /// </summary>
        public bool ThermalNormative { get; set; }

        /// <summary>Откуда взято U — словами, для отчёта и журнала.</summary>
        public string ThermalSource { get; set; }

        public double CalculateHeatLoss(double deltaT)
        {
            return UValue * Area * deltaT;
        }
    }

    public class WindowInfo : IAdjacentSurface
    {
        public ElementId Id { get; set; }
        public string TypeName { get; set; }

        /// <inheritdoc cref="IAdjacentSurface"/>
        public RoomCategory? AdjacentCategory { get; set; }

        /// <inheritdoc cref="IAdjacentSurface"/>
        public int AdjacentRoomId { get; set; }

        /// <summary>
        /// Остекление НАВЕСНОЙ СТЕНЫ (витража), а не окно в проёме. Появляется
        /// не из категории «Окна», а из сегмента границы помещения, поэтому:
        ///   • площадь не вычитается из стены — стены для этого сегмента и нет;
        ///   • сегмент витража может встретиться у помещения несколько раз,
        ///     и площади складываются, а не схлопываются по Id;
        ///   • в поиске проёмов такие записи пропускаются, иначе витраж
        ///     «привязался» бы ещё и к соседней стене.
        /// </summary>
        public bool IsCurtainGlazing { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }
        public double Area { get; set; }
        public string GlassType { get; set; }
        public int Chambers { get; set; }
        public double UValue { get; set; }
        public ElementId HostWallId { get; set; }
        public string Orientation { get; set; }
        public Location Location { get; set; }
        public Autodesk.Revit.DB.XYZ LocationXYZ { get; set; } // XYZ для пространственной фильтрации

        public double CalculateHeatLoss(double deltaT)
        {
            return UValue * Area * deltaT;
        }
    }

    public class DoorInfo : IAdjacentSurface
    {
        public ElementId Id { get; set; }
        public string TypeName { get; set; }

        /// <inheritdoc cref="IAdjacentSurface"/>
        public RoomCategory? AdjacentCategory { get; set; }

        /// <inheritdoc cref="IAdjacentSurface"/>
        public int AdjacentRoomId { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }
        public double Area { get; set; }
        public string Material { get; set; }
        public bool IsExternal { get; set; }
        public double UValue { get; set; }
        public ElementId HostWallId { get; set; }
        public Location Location { get; set; }
        public Autodesk.Revit.DB.XYZ LocationXYZ { get; set; } // XYZ для пространственной фильтрации
        public string DoorType { get; set; } = "Одинарные двери"; // По умолчанию

        public double CalculateHeatLoss(double deltaT)
        {
            return UValue * Area * deltaT;
        }
        
        // Метод для расчета коэффициента β для двери по ТЗ
        public double CalculateDoorBetaCoefficient(double buildingHeight, Dictionary<string, double> doorCoefficients)
        {
            if (!IsExternal) return 0;
            
            if (doorCoefficients.ContainsKey(DoorType))
            {
                return doorCoefficients[DoorType] * buildingHeight;
            }
            
            // По умолчанию для одинарных дверей
            return 0.22 * buildingHeight;
        }
    }
}