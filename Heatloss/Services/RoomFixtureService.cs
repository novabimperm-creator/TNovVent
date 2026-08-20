using QOVETER.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;

namespace QOVETER.Services
{
    /// <summary>
    /// Выгрузка и загрузка ВХОДНЫХ данных расчёта (собранных <see cref="RoomData"/>) в JSON.
    ///
    /// Зачем: <see cref="JsonExportService"/> сохраняет результат, а для регрессионных тестов
    /// нужен вход — чтобы прогнать <see cref="CalculationEngine"/> на реальной модели
    /// БЕЗ Revit. Один раз выгрузили типовой этаж из модели — дальше движок гоняется
    /// на этой фикстуре в автотестах.
    ///
    /// Формат намеренно плоский и без типов Revit (<c>ElementId</c>, <c>XYZ</c>,
    /// <c>LocationCurve</c>): фикстура должна читаться в тестовом процессе, где Revit нет.
    ///
    /// ВАЖНО: <see cref="DataContractJsonSerializer"/> читает поля в порядке
    /// <c>DataMember.Order</c>. Если правите фикстуру руками — не переставляйте ключи.
    /// </summary>
    public class RoomFixtureService
    {
        /// <summary>
        /// Версия формата. Растёт при несовместимых изменениях схемы.
        /// v2 — добавлено поле <c>Apartment</c> (квартирная группировка, этап 3).
        /// v3 — у ограждений появились <c>Length</c> и <c>Height</c>: без габаритов
        /// не восстановить длины линейных узлов для R_пр (этап 4).
        /// v4 — конструкция наружной стены: тип, λ основания, R утеплителя.
        /// По ним выбираются таблицы СП 230, из одних площадей их не подобрать.
        /// v5 — остальные оси таблиц СП: толщина кладки, толщина основания и
        /// комплексный параметр облицовки панели. Без них фикстура с кладкой
        /// не подбирала Г.27, а с панелью — Г.50–Г.52, и расчёт тихо уходил
        /// на ручной каталог с предварительными Ψ.
        /// v6 — уровень помещения (<c>LevelId</c>, <c>LevelName</c>, <c>Elevation</c>)
        /// и признак <c>IsUnderRoof</c>. Без них фикстура из реальной модели давала
        /// ДРУГИЕ числа, чем сама модель: «под кровлей» определяется геометрически
        /// при сборе (<c>GeometryCollector.DetectRoomsUnderRoof</c>) и в фикстуру
        /// не попадало, а без уровня не работали ни <c>AutoDetectFloorRange</c>,
        /// ни предупреждение о слиянии квартир с одинаковым номером. Сеть безопасности
        /// не могла воспроизвести то, что проверяла.
        /// v7 — у ограждений появилось `Adjacent`: что за стеной. Пусто — наружный
        /// воздух, иначе категория НЕотапливаемого помещения (лоджия, лестница,
        /// тамбур). Без него фикстура считает стену на лоджию по наружной ΔT
        /// и завышает потери втрое против модели.
        /// Старые фикстуры читаются: отсутствующие поля дают пустую квартиру
        /// (покомнатный расчёт), оценку длин из площади, ручной ввод конструкции
        /// и «за стеной улица» для всех ограждений.
        /// </summary>
        public const int FormatVersion = 7;

        public void Save(List<RoomData> rooms, string filePath, double externalTemperature,
                         string description = null)
        {
            if (rooms == null || rooms.Count == 0)
                throw new ArgumentException("Список помещений пуст", nameof(rooms));

            var dto = new RoomFixtureFileDto
            {
                FormatVersion       = FormatVersion,
                GeneratedAt         = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss"),
                Description         = description ?? "Выгрузка входных данных из модели Revit",
                Source              = "QOVETER, выгрузка из активной модели",
                ExternalTemperature = externalTemperature,
                Rooms               = rooms.Select(MapRoom).ToList()
            };

            var dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var serializer = new DataContractJsonSerializer(typeof(RoomFixtureFileDto));
            using (var memory = new MemoryStream())
            {
                serializer.WriteObject(memory, dto);
                string raw = Encoding.UTF8.GetString(memory.ToArray());
                // UTF-8 БЕЗ BOM: DataContractJsonSerializer.ReadObject спотыкается о BOM
                // («обнаружен непредвиденный символ ï») и файл перестаёт читаться.
                File.WriteAllText(filePath, raw, new UTF8Encoding(false));
            }

            Logger.Info($"RoomFixture: сохранено {dto.Rooms.Count} помещений в {filePath}");
        }

