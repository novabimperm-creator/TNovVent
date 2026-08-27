using System;
using System.Collections.Generic;
using System.Linq;
using QOVETER.Models;

namespace QOVETER.Services
{
    /// <summary>
    /// Состав отчёта БЕЗ оформления — те же листы и те же числа, что в книге
    /// <see cref="ExcelExportService"/>, но выраженные простой таблицей ячеек.
    ///
    /// <para><b>Зачем отдельно.</b> Книгу собирает ClosedXML, и когда его нет
    /// рядом с плагином, отчёт терялся целиком. Здесь состав отчёта отделён
    /// от способа записи: те же строки уходят и в <see cref="SimpleXlsxWriter"/>
    /// (запасная книга без единой сторонней сборки), и — при нужде — куда угодно ещё.</para>
    ///
    /// <para><b>Чего здесь нет:</b> заливок, рамок, примечаний к ячейкам и
    /// выделения спорных значений цветом. Всё это несёт смысл, поэтому запасная
    /// книга проговаривает те же предупреждения ТЕКСТОМ, отдельными строками
    /// листа «Параметры»: инженер должен прочитать их и в аварийном отчёте.</para>
    /// </summary>
    public static class PlainReportBuilder
    {
        public static List<SheetData> Build(List<CalculationResult> results, ExcelExportParams p)
        {
            if (results == null) throw new ArgumentNullException(nameof(results));

            var rooms   = results.Where(r => !r.IsSummary && string.IsNullOrEmpty(r.ErrorMessage)).ToList();
            var summary = results.FirstOrDefault(r => r.IsSummary);
            var errors  = results.Where(r => !string.IsNullOrEmpty(r.ErrorMessage)).ToList();

            var sheets = new List<SheetData>
            {
                Parameters(p, rooms, summary),
                Rooms(rooms),
                Apartments(rooms),
                Floors(rooms)
            };

            if (errors.Count > 0) sheets.Add(Errors(errors));

            return sheets;
        }

        // ─────────────────────────────────────────────────────────────
        //  Лист «Параметры» — декларация допущений, а не оглавление
        // ─────────────────────────────────────────────────────────────

