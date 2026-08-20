using ClosedXML.Excel;
using QOVETER.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace QOVETER.Services
{
    /// <summary>
    /// Экспорт результатов расчёта теплопотерь в Excel через ClosedXML.
    /// Структура книги: Параметры → По помещениям → По этажам → Итог.
    /// </summary>
    public class ExcelExportService
    {
        public void ExportToExcel(
            List<CalculationResult> results,
            string filePath,
            ExcelExportParams parameters = null)
        {
            if (results == null || results.Count == 0)
                throw new ArgumentException("Список результатов пуст", nameof(results));
            if (string.IsNullOrWhiteSpace(filePath))
                throw new ArgumentException("Путь к файлу не задан", nameof(filePath));

            parameters = parameters ?? new ExcelExportParams();

            // Разделяем строки по комнатам и итог
            var roomResults = results.Where(r => !r.IsSummary && string.IsNullOrEmpty(r.ErrorMessage)).ToList();
            var errors      = results.Where(r => !string.IsNullOrEmpty(r.ErrorMessage)).ToList();
            var summary     = results.FirstOrDefault(r => r.IsSummary);

            EnsureDirectoryExists(filePath);

            using (var book = new XLWorkbook())
            {
                BuildParametersSheet(book, parameters, roomResults, summary);
                BuildRoomsSheet(book, roomResults);
                BuildApartmentSheet(book, roomResults);
                BuildFloorSummarySheet(book, roomResults);
                BuildTotalSheet(book, summary, roomResults);
                if (errors.Any()) BuildErrorsSheet(book, errors);

                book.SaveAs(filePath);
            }

            Logger.Info($"ExcelExport: отчёт сохранён в {filePath} ({roomResults.Count} строк)");
        }

        private static void BuildParametersSheet(
            IXLWorkbook book,
            ExcelExportParams p,
            List<CalculationResult> rooms,
            CalculationResult summary)
        {
            var ws = book.Worksheets.Add("Параметры");
            ws.Cell("A1").Value = "QOVETER — расчёт теплопотерь";
            ws.Range("A1:B1").Merge().Style
                .Font.SetBold().Font.SetFontSize(14)
                .Fill.SetBackgroundColor(XLColor.LightBlue);

            int row = 3;
            void Put(string label, object value)
            {
                ws.Cell(row, 1).Value = label;
                ws.Cell(row, 2).Value = XLCellValue.FromObject(value);
                ws.Cell(row, 1).Style.Font.SetBold();
                row++;
            }

            Put("Дата формирования",       DateTime.Now.ToString("yyyy-MM-dd HH:mm"));

            // Путь к журналу — В ОТЧЁТЕ, а не «где-то в %TEMP%». 2026-08-06 полдня ушло
            // на поиск лога рабочей сессии: он пишется в temp ПРОЦЕССА REVIT, который
            // не обязан совпадать с temp той оболочки, где смотрят. Плагин знает свой
            // путь точно — пусть он его и называет, в том файле, который инженер
            // отправляет на разбор.
            Put("Журнал расчёта",          Logger.LogFilePath);
            if (!string.IsNullOrEmpty(Logger.LastError))
                Put("Журнал: ОШИБКА ЗАПИСИ", Logger.LastError);

            Put("Город",                   p.City);
            Put("Температура внутри, °C",  p.InternalTemp);
            Put("Температура снаружи, °C", p.ExternalTemp);
            Put("Помещений в расчёте",     rooms.Count);
            if (summary != null)
            {
                Put("Q итого, Вт",         summary.Q_total);
                Put("Q с запасом, Вт",     summary.Q_final);
                Put("Площадь итого, м²",   summary.Area);
                if (summary.Area > 0)
                    Put("Удельные теплопотери, Вт/м²", Math.Round(summary.Q_final / summary.Area, 1));
            }

            // Климатика — вход всего расчёта: ошибка в t наруж масштабируется на всё
            // здание через ΔT. Встроенная таблица городов с текстом СП 131.13330
            // не сверена, и молчать об этом в отчёте, идущем в экспертизу, нельзя.
            row++;
            ws.Cell(row, 1).Value = "ПРОВЕРИТЬ";
            ws.Cell(row, 2).Value = CityService.UnverifiedWarning;
            ws.Cell(row, 1).Style.Font.SetBold().Font.SetFontColor(XLColor.DarkOrange);
            ws.Cell(row, 2).Style.Font.SetFontColor(XLColor.DarkOrange).Alignment.SetWrapText();
            ws.Row(row).Height = 45;
            row++;

            // Отдельно — если площадка стоит рядом с порогом ГОСТ 30494. Там цена
            // неточности t наруж выше обычного: надбавка жилым комнатам включается
            // ступенькой, а не пропорционально.
            if (ThermalConstants.IsNearColdRegionThreshold(p.ExternalTemp))
            {
                bool applied = p.ExternalTemp <= ThermalConstants.ColdRegionThreshold;
                ws.Cell(row, 1).Value = "ВНИМАНИЕ";
                ws.Cell(row, 2).Value =
                    $"t наруж {p.ExternalTemp} °C — вблизи порога ГОСТ 30494 ({ThermalConstants.ColdRegionThreshold} °C), " +
                    "на котором расчётная температура ЖИЛЫХ КОМНАТ скачком меняется с 20 на 21 °C. " +
                    "Сейчас надбавка " + (applied ? "ПРИМЕНЕНА" : "НЕ применена") + ". " +
                    "Ошибка в один градус по t наруж переключит её и сдвинет итог примерно на 6%: " +
                    "сверьте температуру по исходным данным площадки, а не по встроенной таблице.";
                ws.Cell(row, 1).Style.Font.SetBold().Font.SetFontColor(XLColor.DarkRed);
                ws.Cell(row, 2).Style.Font.SetFontColor(XLColor.DarkRed).Alignment.SetWrapText();
                ws.Row(row).Height = 60;
                row++;
            }

            // ── Откуда взято U ограждений ────────────────────────────────────
            //
            // Главный вопрос доверия к этому отчёту. Плагин умеет читать слои,
            // аналитическое сопротивление и параметры стены — но если в модели
            // теплопроводности не заполнены, он подставляет ТИПОВОЕ значение,
            // и числа отчёта тогда не про этот дом. Молчать об этом нельзя.
            //
            // Мера отсюда: на 76-СУЗДАЛ.23 (прогон 2026-08-12) из модели считался
            // 1% площади ограждений, 99% шли по оценке — при том что утеплитель
            // в модели нарисован и находится геометрически.
            // Источников ТРИ, и они разного веса. «По данным модели» — проект.
            // «По таблице норматива» — толщина из модели, λ из СП 50.13330.2012
            // прил. Т по распознанному материалу: это расчёт, который защитим
            // ссылкой на позицию таблицы. «Типовое значение» — числа, к этому
            // дому отношения не имеющие. Сливать первые два в одно нельзя.
            double areaKnown = p.WallAreaFromModelM2;
            double areaNorm  = p.WallAreaNormativeM2;
            double areaGuess = p.WallAreaEstimatedM2;
            double areaAll   = areaKnown + areaNorm + areaGuess;
            if (areaAll > 0)
            {
                row++;
                Put("Ограждения по данным модели, м²",      Math.Round(areaKnown, 1));
                Put("Ограждения по λ из СП 50 прил. Т, м²", Math.Round(areaNorm, 1));
                Put("Ограждения по типовому U, м²",         Math.Round(areaGuess, 1));
                Put("Доля по данным модели, %",             Math.Round(areaKnown / areaAll * 100, 1));

                if (areaNorm > 0)
                {
                    ws.Cell(row, 1).Value = "ПРИНЯТО ПО НОРМАТИВУ";
                    ws.Cell(row, 2).Value =
                        $"{Math.Round(areaNorm / areaAll * 100)}% площади ограждений " +
                        $"({Math.Round(areaNorm)} м²) посчитано так: толщина конструкции взята из модели, " +
                        "а теплопроводность материала — из таблицы СП 50.13330.2012, приложение Т, " +
                        "по материалу, распознанному в имени типа. Условия эксплуатации Б " +
                        "(СП 50.13330.2012 п. 4.4, таблица 2: нормальный влажностный режим помещений, " +
                        "нормальная зона влажности по приложению В). Это защитимый расчёт, но не данные " +
                        "проекта: если марка материала известна точнее, задайте R по типам конструкций " +
                        "в %APPDATA%\\QOVETER\\wall_types.json — ручной ввод имеет приоритет над таблицей." +
                        (string.IsNullOrEmpty(p.WallTypesNormative)
                            ? ""
                            : " Конструкции: " + p.WallTypesNormative);
                    ws.Cell(row, 1).Style.Font.SetBold();
                    ws.Cell(row, 2).Style.Alignment.SetWrapText();
                    ws.Row(row).Height = 75;
                    row++;
                }

                if (areaGuess > 0)
                {
                    ws.Cell(row, 1).Value = "ПРОВЕРИТЬ";
                    ws.Cell(row, 2).Value =
                        $"{Math.Round(areaGuess / areaAll * 100)}% площади ограждений " +
                        $"({Math.Round(areaGuess)} м²) посчитано ПО ТИПОВОМУ U: в модели у этих конструкций " +
                        "не заданы теплопроводности материалов, аналитическое сопротивление и параметр R, " +
                        "а материал не удалось распознать и по имени типа. " +
                        "Это типовые значения, а не характеристики этого дома. " +
                        "Чтобы числа стали проектными, заполните материалы в модели либо задайте R " +
                        "по типам конструкций в %APPDATA%\\QOVETER\\wall_types.json." +
                        (string.IsNullOrEmpty(p.WallTypesWithoutData)
                            ? ""
                            : " Конструкции без данных: " + p.WallTypesWithoutData);
                    ws.Cell(row, 1).Style.Font.SetBold().Font.SetFontColor(XLColor.DarkRed);
                    ws.Cell(row, 2).Style.Font.SetFontColor(XLColor.DarkRed).Alignment.SetWrapText();
                    ws.Row(row).Height = 75;
                    row++;
                }
            }

            // ── Температуры неотапливаемых объёмов ───────────────────────────
            //
            // СП 50.13330.2012 п. 5.2 разрешает принимать их «на основе расчета
            // теплового баланса», а таблицы значений не даёт. До 2026-08-13 в отчёт
            // уходило подставленное число (+5 °C лоджии), и проверить его было
            // нечем. Теперь на бумаге и результат, и метод, и то, чего в балансе нет.
            var balanced = (p.UnheatedTemperatures ?? new List<UnheatedRoomTemperature>())
                .Where(v => v.FromBalance).ToList();
            var fromTable = (p.UnheatedTemperatures ?? new List<UnheatedRoomTemperature>())
                .Where(v => !v.FromBalance).ToList();

            if (balanced.Count > 0 || fromTable.Count > 0)
            {
                row++;
                if (balanced.Count > 0)
                {
                    Put("Температура лоджий и прочих неотапливаемых объёмов", "по тепловому балансу");
                    Put("  посчитано помещений", balanced.Count);
                    Put("  диапазон, °C",
                        $"{balanced.Min(v => v.Temperature):F1} … {balanced.Max(v => v.Temperature):F1}");
                    Put("  среднее, °C", Math.Round(balanced.Average(v => v.Temperature), 1));
                }
                if (fromTable.Count > 0)
                    Put("  принято по таблице (баланс не собрался)", fromTable.Count);

                ws.Cell(row, 1).Value = "МЕТОД";
                ws.Cell(row, 2).Value =
                    "Расчётная температура воздуха в остеклённой лоджии, тёплом чердаке и техподполье " +
                    "принята ПО РАСЧЁТУ ТЕПЛОВОГО БАЛАНСА — СП 50.13330.2012, п. 5.2. " +
                    "Таблицы значений норматив для них не даёт. Баланс собран по ограждениям " +
                    "самого объёма (холодная сторона) и ограждениям соседних отапливаемых " +
                    "помещений (тёплая). ВОЗДУХООБМЕН самого объёма в баланс не входит: " +
                    "кратность для него в модели не задана, а подставлять её нельзя. " +
                    "Инфильтрация делает объём холоднее, поэтому приведённые температуры — " +
                    "ВЕРХНЯЯ оценка, а теплопотери через такие ограждения — нижняя.";
                ws.Cell(row, 1).Style.Font.SetBold().Font.SetFontColor(XLColor.DarkGreen);
                ws.Cell(row, 2).Style.Font.SetFontColor(XLColor.DarkGreen).Alignment.SetWrapText();
                ws.Row(row).Height = 75;
                row++;
            }

            // ── Исполнение узлов фасада ──────────────────────────────────────
            //
            // Ψ оконного откоса меняется всемеро в зависимости от того, где стоит
            // рама относительно утеплителя (0,058 при нахлёсте 20 мм против 0,433
            // у Г.35). Из модели это не читается, поэтому число в отчёте без
            // указания источника непроверяемо.
            if (!string.IsNullOrWhiteSpace(p.NodeSettingsSource))
            {
                row++;
                Put("Исполнение узлов фасада", p.NodeSettingsSource);
            }

            if (!string.IsNullOrWhiteSpace(p.VentilationSource))
            {
                row++;
                Put("Воздухообмен", p.VentilationSource);
            }

            // ── Ограждения в грунте ──────────────────────────────────────────
            //
            // Метод обязан быть назван: зональный расчёт даёт величины, которые
            // читатель отчёта не воспроизведёт, зная только площадь и U.
            if (p.GroundFloorAreaM2 > 0 || p.GroundWallAreaM2 > 0)
            {
                row++;
                if (p.GroundFloorAreaM2 > 0)
                    Put("Пол по грунту, м²", Math.Round(p.GroundFloorAreaM2, 1));
                if (p.GroundWallAreaM2 > 0)
                    Put("Стены в грунте, м²", Math.Round(p.GroundWallAreaM2, 1));

                ws.Cell(row, 1).Value = "МЕТОД";
                ws.Cell(row, 2).Value =
                    "Ограждения, контактирующие с грунтом, посчитаны ЗОНАЛЬНЫМ методом — " +
                    "СП 50.13330.2024, приложение Г, пункт Г.7: полосы шириной 2 м вдоль контура " +
                    "здания, базовые сопротивления зон по таблицам Г.3 (пол) и Г.4 (стены), " +
                    "формулы (Г.15)–(Г.18). Теплопроводность грунта принята базовой 1,6 Вт/(м·°С) — " +
                    "СП предписывает её при отсутствии документального подтверждения иной. " +
                    "Разность температур взята до НАРУЖНОГО ВОЗДУХА: демпфирование грунта " +
                    "уже учтено в сопротивлениях зон, второй раз его учитывать нельзя.";
                ws.Cell(row, 1).Style.Font.SetBold().Font.SetFontColor(XLColor.DarkGreen);
                ws.Cell(row, 2).Style.Font.SetFontColor(XLColor.DarkGreen).Alignment.SetWrapText();
                ws.Row(row).Height = 75;
                row++;
            }

            // ── Ограждения в шахты ───────────────────────────────────────────
            //
            // За этими метрами стоит НАСТРОЙКА, а не норматив: прямой расчётной
            // температуры вентиляционной шахты СП не даёт. Величина заметная —
            // на 76-СУЗДАЛ.23 такими оказались 127 м² стен 25 санузлов, и до
            // 2026-08-13 они считались по температуре наружного воздуха, давая
            // 5,9% Q огр здания. Молчать об этом в отчёте нельзя ровно потому же,
            // почему нельзя молчать про оценочные U.
            if (p.ShaftAreaM2 > 0)
            {
                row++;
                Put("Ограждения в шахты, м²", Math.Round(p.ShaftAreaM2, 1));
                Put("Температура шахты, °C",
                    p.ShaftTemperature.HasValue ? (object)p.ShaftTemperature.Value : "не задана");

                ws.Cell(row, 1).Value = "ПРОВЕРИТЬ";
                ws.Cell(row, 2).Value =
                    $"{Math.Round(p.ShaftAreaM2)} м² ограждений выходят не на улицу, а в шахту — " +
                    "замкнутый объём внутри отапливаемого контура (проба нашла за стеной пустоту, " +
                    "ограниченную конструкцией, и помещение за ней). " +
                    (p.ShaftTemperature.HasValue
                        ? $"Принята температура шахты {p.ShaftTemperature.Value} °C — это ЗНАЧЕНИЕ ПО УМОЛЧАНИЮ, " +
                          "а не нормативная величина: прямой расчётной температуры для шахты СП не даёт. " +
                          "Изменить — в диалоге «Температуры помещений»."
                        : "Строки «Шахта» в таблице температур нет, поэтому такие ограждения " +
                          "считаются по НАРУЖНОМУ воздуху — теплопотери завышены.");
                ws.Cell(row, 1).Style.Font.SetBold().Font.SetFontColor(XLColor.DarkOrange);
                ws.Cell(row, 2).Style.Font.SetFontColor(XLColor.DarkOrange).Alignment.SetWrapText();
                ws.Row(row).Height = 60;
                row++;
            }

            // Предупреждение о непроверенных Ψ должно стоять на первом листе, а не
            // прятаться в примечании к ячейке: с этим отчётом идут в экспертизу.
            if (rooms.Any(r => r.IsReducedProvisional))
            {
                row++;
                ws.Cell(row, 1).Value = "ВНИМАНИЕ";
                ws.Cell(row, 2).Value =
                    "R приведённое посчитано по ПРЕДВАРИТЕЛЬНЫМ значениям Ψ. " +
                    "Они не выписаны из СП 230.1325800.2015 и не могут подаваться как нормативные. " +
                    "Выверенные значения заносятся в %APPDATA%\\QOVETER\\thermal_bridges.json.";
                ws.Cell(row, 1).Style.Font.SetBold().Font.SetFontColor(XLColor.DarkRed);
                ws.Cell(row, 2).Style.Font.SetFontColor(XLColor.DarkRed).Alignment.SetWrapText();
                ws.Row(row).Height = 45;
            }

            // Обратная сторона того же правила: узел, для которого таблицы СП нет,
            // в итог не пошёл. Это ЗАНИЖЕНИЕ теплопотерь, и молчать о нём нельзя —
            // читатель отчёта иначе считает приведение полным.
            int roomsWithSkipped = rooms.Count(r => r.BridgeNodesSkipped > 0);
            if (roomsWithSkipped > 0)
            {
                row++;
                ws.Cell(row, 1).Value = "ВНИМАНИЕ";
                ws.Cell(row, 2).Value =
                    $"У {roomsWithSkipped} помещений часть узлов НЕ УЧТЕНА в приведённом сопротивлении: " +
                    "таблиц СП 230.1325800.2015 для распознанной конструкции стены нет. " +
                    "Теплопотери на этих узлах ЗАНИЖЕНЫ. Выписать Ψ из СП и занести в " +
                    "%APPDATA%\\QOVETER\\thermal_bridges.json с IsVerified = true.";
                ws.Cell(row, 1).Style.Font.SetBold().Font.SetFontColor(XLColor.DarkOrange);
                ws.Cell(row, 2).Style.Font.SetFontColor(XLColor.DarkOrange).Alignment.SetWrapText();
                ws.Row(row).Height = 60;
            }

            // Тарельчатые анкеры: точечный элемент СП 230 таблица Г.4, в Ψ угла
            // не входит. Плотность крепежа — раскладка дюбелей в проекте фасада,
            // из модели Revit она не читается. Не задана — анкеры не считались,
            // и это ЗАНИЖЕНИЕ, о котором отчёт обязан сказать: 6 шт/м² с χ = 0,004
            // дают 0,024 Вт/(м²·К), около 8% к U = 0,316.
            if (rooms.Any(r => r.BridgeNodesCounted > 0) && rooms.All(r => r.AnchorU <= 0))
            {
                row++;
                ws.Cell(row, 1).Value = "ВНИМАНИЕ";
                ws.Cell(row, 2).Value =
                    "Тарельчатые анкеры фасадной системы НЕ УЧТЕНЫ: плотность крепежа (шт/м²) " +
                    "не задана. По СП 230.1325800.2015 таблица Г.4 это самостоятельный точечный " +
                    "теплозащитный элемент, в Ψ угла он не входит. Теплопотери на нём ЗАНИЖЕНЫ — " +
                    "порядок величины 0,02 Вт/(м²·К), около 8% к U наружной стены. " +
                    "Задаётся полем AnchorsPerM2 в файле объекта <модель>.QOVETER-узлы.json.";
                ws.Cell(row, 1).Style.Font.SetBold().Font.SetFontColor(XLColor.DarkOrange);
                ws.Cell(row, 2).Style.Font.SetFontColor(XLColor.DarkOrange).Alignment.SetWrapText();
                ws.Row(row).Height = 60;
            }

            // Конструкция, ПРИНЯТАЯ допущением. Ψ у таких помещений остаются
            // нормативными — но описывают соседнюю стену того же дома, а не эту.
            // Без отдельной строки отчёт выглядит так, будто конструкция прочитана
            // из модели у всех: разница видна только в журнале, куда в экспертизу
            // никто не пойдёт.
            int roomsAssumed = rooms.Count(r => r.IsWallConstructionAssumed);
            if (roomsAssumed > 0)
            {
                row++;
                ws.Cell(row, 1).Value = "Конструкция принята";
                ws.Cell(row, 2).Value =
                    $"У {roomsAssumed} помещений конструкция наружного ограждения ПРИНЯТА, " +
                    "а не прочитана из слоёв модели: либо в уличном ограждении остался только " +
                    "отделочный слой и взята преобладающая конструкция объекта, либо уличных " +
                    "стен у помещения нет вовсе и конструкция взята с ограждения к лоджии или шахте. " +
                    "Ψ по СП 230 нормативные, но описывают соседнюю стену этого же дома. " +
                    "Точное распределение — строкой «[Конструкция]» в журнале расчёта.";
                ws.Cell(row, 1).Style.Font.SetBold().Font.SetFontColor(XLColor.DarkOrange);
                ws.Cell(row, 2).Style.Font.SetFontColor(XLColor.DarkOrange).Alignment.SetWrapText();
                ws.Row(row).Height = 75;
            }

            ws.Column(1).Width = 38;
            ws.Column(2).Width = 22;
        }

        private static void BuildRoomsSheet(IXLWorkbook book, List<CalculationResult> rooms)
        {
            var ws = book.Worksheets.Add("По помещениям");
            string[] headers =
            {
                // «№» — порядковый номер строки; «Номер» — НАСТОЯЩИЙ номер помещения
                // из модели (RoomData.Number). Без него строку отчёта нельзя было
                // сопоставить ни с планом, ни с фикстурой: номер был только в JSON.
                "№", "Номер", "Помещение", "Кв.", "Этаж", "Тип", "Ориентация",
                // «Окна вычтено» — площадь, снятая с площади стен; «Окна всего» —
                // сумма по окнам помещения, по которой считаются потери. Величины
                // ОБЯЗАНЫ совпадать. Пока была одна колонка, их расхождение — то есть
                // двойной счёт остекления — не было видно ничем: в отчёте стоял ноль,
                // а потери через окна считались.
                // «Стены» — все ограждения помещения; «в т.ч. к неотапл.» — та их часть,
                // что выходит не на улицу, а на лоджию, лестницу или тамбур. У этой части
                // своя ΔT (лоджия 5 °C), и в отчёте это должно быть видно: иначе
                // одинаковые по площади помещения выглядят необъяснимо разными.
                "Площадь, м²", "Стены, м²", "в т.ч. к неотапл., м²",
                "Окна вычтено, м²", "Окна всего, м²",
                // Колонки «Q инф» нет намеренно: по ТЗ инфильтрация отдельно не считается —
                // формула (1) даёт единый член Qинф/вент, он же Q вент. Раньше колонка
                // выводилась и всегда была нулевой.
                // Q огр — сумма; следом её слагаемые. Без них «Q огр» неразбираем:
                // на 76-СУЗДАЛ.23 этаж 14 даёт +7,4% против расчёта проектировщика,
                // и по одной суммарной цифре нельзя сказать, кровля это, стены
                // или окна. Q_floor и Q_roof считались с самого начала, но в отчёт
                // не выводились — появление пола первого этажа пришлось в своё
                // время подтверждать через код и тест вместо отчёта.
                "Q огр, Вт", "в т.ч. стены", "в т.ч. окна", "в т.ч. двери",
                "в т.ч. пол", "в т.ч. кровля",
                "Q вент, Вт", "Q вн, Вт",
                "Q итого, Вт", "Q с запасом, Вт", "Уд., Вт/м²",
                "Σβ",
                // Инженеру нужны обе величины сразу: условное сопротивление — то, что
                // даёт слоёный пирог, приведённое — с учётом теплотехнических
                // неоднородностей. r показывает, сколько съедают мостики.
                "R усл, м²·К/Вт", "R пр, м²·К/Вт", "r"
            };

            for (int i = 0; i < headers.Length; i++)
            {
                ws.Cell(1, i + 1).Value = headers[i];
            }
            ws.Range(1, 1, 1, headers.Length).Style
                .Font.SetBold()
                .Fill.SetBackgroundColor(XLColor.LightGray)
                .Border.SetBottomBorder(XLBorderStyleValues.Thin);

            int row = 2;
            int idx = 1;
            foreach (var r in rooms)
            {
                ws.Cell(row, 1).Value = idx++;
                ws.Cell(row, 2).Value = r.RoomData?.Number ?? "";
                ws.Cell(row, 3).Value = r.RoomData?.Name ?? r.RoomName;
                ws.Cell(row, 4).Value = string.IsNullOrEmpty(r.Apartment) ? "—" : r.Apartment;
                ws.Cell(row, 5).Value = r.FloorNumber;
                ws.Cell(row, 6).Value = r.RoomData?.Type ?? "";
                ws.Cell(row, 7).Value = r.Orientation ?? "";
                ws.Cell(row, 8).Value = r.Area;
                ws.Cell(row, 9).Value = r.WallArea;
                ws.Cell(row, 10).Value = Math.Round(
                    r.RoomData?.Walls?.Where(w => w.IsExternal && w.AdjacentCategory.HasValue)
                                      .Sum(w => w.Area) ?? 0, 2);
                ws.Cell(row, 11).Value = r.WindowArea;
                ws.Cell(row, 12).Value = r.ExtWindowsArea;
                // Расхождение = площадь окон не вычтена из стен, значит остекление
                // посчитано дважды. Помечаем прямо в отчёте, а не оставляем на глаз.
                if (Math.Abs(r.ExtWindowsArea - r.WindowArea) > 0.05)
                {
                    ws.Cell(row, 12).Style.Font.SetBold().Font.SetFontColor(XLColor.DarkRed);
                    ws.Cell(row, 12).GetComment().AddText(
                        "Площадь окон не вычтена из площади стен: остекление учтено дважды, " +
                        "теплопотери помещения завышены");
                }
                ws.Cell(row, 13).Value = r.Q_ogr;
                ws.Cell(row, 14).Value = Math.Round(r.Q_walls, 1);
                ws.Cell(row, 15).Value = Math.Round(r.Q_windows, 1);
                ws.Cell(row, 16).Value = Math.Round(r.Q_doors, 1);
                ws.Cell(row, 17).Value = Math.Round(r.Q_floor, 1);
                ws.Cell(row, 18).Value = Math.Round(r.Q_roof, 1);

                // Слагаемые обязаны складываться в Q огр. Расхождение означает,
                // что какая-то составляющая считается, но не показана, —
                // ровно та беда, ради которой эти колонки и заведены.
                double parts = r.Q_walls + r.Q_windows + r.Q_doors + r.Q_floor + r.Q_roof;
                if (r.Q_ogr > 0 && Math.Abs(parts - r.Q_ogr) > Math.Max(1.0, r.Q_ogr * 0.02))
                {
                    ws.Cell(row, 13).Style.Font.SetBold().Font.SetFontColor(XLColor.DarkRed);
                    ws.Cell(row, 13).GetComment().AddText(
                        $"Слагаемые дают {parts:F0} Вт против {r.Q_ogr:F0} Вт в сумме: " +
                        "часть теплопотерь не разложена по составляющим");
                }

                ws.Cell(row, 19).Value = r.Q_vent;
                ws.Cell(row, 20).Value = r.Q_vn;
                ws.Cell(row, 21).Value = r.Q_total;
                ws.Cell(row, 22).Value = r.Q_final;
                ws.Cell(row, 23).Value = r.Area > 0 ? Math.Round(r.Q_final / r.Area, 1) : 0;
                double betaSum = r.BetaCoefficients?.Values.Sum() ?? 0;
                ws.Cell(row, 24).Value = Math.Round(betaSum, 3);
                ws.Cell(row, 25).Value = r.R_conditional;
                ws.Cell(row, 26).Value = r.R_reduced;
                ws.Cell(row, 27).Value = r.Homogeneity;
                if (r.IsReducedProvisional)
                {
                    ws.Cell(row, 26).Style.Font.SetItalic().Font.SetFontColor(XLColor.DarkOrange);
                    ws.Cell(row, 26).GetComment().AddText(
                        "R приведённое посчитано по предварительным Ψ — не сверено с СП 230.1325800.2015");
                }
                else if (r.BridgeNodesSkipped > 0)
                {
                    ws.Cell(row, 26).Style.Font.SetItalic().Font.SetFontColor(XLColor.DarkOrange);
                    ws.Cell(row, 26).GetComment().AddText(
                        $"Учтено узлов {r.BridgeNodesCounted}, пропущено {r.BridgeNodesSkipped}: " +
                        $"таблиц СП 230 для конструкции «{r.WallConstructionName}» нет — R_пр завышено");
                }
                else if (r.IsWallConstructionAssumed)
                {
                    // Не оранжевым: узлы посчитаны и Ψ нормативные. Помечается сам
                    // факт допущения — конструкцию описала соседняя стена.
                    ws.Cell(row, 26).Style.Font.SetItalic();
                    ws.Cell(row, 26).GetComment().AddText(
                        $"Конструкция «{r.WallConstructionName}» ПРИНЯТА, а не прочитана из слоёв " +
                        "модели: в ограждении этого помещения разбирать нечего. " +
                        "Ψ взяты по таблицам СП 230 для этой конструкции.");
                }
                row++;
            }

            // Авто-ширина и формат чисел. Числовой формат — начиная с «Площадь»:
            // «Номер» помещения остаётся текстом (в моделях он бывает «12а»).
            ws.Columns(8, headers.Length).Style.NumberFormat.SetFormat("0.00");
            ws.Columns().AdjustToContents();
            ws.SheetView.FreezeRows(1);
            if (row > 2)
                ws.Range(2, 1, row - 1, headers.Length).SetAutoFilter();
        }

        /// <summary>
        /// Итоги по квартирам. По ТЗ воздухообмен нормируется на квартиру, значит и
        /// проверять расчёт инженер будет по квартире. Помещения без номера квартиры
        /// (лестницы, лифтовые холлы, внеквартирные коридоры) сведены отдельной строкой:
        /// нормативно они идут в общедомовую систему, а не в квартирную.
        /// </summary>
        private static void BuildApartmentSheet(IXLWorkbook book, List<CalculationResult> rooms)
        {
            var ws = book.Worksheets.Add("По квартирам");
            string[] headers =
            {
                "Квартира", "Помещений", "Площадь, м²",
                "Q огр, Вт", "Q вент, Вт", "Q итого, Вт", "Q с запасом, Вт", "Уд., Вт/м²"
            };
            for (int i = 0; i < headers.Length; i++)
                ws.Cell(1, i + 1).Value = headers[i];
            ws.Range(1, 1, 1, headers.Length).Style
                .Font.SetBold()
                .Fill.SetBackgroundColor(XLColor.LightGray)
                .Border.SetBottomBorder(XLBorderStyleValues.Thin);

            int row = 2;
            var apartments = rooms
                .Where(r => !string.IsNullOrEmpty(r.Apartment))
                .GroupBy(r => r.Apartment)
                .OrderBy(g => g.Key, new ApartmentNumberComparer());

            foreach (var apartment in apartments)
            {
                WriteGroup(ws, ref row, apartment.Key, apartment.ToList());
            }

            var common = rooms.Where(r => string.IsNullOrEmpty(r.Apartment)).ToList();
            if (common.Count > 0)
            {
                WriteGroup(ws, ref row, "Вне квартир (общедомовые)", common);
                ws.Row(row - 1).Style.Font.SetItalic();
            }

            if (row > 2)
            {
                ws.Columns(3, headers.Length).Style.NumberFormat.SetFormat("0.0");
                ws.SheetView.FreezeRows(1);
            }
            ws.Columns().AdjustToContents();
        }

        private static void WriteGroup(IXLWorksheet ws, ref int row, string label,
                                       List<CalculationResult> group)
        {
            double area = group.Sum(r => r.Area);
            ws.Cell(row, 1).Value = label;
            ws.Cell(row, 2).Value = group.Count;
            ws.Cell(row, 3).Value = Math.Round(area, 2);
            ws.Cell(row, 4).Value = Math.Round(group.Sum(r => r.Q_ogr), 1);
            ws.Cell(row, 5).Value = Math.Round(group.Sum(r => r.Q_vent), 1);
            ws.Cell(row, 6).Value = Math.Round(group.Sum(r => r.Q_total), 1);
            ws.Cell(row, 7).Value = Math.Round(group.Sum(r => r.Q_final), 1);
            ws.Cell(row, 8).Value = area > 0 ? Math.Round(group.Sum(r => r.Q_final) / area, 1) : 0;
            row++;
        }

        /// <summary>
        /// Номера квартир сортируются как числа, если они числовые: иначе «10» встаёт
        /// между «1» и «2», и инженер не находит свою квартиру.
        /// </summary>
        private class ApartmentNumberComparer : IComparer<string>
        {
            public int Compare(string x, string y)
            {
                int nx, ny;
                bool okX = int.TryParse(x, out nx);
                bool okY = int.TryParse(y, out ny);
                if (okX && okY) return nx.CompareTo(ny);
                if (okX) return -1;
                if (okY) return 1;
                return string.Compare(x, y, StringComparison.CurrentCulture);
            }
        }

        private static void BuildFloorSummarySheet(IXLWorkbook book, List<CalculationResult> rooms)
        {
            var ws = book.Worksheets.Add("По этажам");
            string[] headers =
            {
                "Этаж", "Помещений", "Площадь, м²",
                "Q огр, Вт", "Q вент, Вт", "Q итого, Вт", "Q с запасом, Вт"
            };
            for (int i = 0; i < headers.Length; i++)
                ws.Cell(1, i + 1).Value = headers[i];
            ws.Range(1, 1, 1, headers.Length).Style
                .Font.SetBold()
                .Fill.SetBackgroundColor(XLColor.LightGray);

            int row = 2;
            var byFloor = rooms.GroupBy(r => r.FloorNumber).OrderBy(g => g.Key);
            foreach (var floor in byFloor)
            {
                ws.Cell(row, 1).Value = floor.Key;
                ws.Cell(row, 2).Value = floor.Count();
                ws.Cell(row, 3).Value = Math.Round(floor.Sum(r => r.Area), 2);
                ws.Cell(row, 4).Value = Math.Round(floor.Sum(r => r.Q_ogr), 1);
                ws.Cell(row, 5).Value = Math.Round(floor.Sum(r => r.Q_vent), 1);
                ws.Cell(row, 6).Value = Math.Round(floor.Sum(r => r.Q_total), 1);
                ws.Cell(row, 7).Value = Math.Round(floor.Sum(r => r.Q_final), 1);
                row++;
            }
            ws.Columns().AdjustToContents();
        }

        private static void BuildTotalSheet(
            IXLWorkbook book,
            CalculationResult summary,
            List<CalculationResult> rooms)
        {
            var ws = book.Worksheets.Add("Итог");
            // Заголовок берётся из итоговой строки расчёта: если считали один этаж,
            // лист не должен называться «Сводно по зданию».
            ws.Cell("A1").Value = string.IsNullOrWhiteSpace(summary?.RoomName)
                ? "Сводно"
                : "Сводно: " + summary.RoomName.Replace("ИТОГО ", "").ToLowerInvariant();
            ws.Range("A1:B1").Merge().Style.Font.SetBold().Font.SetFontSize(14);

            int row = 3;
            void Put(string label, double value, string format = "0.0")
            {
                ws.Cell(row, 1).Value = label;
                ws.Cell(row, 2).Value = value;
                ws.Cell(row, 2).Style.NumberFormat.SetFormat(format);
                ws.Cell(row, 1).Style.Font.SetBold();
                row++;
            }

            double totalArea  = summary?.Area  ?? rooms.Sum(r => r.Area);
            double totalOgr   = summary?.Q_ogr ?? rooms.Sum(r => r.Q_ogr);
            double totalVent  = summary?.Q_vent?? rooms.Sum(r => r.Q_vent);
            double totalQ     = summary?.Q_total ?? rooms.Sum(r => r.Q_total);
            double totalFinal = summary?.Q_final ?? rooms.Sum(r => r.Q_final);

            Put("Помещений",                rooms.Count, "0");
            Put("Площадь, м²",              totalArea,   "0.0");
            Put("Q ограждающих, Вт",        totalOgr,    "0.0");
            Put("Q вентиляции, Вт",         totalVent,   "0.0");
            Put("Q итого, Вт",              totalQ,      "0.0");
            Put("Q с запасом, Вт",          totalFinal,  "0.0");
            if (totalArea > 0)
                Put("Удельные, Вт/м²",      Math.Round(totalFinal / totalArea, 1), "0.0");

            ws.Column(1).Width = 32;
            ws.Column(2).Width = 18;
        }

        private static void BuildErrorsSheet(IXLWorkbook book, List<CalculationResult> errors)
        {
            var ws = book.Worksheets.Add("Ошибки");
            ws.Cell(1, 1).Value = "Помещение";
            ws.Cell(1, 2).Value = "Сообщение";
            ws.Range(1, 1, 1, 2).Style.Font.SetBold().Fill.SetBackgroundColor(XLColor.Salmon);
            int row = 2;
            foreach (var r in errors)
            {
                ws.Cell(row, 1).Value = r.RoomName;
                ws.Cell(row, 2).Value = r.ErrorMessage;
                row++;
            }
            ws.Columns().AdjustToContents();
        }

        private static void EnsureDirectoryExists(string filePath)
        {
            var dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);
        }
    }

    /// <summary>Параметры расчёта для шапки Excel-отчёта</summary>
    public class ExcelExportParams
    {
        public string City         { get; set; } = "";
        public double InternalTemp { get; set; } = 20.0;
        public double ExternalTemp { get; set; } = -25.0;

        /// <summary>Площадь ограждений, посчитанная по ДАННЫМ МОДЕЛИ, м².</summary>
        public double WallAreaFromModelM2 { get; set; }

        /// <summary>
        /// Площадь ограждений, посчитанная по НОРМАТИВУ, м²: толщина из модели,
        /// λ из таблицы СП 50.13330.2012 прил. Т по распознанному материалу.
        /// </summary>
        public double WallAreaNormativeM2 { get; set; }

        /// <summary>Площадь ограждений на ТИПОВОМ U — материал не распознан вовсе, м².</summary>
        public double WallAreaEstimatedM2 { get; set; }

        /// <summary>
        /// Типы конструкций, посчитанные по таблице норматива, с площадями —
        /// чтобы было видно, к чему приложена справочная λ, а не проектная.
        /// </summary>
        public string WallTypesNormative { get; set; } = "";

        /// <summary>
        /// Типы конструкций без теплотехнических данных, с площадями —
        /// список того, что инженеру предстоит либо заполнить в модели,
        /// либо задать вручную.
        /// </summary>
        public string WallTypesWithoutData { get; set; } = "";

        /// <summary>Площадь ограждений, выходящих в шахты (не на улицу), м².</summary>
        public double ShaftAreaM2 { get; set; }

        /// <summary>
        /// Температуры неотапливаемых объёмов, посчитанные тепловым балансом
        /// (СП 50.13330 п. 5.2). Пусто — считали по таблице, как до 2026-08-13.
        /// </summary>
        public List<UnheatedRoomTemperature> UnheatedTemperatures { get; set; }

        /// <summary>
        /// Принятая расчётная температура шахты, °C. null — строки в таблице нет,
        /// и тогда движок считает шахту по наружному воздуху.
        /// </summary>
        public double? ShaftTemperature { get; set; }

        /// <summary>
        /// Откуда взято исполнение узлов фасада: путь к файлу объекта либо
        /// пометка про умолчания. Число Ψ без этой строки непроверяемо —
        /// у оконного узла разброс по исполнениям доходит до семи раз.
        /// </summary>
        public string NodeSettingsSource { get; set; }

        /// <summary>
        /// Как считался воздухообмен. Величина заметная: на 76-СУЗДАЛ.23 она даёт
        /// 57% итога, и методики у заказчиков расходятся — читатель отчёта обязан
        /// видеть, по какой считали.
        /// </summary>
        public string VentilationSource { get; set; }

        /// <summary>Площадь пола по грунту, посчитанная зональным методом, м².</summary>
        public double GroundFloorAreaM2 { get; set; }

        /// <summary>Площадь заглублённых стен, посчитанная зональным методом, м².</summary>
        public double GroundWallAreaM2 { get; set; }
    }
}