        public RoomFixture Load(string filePath)
        {
            if (!File.Exists(filePath))
                throw new FileNotFoundException($"Фикстура не найдена: {filePath}", filePath);

            var serializer = new DataContractJsonSerializer(typeof(RoomFixtureFileDto));
            RoomFixtureFileDto dto;
            using (var stream = File.OpenRead(filePath))
            {
                dto = (RoomFixtureFileDto)serializer.ReadObject(stream);
            }

            if (dto == null)
                throw new InvalidDataException($"Не удалось прочитать фикстуру: {filePath}");
            if (dto.FormatVersion > FormatVersion)
                throw new InvalidDataException(
                    $"Фикстура версии {dto.FormatVersion}, поддерживается до {FormatVersion}: {filePath}");

            return new RoomFixture
            {
                Description         = dto.Description,
                Source              = dto.Source,
                ExternalTemperature = dto.ExternalTemperature,
                Rooms               = (dto.Rooms ?? new List<RoomFixtureRoomDto>()).Select(MapRoom).ToList()
            };
        }

        // ─── Маппинг ──────────────────────────────────────────────

        private static RoomFixtureRoomDto MapRoom(RoomData r) => new RoomFixtureRoomDto
        {
            Number                = r.Number,
            Name                  = r.Name,
            Type                  = r.Type,
            Category              = r.Category.ToString(),
            Apartment             = r.Apartment ?? string.Empty,
            WallConstruction      = r.WallConstruction?.Construction.ToString() ?? "Unknown",
            BaseConductivity      = r.WallConstruction?.BaseConductivity ?? 0,
            InsulationResistance  = r.WallConstruction?.InsulationResistance ?? 0,
            WallThicknessMm       = r.WallConstruction?.WallThicknessMm ?? 0,
            BaseThicknessMm       = r.WallConstruction?.BaseThicknessMm ?? 0,
            FacingComplexParameter = r.WallConstruction?.FacingComplexParameter ?? 0,
            Area                  = Math.Round(r.Area, 3),
            Height                = Math.Round(r.Height, 3),
            Volume                = Math.Round(r.Volume, 3),
            Orientation           = r.Orientation,
            FloorNumber           = r.FloorNumber,
            LevelId               = r.LevelId,
            LevelName             = r.LevelName,
            Elevation             = Math.Round(r.Elevation, 3),
            IsUnderRoof           = r.IsUnderRoof,
            IsCorner              = r.IsCorner,
            NumberOfExternalWalls = r.NumberOfExternalWalls,
            WallArea              = Math.Round(r.WallArea, 3),
            WindowArea            = Math.Round(r.WindowArea, 3),
            DoorArea              = Math.Round(r.DoorArea, 3),
            AverageInverseUValue  = Math.Round(r.AverageInverseUValue, 4),
            Walls = (r.Walls ?? new List<WallInfo>())
                .Where(w => w.IsExternal)
                .Select(w => new FixtureSurfaceDto
                {
                    TypeName    = w.TypeName,
                    Area        = Math.Round(w.Area, 3),
                    UValue      = Math.Round(w.UValue, 4),
                    Orientation = w.Orientation,
                    Length      = Math.Round(w.Length, 3),
                    Height      = Math.Round(w.Height, 3),
                    Adjacent    = w.AdjacentCategory?.ToString()
                }).ToList(),
            Windows = (r.Windows ?? new List<WindowInfo>())
                .Select(w => new FixtureSurfaceDto
                {
                    TypeName    = w.TypeName,
                    Area        = Math.Round(w.Area, 3),
                    UValue      = Math.Round(w.UValue, 4),
                    Orientation = w.Orientation,
                    Length      = Math.Round(w.Width, 3),
                    Height      = Math.Round(w.Height, 3),
                    Adjacent    = w.AdjacentCategory?.ToString()
                }).ToList(),
            Doors = (r.Doors ?? new List<DoorInfo>())
                .Where(d => d.IsExternal)
                .Select(d => new FixtureDoorDto
                {
                    TypeName = d.TypeName,
                    Area     = Math.Round(d.Area, 3),
                    UValue   = Math.Round(d.UValue, 4),
                    DoorType = d.DoorType
                }).ToList()
        };

