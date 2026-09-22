using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.UI;
using QOVETER.Models;
using System;
using System.Collections.Generic;
using System.Linq;

using static TNovCommon.ElementIdCompat;

namespace QOVETER.Services
{
    /// <summary>
    /// Запись результатов расчёта теплопотерь в параметры пространственных элементов
    /// активного документа: помещений (Room) в АР-модели и пространств (Space) в ОВ.
    ///
    /// Расчёт ведётся в АР-файле, где живут помещения, — туда же по умолчанию идёт
    /// и запись. Space поддержан как запасной путь: если помещений в документе нет,
    /// а пространства есть, значения пишутся в них.
    ///
    /// Сопоставление: сначала по <c>ElementId</c> — он точный, так как результаты
    /// собраны из этого же документа. Номер используется только как запасной ключ,
    /// потому что номера помещений в многоэтажном доме повторяются от этажа к этажу,
    /// и запись «по номеру» разложила бы одно значение сразу на все этажи.
    /// API Revit 2022 — используются актуальные методы и UnitTypeId.
    /// </summary>
    public class RevitParameterWriter
    {
        public string TargetParameterName { get; set; } = "N_Теплопотери";
        private readonly Document _document;

        public RevitParameterWriter(Document document)
        {
            _document = document ?? throw new ArgumentNullException(nameof(document));
        }

        // ─────────────────────────────────────────────────────────
        //  ПУБЛИЧНЫЙ МЕТОД — записывает Q_final во все совпадающие Space
        // ─────────────────────────────────────────────────────────

        /// <summary>
        /// Записывает расчётные теплопотери Q_final в параметр "N_Теплопотери"
        /// элементов Space в текущей (ОВ) модели, сопоставляя их с результатами
        /// расчётов по номеру помещения.
        /// </summary>
        /// <returns>Объект с результатами операции</returns>
        public WriteResult WriteHeatLossToSpaces(List<CalculationResult> results)
        {
            var writeResult = new WriteResult
            {
                ParamName = TargetParameterName
            };

            if (results == null || results.Count == 0)
            {
                writeResult.ErrorMessage = "Список результатов расчёта пуст.";
                return writeResult;
            }

            // Собираем результаты, исключая итоговую строку
            var roomResults = results
                .Where(r => !r.IsSummary && r.RoomData != null)
                .ToList();

            if (roomResults.Count == 0)
            {
                writeResult.ErrorMessage = "Нет результатов с привязанными данными помещений.";
                return writeResult;
            }

            // Помещения АР — основная цель, пространства ОВ — запасная.
            var targets = CollectSpatialElements();
            writeResult.TargetKind = targets.Kind;

            if (targets.Elements.Count == 0)
            {
                writeResult.ErrorMessage =
                    "В активном документе нет ни помещений (Room), ни пространств (Space).\n" +
                    "Откройте модель, в которой они размещены.";
                return writeResult;
            }

            var byId = targets.Elements
                .GroupBy(e => e.Id.IntValue())
                .ToDictionary(g => g.Key, g => g.First());
            var byNumber = BuildNumberIndex(targets.Elements);

            // TransactionGroup — позволяет ОТКАТИТЬ всю операцию одной командой Undo в Revit,
            // даже если внутри несколько Transaction. Также объединяет всё в один пункт истории.
            using (var group = new TransactionGroup(_document, "QOVETER: Запись теплопотерь"))
            {
                group.Start();
                using (var transaction = new Transaction(_document, "QOVETER: Запись теплопотерь"))
                {
                    transaction.Start();
                    try
                    {
                        foreach (var result in roomResults)
                        {
                            // 1. Точное совпадение по ElementId — результаты собраны
                            //    из этого же документа, так что это надёжнее номера.
                            SpatialElement exact;
                            if (result.RoomData.Id != 0 && byId.TryGetValue(result.RoomData.Id, out exact))
                            {
                                if (WriteValue(exact, result.Q_final, writeResult))
                                    writeResult.WrittenCount++;
                                continue;
                            }

                            // 2. Запасной путь — по номеру. Возможен, когда расчёт
                            //    выполнен в одной модели, а запись идёт в другую
                            //    (АР → ОВ). Номера по этажам повторяются, поэтому
                            //    неоднозначные совпадения помечаются отдельно.
                            string roomNumber = result.RoomData?.Number?.Trim();
                            if (string.IsNullOrEmpty(roomNumber)) continue;

                            List<SpatialElement> matched;
                            if (!byNumber.TryGetValue(roomNumber, out matched))
                            {
                                writeResult.NotFoundNumbers.Add(roomNumber);
                                continue;
                            }

                            if (matched.Count > 1)
                                writeResult.AmbiguousNumbers.Add($"{roomNumber} ({matched.Count})");

                            foreach (var element in matched)
                            {
                                if (WriteValue(element, result.Q_final, writeResult))
                                    writeResult.WrittenCount++;
                            }
                        }

                        transaction.Commit();
                    }
                    catch (Exception ex)
                    {
                        Logger.Error("RevitParameterWriter: ошибка транзакции", ex);
                        transaction.RollBack();
                        writeResult.ErrorMessage = $"Ошибка транзакции Revit: {ex.Message}";
                    }
                }

                if (string.IsNullOrEmpty(writeResult.ErrorMessage) && writeResult.Errors.Count == 0)
                {
                    group.Assimilate();   // схлопываем в один шаг истории
                }
                else
                {
                    // Откатываем операцию целиком — и ОБЯЗАТЕЛЬНО сообщаем об этом.
                    // Раньше WrittenCount оставался ненулевым, и пользователь читал
                    // «✅ Успешно записано: N», хотя в модели после отката не было
                    // ни одного значения.
                    group.RollBack();
                    writeResult.WasRolledBack = true;
                    Logger.Warn($"RevitParameterWriter: операция откачена, " +
                                $"{writeResult.WrittenCount} значений не сохранены");
                    writeResult.WrittenCount = 0;
                }
            }

            return writeResult;
        }

