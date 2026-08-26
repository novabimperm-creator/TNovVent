using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using QOVETER.Models;

namespace QOVETER.Services
{
    /// <summary>
    /// Запасной вывод результатов — CSV без единой сторонней сборки.
    ///
    /// <para><b>Зачем понадобился.</b> 2026-08-26 на большом проекте расчёт
    /// отработал, таблица результатов заполнилась — а кнопка «Excel» ответила
    /// «Не удалось загрузить файл или сборку ClosedXML… либо одну из их
    /// зависимостей». Рядом с DLL плагина не оказалось библиотек Excel-экспорта.
    /// Итог: часы работы Revit, готовые числа на экране и никакой возможности
    /// их сохранить.</para>
    ///
    /// <para><b>Что делает.</b> Тот же лист «По помещениям», что и в книге Excel:
    /// те же колонки в том же порядке. Ни оформления, ни листов «Параметры»
    /// и «По квартирам» — это аварийный выход, а не замена отчёта.</para>
    ///
    /// <para><b>Почему точка с запятой и BOM.</b> Excel в русской локали читает
    /// разделителем списка «;», а UTF-8 без BOM открывает как ANSI и показывает
    /// кракозябры. Файл должен открываться двойным щелчком, без мастера импорта:
    /// инженеру в этот момент уже не до настроек.</para>
    /// </summary>
    public class CsvExportService
    {
        /// <summary>Разделитель полей: Excel в русской локали ждёт именно его.</summary>
        private const char Separator = ';';

        /// <summary>Числа пишутся с запятой — как их читает Excel в той же локали.</summary>
        private static readonly CultureInfo Ru = new CultureInfo("ru-RU");

        /// <summary>
        /// Пишет результаты в CSV. Возвращает число строк помещений.
        /// </summary>
        public int Export(List<CalculationResult> results, string path, ExcelExportParams parameters)
        {
            if (results == null) throw new ArgumentNullException(nameof(results));

            var text = new StringBuilder();

            // Шапка: без неё непонятно, при каких условиях считано.
            text.AppendLine(Join("QOVETER — расчёт теплопотерь (аварийный вывод в CSV)"));
            text.AppendLine(Join("Дата", DateTime.Now.ToString("yyyy-MM-dd HH:mm", Ru)));
            if (parameters != null)
            {
                text.AppendLine(Join("Город", parameters.City));
                text.AppendLine(Join("t внутр, °C", Num(parameters.InternalTemp)));
                text.AppendLine(Join("t наруж, °C", Num(parameters.ExternalTemp)));
            }
            text.AppendLine(Join("Журнал расчёта", Logger.LogFilePath));
            text.AppendLine();

            string[] headers =
            {
                "№", "Номер", "Помещение", "Кв.", "Этаж", "Тип", "Ориентация",
                "Площадь, м²", "Стены, м²", "в т.ч. к неотапл., м²",
                "Окна вычтено, м²", "Окна всего, м²",
                "Q огр, Вт", "в т.ч. стены", "в т.ч. окна", "в т.ч. двери",
                "в т.ч. пол", "в т.ч. кровля",
                "Q вент, Вт", "Q вн, Вт",
                "Q итого, Вт", "Q с запасом, Вт", "Уд., Вт/м²",
                "Σβ", "R усл, м²·К/Вт", "R пр, м²·К/Вт", "r"
            };
            text.AppendLine(string.Join(Separator.ToString(), headers.Select(Escape)));

            int index = 1;
            foreach (var r in results)
            {
                double unheatedArea = r.RoomData?.Walls?
                    .Where(w => w.IsExternal && w.AdjacentCategory.HasValue)
                    .Sum(w => w.Area) ?? 0;
                double betaSum = r.BetaCoefficients?.Values.Sum() ?? 0;

                var cells = new[]
                {
                    index.ToString(Ru),
                    r.RoomData?.Number ?? "",
                    r.RoomData?.Name ?? r.RoomName,
                    string.IsNullOrEmpty(r.Apartment) ? "—" : r.Apartment,
                    r.FloorNumber.ToString(Ru),
                    r.RoomData?.Type ?? "",
                    r.Orientation ?? "",
                    Num(r.Area), Num(r.WallArea), Num(unheatedArea),
                    Num(r.WindowArea), Num(r.ExtWindowsArea),
                    Num(r.Q_ogr), Num(r.Q_walls), Num(r.Q_windows), Num(r.Q_doors),
                    Num(r.Q_floor), Num(r.Q_roof),
                    Num(r.Q_vent), Num(r.Q_vn),
                    Num(r.Q_total), Num(r.Q_final),
                    Num(r.Area > 0 ? r.Q_final / r.Area : 0),
                    Num(betaSum, 3),
                    Num(r.R_conditional), Num(r.R_reduced), Num(r.Homogeneity, 3)
                };

                text.AppendLine(string.Join(Separator.ToString(), cells.Select(Escape)));
                index++;
            }

            // BOM обязателен: без него Excel читает файл как ANSI.
            File.WriteAllText(path, text.ToString(), new UTF8Encoding(true));

            Logger.Info($"Аварийный вывод в CSV: {results.Count} строк, файл {path}");
            return results.Count;
        }

        private static string Num(double value, int digits = 2) =>
            Math.Round(value, digits).ToString(Ru);

        private static string Join(params string[] cells) =>
            string.Join(Separator.ToString(), cells.Select(Escape));

        /// <summary>
        /// Имена помещений в моделях содержат и точку с запятой, и кавычки,
        /// и переносы строк. Без экранирования такая строка разъезжается
        /// по колонкам, а отчёт молча теряет смысл.
        /// </summary>
        private static string Escape(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";

            bool needsQuotes = value.IndexOf(Separator) >= 0 ||
                               value.IndexOf('"') >= 0 ||
                               value.IndexOf('\n') >= 0 ||
                               value.IndexOf('\r') >= 0;

            return needsQuotes ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
        }
    }
}