        /// <summary>
        /// Восстанавливает конструкцию стены из фикстуры. Для файлов до v4 поля нет —
        /// возвращается null, и расчёт R_пр по таблицам СП 230 для такой фикстуры
        /// не выполняется (нужен ручной ввод конструкции).
        /// </summary>
        private static WallConstructionProfile RestoreConstruction(RoomFixtureRoomDto d)
        {
            WallConstructionType type;
            if (string.IsNullOrWhiteSpace(d.WallConstruction) ||
                !Enum.TryParse(d.WallConstruction, out type) ||
                type == WallConstructionType.Unknown)
            {
                return null;
            }

            // Толщина кладки, толщина основания и комплексный параметр облицовки —
            // такие же ОСИ таблиц СП 230, как λ и R утеплителя. Без них фикстура
            // с кладкой (Г.27) или с панелью (Г.50–Г.52) не подбирала таблицу и
            // молча падала на ручной каталог с предварительными Ψ.
            return new WallConstructionProfile
            {
                Construction           = type,
                BaseConductivity       = d.BaseConductivity > 0 ? d.BaseConductivity : (double?)null,
                InsulationResistance   = d.InsulationResistance > 0 ? d.InsulationResistance : (double?)null,
                WallThicknessMm        = d.WallThicknessMm > 0 ? d.WallThicknessMm : (double?)null,
                BaseThicknessMm        = d.BaseThicknessMm > 0 ? d.BaseThicknessMm : (double?)null,
                FacingComplexParameter = d.FacingComplexParameter > 0
                    ? d.FacingComplexParameter
                    : (double?)null
            };
        }

        /// <summary>
        /// Что за ограждением из фикстуры. Пусто или нераспознанное — наружный воздух
        /// (так читаются все фикстуры до v7).
        /// </summary>
        private static RoomCategory? ParseAdjacent(string value)
        {
            RoomCategory category;
            if (string.IsNullOrWhiteSpace(value) || !Enum.TryParse(value, out category))
                return null;
            return category;
        }