        private static SheetData Parameters(ExcelExportParams p, List<CalculationResult> rooms,
                                            CalculationResult summary)
        {
            var sheet = new SheetData("Параметры");

            sheet.Add("QOVETER — расчёт теплопотерь").Bold();
            sheet.Add("Книга собрана запасным путём, без библиотек оформления: " +
                      "числа те же, оформления нет");
            sheet.Blank();

            sheet.Add("Исходные данные").Bold();
            sheet.Add("Дата расчёта", DateTime.Now.ToString("yyyy-MM-dd HH:mm"));
            if (p != null)
            {
                sheet.Add("Город", p.City ?? "");
                sheet.Add("t внутр, °C", p.InternalTemp);
                sheet.Add("t наруж, °C", p.ExternalTemp);
            }
            sheet.Add("Помещений в расчёте", rooms.Count);
            if (summary != null)
            {
                sheet.Add("Q итого, Вт", summary.Q_total);
                sheet.Add("Q с запасом, Вт", summary.Q_final);
            }
            sheet.Add("Журнал расчёта", Logger.LogFilePath);
            sheet.Blank();

            if (p != null)
            {
                sheet.Add("Чем посчитаны ограждения").Bold();
                sheet.Add("По данным модели, м²", Round(p.WallAreaFromModelM2));
                sheet.Add("По таблице норматива (СП 50 прил. Т), м²", Round(p.WallAreaNormativeM2));
                sheet.Add("По типовому U — материал не распознан, м²", Round(p.WallAreaEstimatedM2));

                double total = p.WallAreaFromModelM2 + p.WallAreaNormativeM2 + p.WallAreaEstimatedM2;
                if (total > 0)
                    sheet.Add("Доля по данным модели, %", Round(p.WallAreaFromModelM2 / total * 100, 1));

                if (!string.IsNullOrWhiteSpace(p.WallTypesNormative))
                    sheet.Add("Конструкции по нормативной λ", p.WallTypesNormative);
                if (!string.IsNullOrWhiteSpace(p.WallTypesWithoutData))
                    sheet.Add("Конструкции без теплотехники", p.WallTypesWithoutData);
                sheet.Blank();

                sheet.Add("Принятые решения").Bold();
                if (!string.IsNullOrWhiteSpace(p.VentilationSource))
                    sheet.Add("Воздухообмен", p.VentilationSource);
                if (!string.IsNullOrWhiteSpace(p.NodeSettingsSource))
                    sheet.Add("Исполнение узлов фасада", p.NodeSettingsSource);
                if (p.ShaftAreaM2 > 0)
                    sheet.Add("Ограждения в шахты, м²", Round(p.ShaftAreaM2),
                              p.ShaftTemperature.HasValue
                                  ? $"принято {p.ShaftTemperature.Value:0.#} °C"
                                  : "принят наружный воздух");
                if (p.GroundFloorAreaM2 > 0)
                    sheet.Add("Пол по грунту (зональный метод), м²", Round(p.GroundFloorAreaM2));
                if (p.GroundWallAreaM2 > 0)
                    sheet.Add("Стены в грунте (зональный метод), м²", Round(p.GroundWallAreaM2));
                sheet.Blank();

                if (p.UnheatedTemperatures != null && p.UnheatedTemperatures.Count > 0)
                {
                    var byBalance = p.UnheatedTemperatures.Where(t => t.FromBalance).ToList();
                    var byTable   = p.UnheatedTemperatures.Where(t => !t.FromBalance).ToList();

                    sheet.Add("Температуры неотапливаемых объёмов (СП 50.13330 п. 5.2)").Bold();
                    sheet.Add("Посчитано балансом", byBalance.Count);
                    if (byBalance.Count > 0)
                        sheet.Add("Диапазон, °C",
                                  Round(byBalance.Min(t => t.Temperature), 1),
                                  Round(byBalance.Max(t => t.Temperature), 1));
                    sheet.Add("Принято из таблицы", byTable.Count);
                    sheet.Add("Воздухообмен самих объёмов в баланс не входит — " +
                              "температура завышена, а потери через такие ограждения занижены");
                    sheet.Blank();
                }
            }

            // Мостики: те же три числа, которыми сводка прогона отвечает на вопрос
            // «посчиталось ли приведение на самом деле».
            var withNodes = rooms.Where(r => r.BridgeNodesCounted > 0 || r.BridgeNodesSkipped > 0).ToList();
            if (withNodes.Count > 0)
            {
                sheet.Add("Мостики холода (СП 230.1325800.2015)").Bold();
                sheet.Add("Помещений с узлами", withNodes.Count);
                sheet.Add("Узлов учтено", withNodes.Sum(r => r.BridgeNodesCounted));
                sheet.Add("Узлов пропущено (таблицы СП нет)", withNodes.Sum(r => r.BridgeNodesSkipped));

                var withR = rooms.Where(r => r.Homogeneity > 0 && r.Homogeneity < 1).ToList();
                if (withR.Count > 0)
                    sheet.Add("Средний r по расчёту", Round(withR.Average(r => r.Homogeneity), 3));

                int assumed = rooms.Count(r => r.IsWallConstructionAssumed);
                if (assumed > 0)
                    sheet.Add("Помещений, где конструкция ПРИНЯТА, а не прочитана", assumed);

                if (rooms.Any(r => r.IsReducedProvisional))
                    sheet.Add("ВНИМАНИЕ: часть Ψ не сверена с текстом СП — " +
                              "результат нельзя подавать как нормативный").Bold();
                sheet.Blank();
            }

            return sheet;
        }

        // ─────────────────────────────────────────────────────────────
        //  Лист «По помещениям» — те же колонки, что в книге и в CSV
        // ─────────────────────────────────────────────────────────────

        internal static readonly string[] RoomHeaders =
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

        /// <summary>Одна строка помещения — общая для книги и для CSV.</summary>
        internal static object[] RoomRow(CalculationResult r, int index)
        {
            double unheatedArea = r.RoomData?.Walls?
                .Where(w => w.IsExternal && w.AdjacentCategory.HasValue)
                .Sum(w => w.Area) ?? 0;
            double betaSum = r.BetaCoefficients?.Values.Sum() ?? 0;

            return new object[]
            {
                index,
                r.RoomData?.Number ?? "",
                r.RoomData?.Name ?? r.RoomName,
                string.IsNullOrEmpty(r.Apartment) ? "—" : r.Apartment,
                r.FloorNumber,
                r.RoomData?.Type ?? "",
                r.Orientation ?? "",
                Round(r.Area), Round(r.WallArea), Round(unheatedArea),
                Round(r.WindowArea), Round(r.ExtWindowsArea),
                Round(r.Q_ogr), Round(r.Q_walls), Round(r.Q_windows), Round(r.Q_doors),
                Round(r.Q_floor), Round(r.Q_roof),
                Round(r.Q_vent), Round(r.Q_vn),
                Round(r.Q_total), Round(r.Q_final),
                Round(r.Area > 0 ? r.Q_final / r.Area : 0),
                Round(betaSum, 3),
                Round(r.R_conditional, 3), Round(r.R_reduced, 3), Round(r.Homogeneity, 3)
            };
        }

