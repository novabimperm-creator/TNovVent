using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;

namespace QOVETER.Services
{
    /// <summary>Типовой узел с теплотехнической неоднородностью (линейный мостик).</summary>
    public enum ThermalBridgeType
    {
        ExternalCorner = 0,   // Наружный угол стены
        WindowReveal,         // Оконный откос (притвор коробки, четверть)
        DoorReveal,           // Дверной откос
        FloorSlab,            // Примыкание плиты междуэтажного перекрытия
        BalconySlab,          // Балконная плита, пробивающая утеплитель
        Parapet,              // Парапет / карниз (верх стены)
        BaseJunction          // Цокольный узел (низ стены)
    }

    /// <summary>
    /// Каталог линейных коэффициентов теплопередачи Ψ [Вт/(м·К)] для типовых узлов.
    ///
    /// ⚠ ВАЖНО ПРО ИСТОЧНИК ЗНАЧЕНИЙ.
    /// Значения «из коробки» — ПРЕДВАРИТЕЛЬНЫЕ ОЦЕНКИ порядка величины, а НЕ выписка
    /// из СП 230.1325800.2015. Пока значение не сверено с текстом СП, оно помечено
    /// <see cref="ThermalBridgeEntry.IsVerified"/> = false, и расчёт R_пр по нему
    /// маркируется в отчёте как предварительный.
    ///
    /// Чтобы получить нормативные числа: выписать Ψ из СП 230 для своих узлов
    /// в файл <c>%APPDATA%\QOVETER\thermal_bridges.json</c> (шаблон создаётся
    /// методом <see cref="SaveTemplate"/>), указав в поле Source конкретную таблицу.
    /// Загруженные из файла значения считаются выверенными.
    ///
    /// Так сделано намеренно: подписать чужими нормативными номерами значения,
    /// которых нет под рукой, — быстрый способ сделать расчёт незащитимым в экспертизе.
    /// </summary>
    public static class ThermalBridgeCatalog
    {
        /// <summary>Признак того, что значение не сверено с текстом норматива.</summary>
        public const string ProvisionalSource =
            "ПРЕДВАРИТЕЛЬНО, не из СП 230 — сверить с таблицами СП 230.1325800.2015";

        private static readonly Dictionary<ThermalBridgeType, ThermalBridgeEntry> Defaults =
            new Dictionary<ThermalBridgeType, ThermalBridgeEntry>
            {
                { ThermalBridgeType.ExternalCorner, Entry(ThermalBridgeType.ExternalCorner, "Наружный угол",                     0.08) },
                { ThermalBridgeType.WindowReveal,   Entry(ThermalBridgeType.WindowReveal,   "Оконный откос",                     0.06) },
                { ThermalBridgeType.DoorReveal,     Entry(ThermalBridgeType.DoorReveal,     "Дверной откос",                     0.06) },
                { ThermalBridgeType.FloorSlab,      Entry(ThermalBridgeType.FloorSlab,      "Примыкание плиты перекрытия",       0.12) },
                { ThermalBridgeType.BalconySlab,    Entry(ThermalBridgeType.BalconySlab,    "Балконная плита",                   0.35) },
                { ThermalBridgeType.Parapet,        Entry(ThermalBridgeType.Parapet,        "Парапет / карниз",                  0.20) },
                { ThermalBridgeType.BaseJunction,   Entry(ThermalBridgeType.BaseJunction,   "Цокольный узел",                    0.20) }
            };

        private static ThermalBridgeEntry Entry(ThermalBridgeType type, string name, double psi) =>
            new ThermalBridgeEntry
            {
                Type       = type.ToString(),
                Name       = name,
                Psi        = psi,
                Source     = ProvisionalSource,
                IsVerified = false
            };

        /// <summary>Путь к пользовательскому каталогу с выверенными значениями.</summary>
        public static string UserCatalogPath =>
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "QOVETER", "thermal_bridges.json");

        /// <summary>
        /// Каталог по умолчанию — предварительные значения, все не выверены.
        /// </summary>
        public static IReadOnlyDictionary<ThermalBridgeType, ThermalBridgeEntry> Default =>
            Defaults.ToDictionary(kv => kv.Key, kv => Clone(kv.Value));