        private static RoomData MapRoom(RoomFixtureRoomDto d)
        {
            RoomCategory category;
            if (!Enum.TryParse(d.Category ?? "Other", out category))
                category = RoomCategory.Other;

            return new RoomData
            {
                Number                = d.Number,
                Name                  = d.Name,
                Type                  = d.Type,
                Category              = category,
                Apartment             = d.Apartment ?? string.Empty,
                WallConstruction      = RestoreConstruction(d),
                Area                  = d.Area,
                Height                = d.Height > 0 ? d.Height : 3.0,
                Volume                = d.Volume > 0 ? d.Volume : d.Area * 3.0,
                Orientation           = d.Orientation,
                FloorNumber           = d.FloorNumber,
                // Уровень и признак кровли (v6). В фикстурах до v6 их нет — тогда
                // LevelId = 0 у всех, и это ровно прежнее поведение.
                LevelId               = d.LevelId,
                LevelName             = d.LevelName,
                Elevation             = d.Elevation,
                IsUnderRoof           = d.IsUnderRoof,
                IsCorner              = d.IsCorner,
                NumberOfExternalWalls = d.NumberOfExternalWalls,
                WallArea              = d.WallArea,
                WindowArea            = d.WindowArea,
                DoorArea              = d.DoorArea,
                AverageInverseUValue  = d.AverageInverseUValue,
                IsSelected            = true,
                Walls = (d.Walls ?? new List<FixtureSurfaceDto>())
                    .Select(w => new WallInfo
                    {
                        TypeName    = w.TypeName,
                        Area        = w.Area,
                        UValue      = w.UValue,
                        Orientation = w.Orientation,
                        Length      = w.Length,
                        Height      = w.Height,
                        IsExternal  = true,
                        AdjacentCategory = ParseAdjacent(w.Adjacent)
                    }).ToList(),
                Windows = (d.Windows ?? new List<FixtureSurfaceDto>())
                    .Select(w => new WindowInfo
                    {
                        TypeName    = w.TypeName,
                        Area        = w.Area,
                        UValue      = w.UValue,
                        Orientation = w.Orientation,
                        Width       = w.Length,
                        Height      = w.Height,
                        AdjacentCategory = ParseAdjacent(w.Adjacent)
                    }).ToList(),
                Doors = (d.Doors ?? new List<FixtureDoorDto>())
                    .Select(x => new DoorInfo
                    {
                        TypeName   = x.TypeName,
                        Area       = x.Area,
                        UValue     = x.UValue,
                        IsExternal = true,
                        DoorType   = string.IsNullOrEmpty(x.DoorType) ? "Одинарные двери" : x.DoorType
                    }).ToList()
            };
        }
    }

    /// <summary>Загруженная фикстура: входные данные расчёта без привязки к Revit.</summary>
    public class RoomFixture
    {
        public string Description { get; set; }
        public string Source { get; set; }
        public double ExternalTemperature { get; set; }
        public List<RoomData> Rooms { get; set; } = new List<RoomData>();
    }

    // ─── DTO (порядок DataMember важен для DataContractJsonSerializer) ───

    [DataContract]
    public class RoomFixtureFileDto
    {
        [DataMember(Order = 1)] public int FormatVersion { get; set; }
        [DataMember(Order = 2)] public string GeneratedAt { get; set; }
        [DataMember(Order = 3)] public string Description { get; set; }
        [DataMember(Order = 4)] public string Source { get; set; }
        [DataMember(Order = 5)] public double ExternalTemperature { get; set; }
        [DataMember(Order = 6)] public List<RoomFixtureRoomDto> Rooms { get; set; } = new List<RoomFixtureRoomDto>();
    }

    [DataContract]
    public class RoomFixtureRoomDto
    {
        [DataMember(Order = 1)]  public string Number { get; set; }
        [DataMember(Order = 2)]  public string Name { get; set; }
        [DataMember(Order = 3)]  public string Type { get; set; }
        [DataMember(Order = 4)]  public string Category { get; set; }
        /// <summary>Номер квартиры (формат v2). В фикстурах v1 отсутствует.</summary>
        [DataMember(Order = 5)]  public string Apartment { get; set; }
        /// <summary>Тип конструкции наружной стены — по нему выбираются таблицы СП 230 (v4).</summary>
        [DataMember(Order = 6)]  public string WallConstruction { get; set; }
        /// <summary>λ основания стены, Вт/(м·°С) — ось таблиц СП 230 (v4).</summary>
        [DataMember(Order = 7)]  public double BaseConductivity { get; set; }
        /// <summary>R утеплителя стены, м²·°С/Вт — ось таблиц СП 230 (v4).</summary>
        [DataMember(Order = 8)]  public double InsulationResistance { get; set; }
        [DataMember(Order = 9)]  public double Area { get; set; }
        [DataMember(Order = 10)] public double Height { get; set; }
        [DataMember(Order = 11)] public double Volume { get; set; }
        [DataMember(Order = 12)] public string Orientation { get; set; }
        [DataMember(Order = 13)] public int FloorNumber { get; set; }
        [DataMember(Order = 14)] public bool IsCorner { get; set; }
        [DataMember(Order = 15)] public int NumberOfExternalWalls { get; set; }
        [DataMember(Order = 16)] public double WallArea { get; set; }
        [DataMember(Order = 17)] public double WindowArea { get; set; }
        [DataMember(Order = 18)] public double DoorArea { get; set; }
        [DataMember(Order = 19)] public double AverageInverseUValue { get; set; }
        [DataMember(Order = 20)] public List<FixtureSurfaceDto> Walls { get; set; } = new List<FixtureSurfaceDto>();
        [DataMember(Order = 21)] public List<FixtureSurfaceDto> Windows { get; set; } = new List<FixtureSurfaceDto>();
        [DataMember(Order = 22)] public List<FixtureDoorDto> Doors { get; set; } = new List<FixtureDoorDto>();