        // ─────────────────────────────────────────────────────────
        //  СБОР SPACE
        // ─────────────────────────────────────────────────────────

        private List<Space> CollectAllSpaces()
        {
            return new FilteredElementCollector(_document)
                .OfCategory(BuiltInCategory.OST_MEPSpaces)
                .WhereElementIsNotElementType()
                .OfType<Space>()
                .ToList();
        }

        private List<SpatialElement> CollectAllRooms()
        {
            return new FilteredElementCollector(_document)
                .OfCategory(BuiltInCategory.OST_Rooms)
                .WhereElementIsNotElementType()
                .OfType<SpatialElement>()
                .Where(r => r.Area > 0)
                .ToList();
        }

        /// <summary>
        /// Куда писать: помещения активного документа, а если их нет — пространства.
        /// Расчёт ведётся в АР-файле, поэтому помещения идут первыми.
        /// </summary>
        private (List<SpatialElement> Elements, string Kind) CollectSpatialElements()
        {
            var rooms = CollectAllRooms();
            if (rooms.Count > 0)
            {
                Logger.Info($"RevitParameterWriter: цель записи — помещения (Room), {rooms.Count} шт.");
                return (rooms, "помещений");
            }

            var spaces = CollectAllSpaces().Cast<SpatialElement>().ToList();
            Logger.Info($"RevitParameterWriter: помещений нет, цель записи — " +
                        $"пространства (Space), {spaces.Count} шт.");
            return (spaces, "пространств");
        }

        /// <summary>
        /// Индекс: номер → элементы с таким номером. Номера в многоэтажном доме
        /// повторяются, поэтому значение — список, а неоднозначность фиксируется
        /// при записи.
        /// </summary>
        private Dictionary<string, List<SpatialElement>> BuildNumberIndex(List<SpatialElement> elements)
        {
            var index = new Dictionary<string, List<SpatialElement>>(StringComparer.OrdinalIgnoreCase);

            foreach (var element in elements)
            {
                try
                {
                    string number = element.Number?.Trim();
                    if (string.IsNullOrEmpty(number))
                    {
                        var param = element.get_Parameter(BuiltInParameter.ROOM_NUMBER);
                        number = param?.AsString()?.Trim();
                    }
                    if (string.IsNullOrEmpty(number)) continue;

                    if (!index.ContainsKey(number))
                        index[number] = new List<SpatialElement>();
                    index[number].Add(element);
                }
                catch (Exception ex)
                {
                    Logger.Debug($"RevitParameterWriter: номер элемента не прочитан: {ex.Message}");
                }
            }

            return index;
        }

        // ─────────────────────────────────────────────────────────
        //  ЗАПИСЬ ЗНАЧЕНИЯ В ПАРАМЕТР SPACE
        // ─────────────────────────────────────────────────────────