        /// <summary>
        /// Загружает каталог: пользовательский файл поверх значений по умолчанию.
        /// Значения из файла считаются выверенными — их выписал инженер из СП 230.
        /// Файла нет или он битый — возвращаются значения по умолчанию.
        /// </summary>
        public static IReadOnlyDictionary<ThermalBridgeType, ThermalBridgeEntry> Load(string path = null)
        {
            var catalog = Defaults.ToDictionary(kv => kv.Key, kv => Clone(kv.Value));
            string file = path ?? UserCatalogPath;

            if (!File.Exists(file))
            {
                Logger.Debug($"ThermalBridgeCatalog: пользовательский каталог не найден ({file}), " +
                             "используются предварительные значения");
                return catalog;
            }

            try
            {
                var serializer = new DataContractJsonSerializer(typeof(ThermalBridgeCatalogDto));
                ThermalBridgeCatalogDto dto;
                using (var stream = File.OpenRead(file))
                    dto = (ThermalBridgeCatalogDto)serializer.ReadObject(stream);

                int loaded = 0;
                foreach (var entry in dto?.Entries ?? new List<ThermalBridgeEntry>())
                {
                    ThermalBridgeType type;
                    if (!Enum.TryParse(entry.Type ?? "", out type))
                    {
                        Logger.Debug($"ThermalBridgeCatalog: неизвестный тип узла «{entry.Type}» — пропущен");
                        continue;
                    }
                    // Отрицательное Ψ — НОРМАЛЬНОЕ значение по СП 230, а не ошибка ввода:
                    // вогнутый угол вычитается (раздел Г.4, угол там — чисто
                    // геометрический элемент), а узел плиты с несущими
                    // теплоизоляционными элементами и сильной перфорацией уходит
                    // в минус по таблице Г.10. Раньше такие значения молча
                    // отбрасывались, и инженер, выписавший из СП правильное число,
                    // получал предварительное положительное — с завышенными потерями
                    // и с пометкой «предварительно» в отчёте, которого он не ждал.
                    // Отсекаем только заведомо бессмысленный порядок величины.
                    if (Math.Abs(entry.Psi) > 5.0)
                    {
                        Logger.Warn($"ThermalBridgeCatalog: Ψ = {entry.Psi} для «{entry.Type}» " +
                                    "вне разумного диапазона ±5 Вт/(м·К) — пропущено");
                        continue;
                    }

                    catalog[type] = new ThermalBridgeEntry
                    {
                        Type   = type.ToString(),
                        Name   = string.IsNullOrWhiteSpace(entry.Name) ? Defaults[type].Name : entry.Name,
                        Psi    = entry.Psi,
                        Source = string.IsNullOrWhiteSpace(entry.Source)
                            ? "пользовательский каталог (источник не указан)"
                            : entry.Source,
                        // Выверенным значение считается только при ЯВНОМ IsVerified=true.
                        // Молчаливое «раз из файла — значит выверено» сделало бы
                        // предупреждение в отчёте бесполезным: достаточно было бы
                        // скопировать шаблон, чтобы предварительные Ψ стали
                        // «нормативными». Порядок описан в CLAUDE.md.
                        IsVerified = entry.IsVerified
                    };
                    loaded++;
                }

                Logger.Info($"ThermalBridgeCatalog: загружено {loaded} узлов из {file}");
            }
            catch (Exception ex)
            {
                Logger.Error($"ThermalBridgeCatalog: не удалось прочитать {file} — " +
                             "используются предварительные значения", ex);
            }

            return catalog;
        }

        /// <summary>
        /// Записывает шаблон каталога рядом с настройками, чтобы инженеру осталось
        /// подставить Ψ из СП 230 и указать таблицу в поле Source.
        /// </summary>
        public static void SaveTemplate(string path = null)
        {
            string file = path ?? UserCatalogPath;
            var dir = Path.GetDirectoryName(file);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var dto = new ThermalBridgeCatalogDto
            {
                Comment = "Ψ [Вт/(м·К)] по СП 230.1325800.2015. Подставьте значения для своих " +
                          "узлов и укажите в Source номер таблицы. IsVerified=true означает, " +
                          "что значение сверено с текстом СП и может идти в отчёт как нормативное.",
                Entries = Defaults.Values.Select(Clone).ToList()
            };

            var serializer = new DataContractJsonSerializer(typeof(ThermalBridgeCatalogDto));
            using (var memory = new MemoryStream())
            {
                serializer.WriteObject(memory, dto);
                // UTF-8 без BOM: DataContractJsonSerializer не читает файл с BOM.
                File.WriteAllText(file, Encoding.UTF8.GetString(memory.ToArray()),
                                  new UTF8Encoding(false));
            }
            Logger.Info($"ThermalBridgeCatalog: шаблон записан в {file}");
        }

        private static ThermalBridgeEntry Clone(ThermalBridgeEntry e) => new ThermalBridgeEntry
        {
            Type = e.Type, Name = e.Name, Psi = e.Psi, Source = e.Source, IsVerified = e.IsVerified
        };
    }

    [DataContract]
    public class ThermalBridgeCatalogDto
    {
        [DataMember(Order = 1)] public string Comment { get; set; }
        [DataMember(Order = 2)] public List<ThermalBridgeEntry> Entries { get; set; } = new List<ThermalBridgeEntry>();
    }

    [DataContract]
    public class ThermalBridgeEntry
    {
        /// <summary>Имя значения <see cref="ThermalBridgeType"/>.</summary>
        [DataMember(Order = 1)] public string Type { get; set; }
        [DataMember(Order = 2)] public string Name { get; set; }
        /// <summary>Линейный коэффициент теплопередачи Ψ, Вт/(м·К).</summary>
        [DataMember(Order = 3)] public double Psi { get; set; }
        /// <summary>Откуда значение: таблица СП 230 либо пометка о предварительности.</summary>
        [DataMember(Order = 4)] public string Source { get; set; }
        /// <summary>Значение сверено с текстом норматива и может идти в отчёт как нормативное.</summary>
        [DataMember(Order = 5)] public bool IsVerified { get; set; }
    }
}