        // Поля v5 дописаны В КОНЕЦ, а не рядом с WallConstruction: DataContractJsonSerializer
        // читает члены по порядку, и вставка в середину сдвинула бы разбор старых фикстур.
        /// <summary>Толщина кладки d_кл, мм — ось таблицы Г.27 (v5).</summary>
        [DataMember(Order = 23)] public double WallThicknessMm { get; set; }
        /// <summary>Толщина основания d_о, мм — признак выбора таблиц Г.24–Г.26 (v5).</summary>
        [DataMember(Order = 24)] public double BaseThicknessMm { get; set; }
        /// <summary>Комплексный параметр облицовки панели, Вт/°С — ось таблиц Г.50–Г.52 (v5).</summary>
        [DataMember(Order = 25)] public double FacingComplexParameter { get; set; }

        // Поля v6 — тоже В КОНЕЦ, по той же причине.
        /// <summary>Id уровня: по нему считается этажность и ловится слияние квартир (v6).</summary>
        [DataMember(Order = 26)] public int LevelId { get; set; }
        /// <summary>Имя уровня — из него разбирается номер этажа со знаком (v6).</summary>
        [DataMember(Order = 27)] public string LevelName { get; set; }
        /// <summary>Отметка уровня, м: границы этажности берутся по ней, а не по номеру (v6).</summary>
        [DataMember(Order = 28)] public double Elevation { get; set; }
        /// <summary>Над помещением нет отапливаемого — значит кровля (v6).</summary>
        [DataMember(Order = 29)] public bool IsUnderRoof { get; set; }
    }

    [DataContract]
    public class FixtureSurfaceDto
    {
        [DataMember(Order = 1)] public string TypeName { get; set; }
        [DataMember(Order = 2)] public double Area { get; set; }
        [DataMember(Order = 3)] public double UValue { get; set; }
        [DataMember(Order = 4)] public string Orientation { get; set; }
        /// <summary>
        /// Габариты (формат v3): для стены — длина, для окна — ширина. Нужны для
        /// восстановления длин линейных узлов в расчёте R_пр. В фикстурах v1/v2
        /// отсутствуют — тогда длины оцениваются из площади.
        /// </summary>
        [DataMember(Order = 5)] public double Length { get; set; }
        [DataMember(Order = 6)] public double Height { get; set; }

        /// <summary>
        /// Что за ограждением (v7): пусто — наружный воздух, иначе имя
        /// <c>RoomCategory</c> неотапливаемого помещения. От этого зависит ΔT.
        /// </summary>
        [DataMember(Order = 7)] public string Adjacent { get; set; }
    }

    [DataContract]
    public class FixtureDoorDto
    {
        [DataMember(Order = 1)] public string TypeName { get; set; }
        [DataMember(Order = 2)] public double Area { get; set; }
        [DataMember(Order = 3)] public double UValue { get; set; }
        [DataMember(Order = 4)] public string DoorType { get; set; }
    }
}
