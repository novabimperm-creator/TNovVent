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
    /// Сопротивления теплопередаче, заданные инженером ПО ТИПАМ конструкций.
    ///
    /// <para><b>Зачем.</b> Модель не обязана содержать теплотехнику, и обычно
    /// не содержит: на 76-СУЗДАЛ.23 материалы без теплопроводности давали 99%
    /// площади ограждений «по оценке». Подставлять в этом случае типовое U —
    /// значит выдавать за расчёт число, не имеющее отношения к дому. Правильный
    /// выход не «угадать точнее», а дать инженеру вписать проектное значение
    /// из раздела АР — один раз на тип конструкции, а не на каждую стену.</para>
    ///
    /// <para>Приоритет в <see cref="WallThermalCalculator"/>: слои модели →
    /// аналитическое R → параметр стены → <b>этот файл</b> → оценка по имени.
    /// То есть данные модели остаются главнее ручного ввода, а ручной ввод
    /// главнее умолчания.</para>
    ///
    /// <para>Файл: <c>%APPDATA%\QOVETER\wall_types.json</c>. У каждой записи
    /// есть <c>Source</c> — откуда взято число (лист АР, теплотехнический расчёт,
    /// таблица СП). Пустой <c>Source</c> — повод не подавать отчёт как проектный,
    /// та же логика, что у <c>CityData.IsVerified</c> и у каталога Ψ.</para>
    /// </summary>
    public static class WallTypeOverrides
    {
        private static Dictionary<string, WallTypeOverride> _cache;

        /// <summary>Типы, о которых уже предупреждали — чтобы не повторяться на каждую стену.</summary>
        private static readonly HashSet<string> _warnedTypes = new HashSet<string>();

        public static string FilePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "QOVETER", "wall_types.json");

        /// <summary>Сбрасывает кэш — файл перечитается при следующем обращении.</summary>
        public static void Reload()
        {
            _cache = null;
            _warnedTypes.Clear();
        }

        /// <summary>
        /// R типа конструкции, м²·К/Вт, вместе с сопротивлениями теплоотдаче.
        /// Возвращает false, если для этого типа ничего не задано.
        /// </summary>
        public static bool TryGet(string typeName, out double rValue, out string source)
        {
            rValue = 0;
            source = null;
            if (string.IsNullOrWhiteSpace(typeName)) return false;

            var table = Load();
            WallTypeOverride entry;
            if (!table.TryGetValue(Key(typeName), out entry)) return false;

            // R = 0 — это НЕЗАПОЛНЕННАЯ строка шаблона, а не ошибка: шаблон
            // сам создаётся с нулями и сам про это пишет («Записи с R = 0
            // плагином игнорируются»). Предупреждать тут не о чем, и молчать
            // обязательно: строка читается на КАЖДУЮ стену, и за прогон
            // 2026-08-13 12 незаполненных типов дали 13 101 запись Warn
            // в журнале. Каждая из них вдобавок сбрасывает буфер логгера.
            if (entry.R == 0) return false;

            // Диапазон-сторож: R вне 0,1…20 — это не сопротивление, а описка
            // либо перепутанные единицы. Молча принять такое нельзя, оно
            // масштабируется на всю площадь типа. А вот повторять предупреждение
            // на каждую стену незачем — тип называется один раз.
            if (!IsPlausibleR(entry.R))
            {
                if (_warnedTypes.Add(Key(typeName)))
                {
                    Logger.Warn($"[Типы] «{typeName}»: R = {entry.R} вне диапазона 0,1…20 м²·К/Вт — запись игнорируется");
                }
                return false;
            }

            rValue = entry.R;
            source = string.IsNullOrWhiteSpace(entry.Source) ? "ручной ввод без указания источника" : entry.Source;
            return true;
        }

        /// <summary>
        /// R похоже на сопротивление ограждения, а не на описку или другие единицы.
        ///
        /// Нижняя граница 0,1 отсекает нули и «U вместо R»; верхняя 20 — потерянную
        /// запятую и внутренние единицы Revit, где R больше значения в СИ
        /// в 5,678 раза. Ошибка в этом файле масштабируется на ВСЮ площадь типа,
        /// поэтому пропустить её молча нельзя.
        /// </summary>
        public static bool IsPlausibleR(double r)
        {
            return r >= 0.1 && r <= 20;
        }

        private static Dictionary<string, WallTypeOverride> Load()
        {
            if (_cache != null) return _cache;

            _cache = new Dictionary<string, WallTypeOverride>();
            try
            {
                if (!File.Exists(FilePath)) return _cache;

                using (var stream = File.OpenRead(FilePath))
                {
                    var serializer = new DataContractJsonSerializer(typeof(WallTypeOverrideFile));
                    var file = serializer.ReadObject(stream) as WallTypeOverrideFile;
                    foreach (var entry in file?.Types ?? new List<WallTypeOverride>())
                    {
                        if (string.IsNullOrWhiteSpace(entry.TypeName)) continue;
                        _cache[Key(entry.TypeName)] = entry;
                    }
                }

                Logger.Info($"[Типы] прочитано записей из {FilePath}: {_cache.Count}");
            }
            catch (Exception ex)
            {
                Logger.Warn($"[Типы] не удалось прочитать {FilePath}", ex);
            }

            return _cache;
        }

        /// <summary>
        /// Пишет шаблон со списком типов, у которых теплотехники не нашлось.
        /// Существующий файл НЕ трогается: в нём уже может быть работа инженера.
        /// </summary>
        public static string SaveTemplate(IEnumerable<KeyValuePair<string, double>> typesWithArea)
        {
            try
            {
                if (File.Exists(FilePath)) return FilePath;

                var dir = Path.GetDirectoryName(FilePath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                var file = new WallTypeOverrideFile
                {
                    Description =
                        "R конструкций по типам, м²·К/Вт, ВМЕСТЕ с Rsi+Rse. " +
                        "Заполняется, когда в модели не заданы теплопроводности материалов. " +
                        "Source — откуда взято число (лист АР, теплотехнический расчёт, таблица СП): " +
                        "без него отчёт нельзя подавать как проектный. " +
                        "Записи с R = 0 плагином игнорируются.",
                    Types = typesWithArea
                        .OrderByDescending(t => t.Value)
                        .Select(t => new WallTypeOverride
                        {
                            TypeName = t.Key,
                            R = 0,
                            Source = "",
                            Comment = $"площадь в расчёте: {t.Value:F0} м²"
                        })
                        .ToList()
                };

                using (var stream = File.Create(FilePath))
                {
                    var serializer = new DataContractJsonSerializer(typeof(WallTypeOverrideFile));
                    serializer.WriteObject(stream, file);
                }

                Logger.Info($"[Типы] шаблон создан: {FilePath} ({file.Types.Count} типов)");
                return FilePath;
            }
            catch (Exception ex)
            {
                Logger.Warn("[Типы] не удалось записать шаблон", ex);
                return null;
            }
        }

        private static string Key(string typeName) => typeName.Trim().ToLowerInvariant();
    }

    [DataContract]
    public class WallTypeOverrideFile
    {
        [DataMember] public string Description { get; set; }
        [DataMember] public List<WallTypeOverride> Types { get; set; } = new List<WallTypeOverride>();
    }

    [DataContract]
    public class WallTypeOverride
    {
        /// <summary>Имя типа конструкции в модели, как в свойствах стены.</summary>
        [DataMember] public string TypeName { get; set; }

        /// <summary>Сопротивление теплопередаче, м²·К/Вт, включая Rsi+Rse.</summary>
        [DataMember] public double R { get; set; }

        /// <summary>Откуда взято значение: лист АР, расчёт, таблица норматива.</summary>
        [DataMember] public string Source { get; set; }

        [DataMember] public string Comment { get; set; }
    }
}