        private static SheetData Rooms(List<CalculationResult> rooms)
        {
            var sheet = new SheetData("По помещениям") { BoldRows = 1 };
            sheet.Add(RoomHeaders.Cast<object>().ToArray());

            int index = 1;
            foreach (var r in rooms) sheet.Add(RoomRow(r, index++));

            if (rooms.Count > 0)
            {
                sheet.Add("ИТОГО", "", "", "", "", "", "",
                          Round(rooms.Sum(r => r.Area)),
                          Round(rooms.Sum(r => r.WallArea)), null,
                          Round(rooms.Sum(r => r.WindowArea)),
                          Round(rooms.Sum(r => r.ExtWindowsArea)),
                          Round(rooms.Sum(r => r.Q_ogr)), null, null, null, null, null,
                          Round(rooms.Sum(r => r.Q_vent)),
                          Round(rooms.Sum(r => r.Q_vn)),
                          Round(rooms.Sum(r => r.Q_total)),
                          Round(rooms.Sum(r => r.Q_final))).Bold();
            }

            return sheet;
        }

        // ─────────────────────────────────────────────────────────────
        //  Своды
        // ─────────────────────────────────────────────────────────────

        private static SheetData Apartments(List<CalculationResult> rooms)
        {
            var sheet = new SheetData("По квартирам") { BoldRows = 1 };
            sheet.Add("Квартира", "Помещений", "Площадь, м²",
                      "Q огр, Вт", "Q вент, Вт", "Q итого, Вт", "Q с запасом, Вт", "Уд., Вт/м²");

            // Внеквартирные помещения идут ОДНОЙ строкой в конце, а не растворяются
            // среди квартир: это отдельная система отопления.
            var groups = rooms
                .GroupBy(r => string.IsNullOrWhiteSpace(r.Apartment) ? "" : r.Apartment.Trim())
                .OrderBy(g => string.IsNullOrEmpty(g.Key) ? 1 : 0)
                .ThenBy(g => g.Key, StringComparer.CurrentCulture);

            foreach (var g in groups)
            {
                double area = g.Sum(r => r.Area);
                sheet.Add(
                    string.IsNullOrEmpty(g.Key) ? "Вне квартир (общедомовые)" : g.Key,
                    g.Count(), Round(area),
                    Round(g.Sum(r => r.Q_ogr)), Round(g.Sum(r => r.Q_vent)),
                    Round(g.Sum(r => r.Q_total)), Round(g.Sum(r => r.Q_final)),
                    Round(area > 0 ? g.Sum(r => r.Q_final) / area : 0, 1));
            }

            return sheet;
        }

        private static SheetData Floors(List<CalculationResult> rooms)
        {
            var sheet = new SheetData("По этажам") { BoldRows = 1 };
            sheet.Add("Этаж", "Помещений", "Площадь, м²",
                      "Q огр, Вт", "Q вент, Вт", "Q итого, Вт", "Q с запасом, Вт", "Уд., Вт/м²");

            foreach (var g in rooms.GroupBy(r => r.FloorNumber).OrderBy(g => g.Key))
            {
                double area = g.Sum(r => r.Area);
                sheet.Add(g.Key, g.Count(), Round(area),
                          Round(g.Sum(r => r.Q_ogr)), Round(g.Sum(r => r.Q_vent)),
                          Round(g.Sum(r => r.Q_total)), Round(g.Sum(r => r.Q_final)),
                          Round(area > 0 ? g.Sum(r => r.Q_final) / area : 0, 1));
            }

            if (rooms.Count > 0)
            {
                double area = rooms.Sum(r => r.Area);
                sheet.Add("ИТОГО", rooms.Count, Round(area),
                          Round(rooms.Sum(r => r.Q_ogr)), Round(rooms.Sum(r => r.Q_vent)),
                          Round(rooms.Sum(r => r.Q_total)), Round(rooms.Sum(r => r.Q_final)),
                          Round(area > 0 ? rooms.Sum(r => r.Q_final) / area : 0, 1)).Bold();
            }

            return sheet;
        }

        private static SheetData Errors(List<CalculationResult> errors)
        {
            var sheet = new SheetData("Ошибки") { BoldRows = 1 };
            sheet.Add("Помещение", "Ошибка");
            foreach (var e in errors) sheet.Add(e.RoomName, e.ErrorMessage);
            return sheet;
        }

        private static double Round(double value, int digits = 2) => Math.Round(value, digits);
    }
}