        private bool WriteValue(SpatialElement element, double qFinalWatts, WriteResult result)
        {
            try
            {
                // Ищем параметр
                Parameter param = FindParameter(element, TargetParameterName);

                if (param == null)
                {
                    result.MissingParamSpaces.Add(
                        $"{element.Number} ({element.Name})");
                    return false;
                }

                if (param.IsReadOnly)
                {
                    result.ReadOnlySpaces.Add(
                        $"{element.Number} ({element.Name})");
                    return false;
                }

                // Проверка типа параметра и конвертация единиц (Revit 2022 API)
                double valueToWrite = ConvertToRevitInternalUnits(param, qFinalWatts);

                // Тип параметра: Double
                if (param.StorageType == StorageType.Double)
                {
                    param.Set(valueToWrite);
                    return true;
                }

                // Тип параметра: Integer (редко, но обрабатываем)
                if (param.StorageType == StorageType.Integer)
                {
                    param.Set((int)Math.Round(qFinalWatts));
                    return true;
                }

                // Тип параметра: String (текстовый — записываем как строку)
                if (param.StorageType == StorageType.String)
                {
                    param.Set(qFinalWatts.ToString("F1"));
                    return true;
                }

                result.MissingParamSpaces.Add(
                    $"{element.Number} — несовместимый тип хранения ({param.StorageType})");
                return false;
            }
            catch (Exception ex)
            {
                result.Errors.Add(
                    $"{element.Number}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Находит параметр по имени среди экземплярных параметров помещения
        /// или пространства.
        /// </summary>
        private Parameter FindParameter(SpatialElement element, string paramName)
        {
            // Прямой поиск по имени
            foreach (Parameter p in element.Parameters)
            {
                if (string.Equals(p.Definition?.Name, paramName,
                    StringComparison.OrdinalIgnoreCase))
                    return p;
            }
            return null;
        }

        /// <summary>
        /// Конвертирует значение из Ватт во внутренние единицы Revit
        /// в зависимости от SpecTypeId параметра.
        /// Revit 2022: используем ForgeTypeId / UnitTypeId.
        /// </summary>
        private double ConvertToRevitInternalUnits(Parameter param, double wattsValue)
        {
            try
            {
                ForgeTypeId specTypeId = param.Definition.GetDataType();

                // HvacPower (тепловая нагрузка, Вт) — конвертируем Через UnitTypeId.Watts
                if (specTypeId.Equals(SpecTypeId.HvacPower))
                    return UnitUtils.ConvertToInternalUnits(wattsValue, UnitTypeId.Watts);

                // Number (безразмерный) — записываем значение напрямую (параметр хранит Вт как есть)
                // HeatTransferCoefficient — это U-value (W/m²·K), не мощность — неподходящий тип
                return wattsValue;
            }
            catch
            {
                return wattsValue;
            }
        }

    }

    // ─────────────────────────────────────────────────────────────
    //  ВСПОМОГАТЕЛЬНЫЕ КЛАССЫ
    // ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Результат операции записи в модель Revit
    /// </summary>
    public class WriteResult
    {
        public string ParamName { get; set; } = "N_Теплопотери";
        public int WrittenCount { get; set; } = 0;
        public string ErrorMessage { get; set; }

        /// <summary>Куда шла запись: «помещений» (Room, АР) или «пространств» (Space, ОВ).</summary>
        public string TargetKind { get; set; } = "элементов";

        public List<string> NotFoundNumbers  { get; } = new List<string>();
        public List<string> MissingParamSpaces { get; } = new List<string>();
        public List<string> ReadOnlySpaces   { get; } = new List<string>();
        public List<string> Errors           { get; } = new List<string>();

        /// <summary>
        /// Номера, под которыми нашлось больше одного элемента: в многоэтажном доме
        /// номера повторяются, и значение легло сразу на несколько помещений.
        /// </summary>
        public List<string> AmbiguousNumbers { get; } = new List<string>();

        /// <summary>
        /// Вся операция откачена: в модели не осталось ни одного записанного значения.
        /// Взводится, когда при записи была хотя бы одна ошибка — TransactionGroup
        /// откатывается целиком: наполовину записанный результат хуже незаписанного.
        /// </summary>
        public bool WasRolledBack { get; set; }

        public bool IsSuccess => string.IsNullOrEmpty(ErrorMessage) && Errors.Count == 0;

        public string GetSummaryMessage()
        {
            var lines = new System.Text.StringBuilder();

            if (WasRolledBack)
            {
                lines.AppendLine("❌ Запись ОТМЕНЕНА: из-за ошибок вся операция откачена, " +
                                 "в модели ничего не изменилось.");
            }
            else
            {
                lines.AppendLine($"✅ Успешно записано: {WrittenCount} {TargetKind}");
            }

            if (NotFoundNumbers.Count > 0)
                lines.AppendLine($"⚠ Не найдены элементы с номерами: {string.Join(", ", NotFoundNumbers)}");

            if (AmbiguousNumbers.Count > 0)
                lines.AppendLine($"⚠ Номер встречается несколько раз — значение записано во все, " +
                    $"проверьте: {string.Join(", ", AmbiguousNumbers)}");

            if (MissingParamSpaces.Count > 0)
                lines.AppendLine($"⚠ Параметр «{ParamName}» отсутствует у: " +
                    string.Join(", ", MissingParamSpaces));

            if (ReadOnlySpaces.Count > 0)
                lines.AppendLine($"🔒 Параметр доступен только для чтения у: " +
                    string.Join(", ", ReadOnlySpaces));

            if (Errors.Count > 0)
                lines.AppendLine($"❌ Ошибки:\n{string.Join("\n", Errors)}");

            return lines.ToString().TrimEnd();
        }
    }

}
