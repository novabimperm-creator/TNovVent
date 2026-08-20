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
    /// Исполнение узлов фасада, заданное инженером ПО ОБЪЕКТУ.
    ///
    /// <para><b>Зачем отдельный файл, а не умолчание в коде.</b> Положение оконной
    /// рамы относительно утеплителя, нахлёст, зуб, перфорация плиты — это ЧЕРТЁЖ
    /// УЗЛА, а не геометрия здания: из модели Revit оно не вытаскивается. При этом
    /// на разных объектах узел разный, и зашитое в программу «типовое» значение
    /// было бы выдуманным числом ровно в том смысле, в каком им было типовое
    /// U = 0,51. Цена вопроса измерена: у оконного узла СФТК Ψ = 0,058 при
    /// нахлёсте утеплителя на раму 20 мм против 0,433 при раме, смещённой от
    /// утеплителя (Г.35) — <b>всемеро</b>, и откос окна дороже угла, потому что
    /// периметр окна 7 м против 3 м высоты стыка.</para>
    ///
    /// <para><b>Почему файл лежит РЯДОМ С МОДЕЛЬЮ.</b> Настройка в
    /// <c>%APPDATA%</c> переживает смену объекта: инженер задал узел одного дома,
    /// открыл другой — и молча посчитал его чужим узлом. Поэтому основной путь
    /// привязан к файлу модели, а запасной (когда папка модели недоступна для
    /// записи или модель не сохранена) — <c>%APPDATA%\QOVETER\nodes\ИМЯ.json</c>,
    /// тоже ПО ИМЕНИ МОДЕЛИ. Общего файла «на все объекты» нет намеренно.</para>
    ///
    /// <para>Приоритет: файл рядом с моделью → файл по имени модели в
    /// <c>%APPDATA%</c> → умолчания <see cref="CalculationParameters.NodeDetails"/>
    /// («в запас», из равных таблиц берётся худшая по потерям).</para>
    /// </summary>
    public static class ThermalNodeSettings
    {
        /// <summary>Расширение файла настроек узлов рядом с моделью.</summary>
        public const string FileSuffix = ".QOVETER-узлы.json";

        /// <summary>
        /// Путь к файлу рядом с моделью. Пусто, если модель не сохранена
        /// (у несохранённого документа <c>PathName</c> пустой).
        /// </summary>
        public static string ModelSidePath(string modelPath)
        {
            if (string.IsNullOrWhiteSpace(modelPath)) return null;
            try
            {
                string dir = Path.GetDirectoryName(modelPath);
                if (string.IsNullOrEmpty(dir)) return null;
                return Path.Combine(dir, Path.GetFileNameWithoutExtension(modelPath) + FileSuffix);
            }
            catch (Exception ex)
            {
                Logger.Debug($"[Узлы] путь рядом с моделью не построен: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Запасной путь в профиле пользователя — ТОЖЕ по имени модели, а не общий:
        /// иначе узел одного объекта применился бы к другому.
        /// </summary>
        public static string ProfilePath(string modelPath)
        {
            string name = string.IsNullOrWhiteSpace(modelPath)
                ? "без имени модели"
                : Path.GetFileNameWithoutExtension(modelPath);

            foreach (char bad in Path.GetInvalidFileNameChars()) name = name.Replace(bad, '_');

            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "QOVETER", "nodes", name + ".json");
        }

        /// <summary>Файл, из которого читались бы настройки этой модели. null — ни одного нет.</summary>
        public static string ExistingPath(string modelPath)
        {
            string beside = ModelSidePath(modelPath);
            if (!string.IsNullOrEmpty(beside) && File.Exists(beside)) return beside;

            string profile = ProfilePath(modelPath);
            return File.Exists(profile) ? profile : null;
        }

        /// <summary>
        /// Читает исполнение узлов для этой модели поверх умолчаний.
        /// Файла нет — возвращаются умолчания без изменений.
        /// </summary>
        /// <param name="modelPath">Полный путь к .rvt; пусто — модель не сохранена.</param>
        /// <param name="defaults">Умолчания расчёта, поверх которых накладывается файл.</param>
        /// <param name="source">Откуда взято: путь к файлу либо «умолчания».</param>
        public static BridgeSelectors Load(string modelPath, BridgeSelectors defaults, out string source)
        {
            var result = Clone(defaults ?? new BridgeSelectors());
            source = "умолчания расчёта (исполнение узлов не задано — берётся худшее по потерям)";

            string path = ExistingPath(modelPath);
            if (path == null) return result;

            try
            {
                ThermalNodeFile file;
                using (var stream = File.OpenRead(path))
                {
                    var serializer = new DataContractJsonSerializer(typeof(ThermalNodeFile));
                    file = serializer.ReadObject(stream) as ThermalNodeFile;
                }

                if (file == null)
                {
                    Logger.Warn($"[Узлы] {path} пуст — применяются умолчания");
                    return result;
                }

                var applied = new List<string>();

                if (file.FrameMm.HasValue)              { result.FrameMm = file.FrameMm;                           applied.Add($"рама {file.FrameMm} мм"); }
                if (file.NotchMm.HasValue)              { result.NotchMm = file.NotchMm;                           applied.Add($"зуб {file.NotchMm} мм"); }
                if (file.OverlapMm.HasValue)            { result.OverlapMm = file.OverlapMm;                       applied.Add($"нахлёст {file.OverlapMm} мм"); }
                if (file.SlabInsulationR.HasValue)      { result.SlabInsulationR = file.SlabInsulationR;           applied.Add($"R утепл. плиты {file.SlabInsulationR}"); }
                if (file.SlabThicknessMm.HasValue)      { result.SlabThicknessMm = file.SlabThicknessMm;           applied.Add($"плита {file.SlabThicknessMm} мм"); }
                if (file.SlabPerforationRatio.HasValue) { result.SlabPerforationRatio = file.SlabPerforationRatio; applied.Add($"перфорация {file.SlabPerforationRatio}"); }
                if (file.ParapetInsulationMm.HasValue)  { result.ParapetInsulationMm = file.ParapetInsulationMm;   applied.Add($"утепл. парапета {file.ParapetInsulationMm} мм"); }
                if (file.AnchorsPerM2.HasValue)         { result.AnchorsPerM2 = file.AnchorsPerM2;                 applied.Add($"анкеров {file.AnchorsPerM2} шт/м²"); }
                if (file.AnchorL1Mm.HasValue)           { result.AnchorL1Mm = file.AnchorL1Mm;                     applied.Add($"L1 анкера {file.AnchorL1Mm} мм"); }

                if (!string.IsNullOrWhiteSpace(file.Execution))
                {
                    // Опечатку принимать нельзя: неизвестное исполнение каталог
                    // молча пропустит мимо и снова возьмёт худшее из равных, а
                    // инженер будет считать, что задал узел. Поэтому — проверка
                    // по списку исполнений, реально существующих в каталоге СП.
                    string match = KnownExecutions()
                        .FirstOrDefault(e => string.Equals(e, file.Execution.Trim(),
                                                           StringComparison.OrdinalIgnoreCase));
                    if (match == null)
                    {
                        Logger.Warn(
                            $"[Узлы] исполнение «{file.Execution}» в каталоге СП 230 не найдено — " +
                            $"пропущено. Допустимые: {string.Join(", ", KnownExecutions())}");
                    }
                    else
                    {
                        result.Execution = match;
                        applied.Add($"исполнение «{match}»");
                    }
                }

                source = path + (string.IsNullOrWhiteSpace(file.Source) ? "" : $" (источник: {file.Source})");

                Logger.Info($"[Узлы] прочитано из {path}: " +
                            (applied.Count > 0 ? string.Join(", ", applied) : "ничего не задано"));
            }
            catch (Exception ex)
            {
                Logger.Warn($"[Узлы] не удалось прочитать {path} — применяются умолчания", ex);
            }

            return result;
        }

        /// <summary>
        /// Воздухообмен, заданный ДЛЯ ЭТОГО ОБЪЕКТА. Возвращает false, если файла
        /// нет или метод в нём не указан — тогда остаётся умолчание расчёта
        /// (квартирная норма по ТЗ).
        ///
        /// <para>Живёт в том же файле, что и узлы, и по той же причине: методики
        /// воздухообмена у заказчиков и смежников расходятся, а объекты у нас
        /// разные. На 76-СУЗДАЛ.23 разница между квартирной нормой ТЗ и расчётом
        /// смежника дала 73% всего расхождения по этажу.</para>
        /// </summary>
        public static bool TryLoadVentilation(string modelPath, out VentilationMethod method,
                                              out double airChangeRatePerHour, out string note)
        {
            method = VentilationMethod.ApartmentNorm;
            airChangeRatePerHour = 0;
            note = null;

            string path = ExistingPath(modelPath);
            if (path == null) return false;

            try
            {
                ThermalNodeFile file;
                using (var stream = File.OpenRead(path))
                {
                    var serializer = new DataContractJsonSerializer(typeof(ThermalNodeFile));
                    file = serializer.ReadObject(stream) as ThermalNodeFile;
                }

                if (file == null || string.IsNullOrWhiteSpace(file.VentilationMethod)) return false;

                VentilationMethod parsed;
                if (!Enum.TryParse(file.VentilationMethod.Trim(), true, out parsed) ||
                    !Enum.IsDefined(typeof(VentilationMethod), parsed))
                {
                    // Опечатку принимать нельзя ровно по той же причине, что
                    // и в исполнении узла: инженер будет считать, что задал метод.
                    Logger.Warn(
                        $"[Воздухообмен] метод «{file.VentilationMethod}» не распознан — " +
                        $"остаётся умолчание. Допустимые: {string.Join(", ", Enum.GetNames(typeof(VentilationMethod)))}");
                    return false;
                }

                method = parsed;
                airChangeRatePerHour = file.AirChangeRatePerHour ?? 0;

                note = method == VentilationMethod.AirChangeRate
                    ? $"по кратности n = {airChangeRatePerHour:F2} 1/ч (файл объекта)"
                    : (method == VentilationMethod.PerRoomNorm
                        ? "покомнатно по нормам (файл объекта)"
                        : "на квартиру по ТЗ (файл объекта)");

                Logger.Info($"[Воздухообмен] {note}, источник: {path}");
                return true;
            }
            catch (Exception ex)
            {
                Logger.Warn($"[Воздухообмен] не удалось прочитать {path} — остаётся умолчание", ex);
                return false;
            }
        }

        /// <summary>
        /// Исполнения узлов, которые реально есть в каталоге СП 230: только они
        /// имеют смысл в файле настроек. Служебные «Default», «Convex», «Concave»
        /// в список не идут — их выбирает не инженер, а сам узел.
        /// </summary>
        public static IReadOnlyList<string> KnownExecutions()
        {
            var service = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { "Default", "Convex", "Concave" };

            return SP230Catalog.Tables
                .Select(t => t.Variant)
                .Where(v => !string.IsNullOrWhiteSpace(v) && !service.Contains(v))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(v => v, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        /// <summary>
        /// Создаёт шаблон для этой модели, если его ещё нет, и возвращает путь.
        /// Существующий файл НЕ трогается — в нём уже может быть работа инженера.
        /// Папка модели недоступна для записи — шаблон уходит в профиль,
        /// но всё равно ПО ИМЕНИ МОДЕЛИ.
        /// </summary>
        public static string SaveTemplate(string modelPath, BridgeSelectors defaults)
        {
            string existing = ExistingPath(modelPath);
            if (existing != null) return existing;

            var file = BuildTemplate(modelPath, defaults);

            string beside = ModelSidePath(modelPath);
            if (!string.IsNullOrEmpty(beside) && TryWrite(beside, file)) return beside;

            string profile = ProfilePath(modelPath);
            return TryWrite(profile, file) ? profile : null;
        }

        private static ThermalNodeFile BuildTemplate(string modelPath, BridgeSelectors d)
        {
            d = d ?? new BridgeSelectors();
            return new ThermalNodeFile
            {
                Description =
                    "Исполнение узлов фасада ДЛЯ ЭТОГО ОБЪЕКТА. Из модели Revit эти признаки " +
                    "не вытаскиваются — это чертёж узла, а не геометрия здания. " +
                    "Пока поле пустое (null), из равных по числовым признакам таблиц СП 230 " +
                    "берётся ХУДШАЯ по потерям — оценка в запас. Заполняйте по своему проекту.",
                Model = string.IsNullOrWhiteSpace(modelPath) ? "" : Path.GetFileName(modelPath),
                Execution = null,
                ExecutionHelp =
                    "Исполнение узла, одно из: " + string.Join(", ", KnownExecutions()) + ". " +
                    "Для оконного узла СФТК: FrameAtInsulation — рама сразу за утеплителем (Г.33), " +
                    "FrameShiftedIntoInsulation — рама сдвинута в утеплитель (Г.34), " +
                    "FrameShiftedFromInsulation — рама сдвинута от утеплителя (Г.35, по примечанию " +
                    "СП худший вариант). Для плиты перекрытия: Perforated — обычная перфорация, " +
                    "ThermalInsert — несущие теплоизоляционные элементы (НТЭ).",
                FrameMm = d.FrameMm,
                FrameHelp = "Толщина оконной рамы, мм. В таблицах СП: 60, 80, 120. " +
                            "Рама 60 мм и тоньше без дополнительного утепления узла по СП недопустима.",
                NotchMm = d.NotchMm,
                NotchHelp = "Зуб (четверть) при установке окна, мм: 0 или 60.",
                OverlapMm = d.OverlapMm,
                OverlapHelp = "Нахлёст утеплителя на оконную раму, мм: 0, 20 или 60. " +
                              "Самый дешёвый способ улучшить узел: на СФТК нахлёст 20 мм " +
                              "снижает Ψ откоса примерно вдвое.",
                SlabInsulationR = d.SlabInsulationR,
                SlabInsulationHelp = "Термическое сопротивление утеплителя на плите перекрытия, " +
                                     "м²·°С/Вт. В таблицах СП: 1,88, 3,13, 5,0, 7,81.",
                SlabThicknessMm = d.SlabThicknessMm,
                SlabThicknessHelp = "Эффективная толщина плиты перекрытия, мм: 160 или 210.",
                SlabPerforationRatio = d.SlabPerforationRatio,
                SlabPerforationHelp = "Перфорация плиты — отношение a/b: 0, 1, 3 или 5. " +
                                      "СП называет узлы без перфорации недопустимыми " +
                                      "в современных конструкциях, 3/1 — типовым.",
                ParapetInsulationMm = d.ParapetInsulationMm,
                ParapetInsulationHelp = "Высота дополнительного утепления парапета, мм: 0, 200 или 500.",
                Source = "",
                SourceHelp = "Откуда взяты значения: лист АР, узел из альбома, ответ проектировщика. " +
                             "Без него отчёт нельзя подавать как проектный.",

                VentilationMethod = "ApartmentNorm",
                VentilationHelp =
                    "Как считать воздухообмен, одно из: ApartmentNorm — по ТЗ, НА КВАРТИРУ, " +
                    "L = max(Σ приток по жилым 3 м³/(ч·м²), Σ вытяжка кухни и санузлов), " +
                    "нагрузка идёт в комнаты с окнами (умолчание); PerRoomNorm — покомнатно, " +
                    "каждое помещение по своей норме; AirChangeRate — по кратности, L = n · V. " +
                    "Разные заказчики и смежники считают по-разному, и разница велика: " +
                    "на 76-СУЗДАЛ.23 кухни по ТЗ дали на 24% меньше, чем у смежника.",
                AirChangeRatePerHour = null,
                AirChangeHelp =
                    "Кратность n, 1/ч — нужна ТОЛЬКО при AirChangeRate. Умолчания нет намеренно: " +
                    "норматив нормирует расход, а не кратность, поэтому число берётся из проекта. " +
                    "Не задано при выбранной кратности — расчёт откатится к квартирной норме " +
                    "и скажет об этом в журнале.",

                AnchorsPerM2 = d.AnchorsPerM2,
                AnchorsHelp =
                    "Тарельчатых анкеров фасадной системы, шт/м². СП 230 таблица Г.4 считает " +
                    "их ТОЧЕЧНЫМ элементом — потери на штуку, и в Ψ угла они не входят. " +
                    "Умолчания нет намеренно: у СФТК и вентфасада раскладка разная, а в угловых " +
                    "зонах здания она вдвое плотнее середины. Пусто — анкеры НЕ СЧИТАЮТСЯ, " +
                    "теплопотери на этом занижены, и прогон это скажет. Цена вопроса: " +
                    "6 шт/м² с χ = 0,004 дают 0,024 Вт/(м²·К), около 8% к U = 0,316.",
                AnchorL1Mm = d.AnchorL1Mm,
                AnchorL1Help =
                    "L1 — расстояние от края стального распорного элемента анкера до тарелки " +
                    "дюбеля, мм (ось таблицы Г.4). Ступени СП: ≤2 → χ 0,006; ≤6 → 0,005; " +
                    "≤11 → 0,004; ≤16 → 0,003; ≤24 → 0,0025; ≤40 → 0,002; ≤70 → 0,0015; " +
                    "далее 0,001. Интерполяции между ступенями СП не даёт. Не задано при " +
                    "заданной плотности — берётся худший случай, χ = 0,006."
            };
        }

        private static bool TryWrite(string path, ThermalNodeFile file)
        {
            try
            {
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                var serializer = new DataContractJsonSerializer(typeof(ThermalNodeFile));
                using (var memory = new MemoryStream())
                {
                    serializer.WriteObject(memory, file);
                    // UTF-8 без BOM: DataContractJsonSerializer не читает файл с BOM.
                    File.WriteAllText(path, Encoding.UTF8.GetString(memory.ToArray()),
                                      new UTF8Encoding(false));
                }

                Logger.Info($"[Узлы] шаблон создан: {path}");
                return true;
            }
            catch (Exception ex)
            {
                Logger.Debug($"[Узлы] не удалось записать {path}: {ex.Message}");
                return false;
            }
        }

        private static BridgeSelectors Clone(BridgeSelectors s) => new BridgeSelectors
        {
            FrameMm = s.FrameMm,
            NotchMm = s.NotchMm,
            OverlapMm = s.OverlapMm,
            SlabInsulationR = s.SlabInsulationR,
            SlabThicknessMm = s.SlabThicknessMm,
            SlabPerforationRatio = s.SlabPerforationRatio,
            BaseThicknessMm = s.BaseThicknessMm,
            ParapetInsulationMm = s.ParapetInsulationMm,
            WallInsulationR = s.WallInsulationR,
            Execution = s.Execution
        };
    }

    /// <summary>
    /// Файл настроек узлов. Поля *Help существуют затем, чтобы инженеру не
    /// приходилось искать допустимые значения в документации: шаблон объясняет
    /// себя сам, как это уже сделано в <c>wall_types.json</c>.
    /// </summary>
    [DataContract]
    public class ThermalNodeFile
    {
        [DataMember(Order = 1)]  public string Description { get; set; }
        [DataMember(Order = 2)]  public string Model { get; set; }

        [DataMember(Order = 3)]  public string Execution { get; set; }
        [DataMember(Order = 4)]  public string ExecutionHelp { get; set; }

        [DataMember(Order = 5)]  public double? FrameMm { get; set; }
        [DataMember(Order = 6)]  public string FrameHelp { get; set; }

        [DataMember(Order = 7)]  public double? NotchMm { get; set; }
        [DataMember(Order = 8)]  public string NotchHelp { get; set; }

        [DataMember(Order = 9)]  public double? OverlapMm { get; set; }
        [DataMember(Order = 10)] public string OverlapHelp { get; set; }

        [DataMember(Order = 11)] public double? SlabInsulationR { get; set; }
        [DataMember(Order = 12)] public string SlabInsulationHelp { get; set; }

        [DataMember(Order = 13)] public double? SlabThicknessMm { get; set; }
        [DataMember(Order = 14)] public string SlabThicknessHelp { get; set; }

        [DataMember(Order = 15)] public double? SlabPerforationRatio { get; set; }
        [DataMember(Order = 16)] public string SlabPerforationHelp { get; set; }

        [DataMember(Order = 17)] public double? ParapetInsulationMm { get; set; }
        [DataMember(Order = 18)] public string ParapetInsulationHelp { get; set; }

        [DataMember(Order = 19)] public string Source { get; set; }
        [DataMember(Order = 20)] public string SourceHelp { get; set; }

        // ⚠ Новые поля дописываются ТОЛЬКО В КОНЕЦ: DataContractJsonSerializer
        // читает по порядку, и вставка в середину ломает чтение старых файлов.
        [DataMember(Order = 21)] public string VentilationMethod { get; set; }
        [DataMember(Order = 22)] public string VentilationHelp { get; set; }
        [DataMember(Order = 23)] public double? AirChangeRatePerHour { get; set; }
        [DataMember(Order = 24)] public string AirChangeHelp { get; set; }

        // Новые поля дописываются ТОЛЬКО В КОНЕЦ: DataContractJsonSerializer читает
        // по порядку, и вставка в середину сломала бы уже заполненные файлы объектов.
        [DataMember(Order = 25)] public double? AnchorsPerM2 { get; set; }
        [DataMember(Order = 26)] public string AnchorsHelp { get; set; }
        [DataMember(Order = 27)] public double? AnchorL1Mm { get; set; }
        [DataMember(Order = 28)] public string AnchorL1Help { get; set; }
    }
}
