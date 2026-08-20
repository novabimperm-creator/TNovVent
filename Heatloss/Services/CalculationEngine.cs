using System;
using System.Collections.Generic;
using System.Linq;
using QOVETER.Models;

namespace QOVETER.Services
{
    public class CalculationEngine
    {
        // Все нормативные константы — в ThermalConstants со ссылками на пункты СП.

        private readonly FloorRoofThermalCalculator _floorRoofCalculator;
        private readonly ReducedResistanceCalculator _reducedResistance =
            new ReducedResistanceCalculator(ThermalBridgeCatalog.Load());

        /// <summary>
        /// Создаёт движок без доступа к Revit API. U пола/кровли будут только нормативные.
        /// </summary>
        public CalculationEngine() { }

        /// <summary>
        /// Создаёт движок, который читает реальные конструкции пола/кровли из активного документа.
        /// </summary>
        public CalculationEngine(Autodesk.Revit.DB.Document document)
        {
            if (document != null)
                _floorRoofCalculator = new FloorRoofThermalCalculator(document);
        }

        public List<CalculationResult> Calculate(List<RoomData> rooms, CalculationParameters parameters, BuildingParameters buildingParams)
        {
            var results = new List<CalculationResult>();
            
            // Фильтруем выбранные помещения
            var selectedRooms = rooms.Where(r => r.IsSelected).ToList();
            if (selectedRooms.Count == 0)
            {
                return results;
            }
            
            // Определяем этажность помещений
            foreach (var room in selectedRooms)
            {
                room.DetermineFloorProperties(buildingParams.GroundFloorNumber, buildingParams.TopFloorNumber);
            }

            // Надбавка ГОСТ 30494 включается СКАЧКОМ на пороге −31 °C. Рядом с ним
            // точность t наруж стоит не 4%, а около 6% — потому что вместе с ΔT
            // меняется и расчётная температура жилых комнат.
            if (ThermalConstants.IsNearColdRegionThreshold(parameters.ExternalTemperature))
            {
                bool applied = parameters.ExternalTemperature <= ThermalConstants.ColdRegionThreshold;
                Logger.Warn(
                    $"t наруж = {parameters.ExternalTemperature} °C — в {Math.Abs(parameters.ExternalTemperature - ThermalConstants.ColdRegionThreshold):F0} °C " +
                    $"от порога ГОСТ 30494 ({ThermalConstants.ColdRegionThreshold} °C). Надбавка +1 °C жилым комнатам " +
                    (applied ? "ПРИМЕНЯЕТСЯ" : "НЕ применяется") +
                    "; ошибка в один градус переключит её и сдвинет итог примерно на 6%. " +
                    "Проверьте t наруж по исходным данным площадки.");
            }
            
            // Шаг 0. Температуры неотапливаемых объёмов — балансом, а не из таблицы.
            //
            // СП 50.13330.2012 п. 5.2: расчётную температуру воздуха в остеклённой
            // лоджии «допускается принимать на основе расчета теплового баланса».
            // Значения для неё норматив не даёт вовсе, поэтому прежние +5 °C были
            // подставленным числом: при −29 °C на улице они означали, что лоджия
            // вчетверо теплее улицы (эквивалент n = 0,29). Считается по ВСЕМ
            // собранным помещениям — лоджия снята с расчёта и в selectedRooms
            // не входит, а её остекление и есть холодная сторона баланса.
            _unheatedTemperatures = UnheatedVolumes.Resolve(
                parameters.AllRooms ?? rooms,
                parameters.ExternalTemperature,
                r => GetRoomDesignTemperature(r, parameters),
                c => parameters.RoomTemperatures?.GetTemperature(c));

            LogUnheatedTemperatures(parameters);

            // Шаг 1. Трансмиссионные теплопотери и бытовые тепловыделения — покомнатно.
            var computations = new List<RoomComputation>();
            foreach (var room in selectedRooms)
            {
                try
                {
                    computations.Add(ComputeRoomTransmission(room, parameters, buildingParams));
                }
                catch (Exception ex)
                {
                    Logger.Error($"Ошибка расчёта теплопотерь для помещения {room.Name}", ex);
                    results.Add(new CalculationResult
                    {
                        RoomName = $"{room.DisplayName} (ОШИБКА)",
                        ErrorMessage = ex.Message
                    });
                }
            }

            LogReducedSummary(computations, parameters);

            // Шаг 2. Воздухообмен: на квартиру (ТЗ) либо покомнатно (старое приближение).
            AssignVentilation(computations, parameters);

            // Шаг 3. Сборка строк отчёта.
            foreach (var computation in computations)
            {
                results.Add(BuildResult(computation, parameters));
            }

            // Добавляем сводный результат
            if (results.Count > 0 && results.All(r => string.IsNullOrEmpty(r.ErrorMessage)))
            {
                results.Add(CreateSummaryResult(results, parameters));
            }
            
            return results;
        }

        /// <summary>
        /// Промежуточное состояние расчёта помещения: всё, что считается покомнатно,
        /// до распределения квартирного воздухообмена.
        /// </summary>
        private class RoomComputation
        {
            public RoomData Room;
            public double TInt;
            public double DeltaT;
            public double QOgr;
            public double QVn;
            public LossDetails Details;
            public Dictionary<string, double> Betas;
            public ReducedResistanceResult Reduced;

            /// <summary>Назначается на шаге распределения воздухообмена.</summary>
            public double QVent;

            /// <summary>Бытовые тепловыделения, вычитаемые из этой строки (доля по квартире).</summary>
            public double QVnSubtracted;

            /// <summary>Помещение получает нагрузку на нагрев воздуха: по ТЗ — только с окнами.</summary>
            public bool HasWindows =>
                Room.Windows != null && Room.Windows.Count > 0 || Room.WindowArea > 0.01;
        }

        private RoomComputation ComputeRoomTransmission(RoomData room, CalculationParameters parameters,
                                                        BuildingParameters buildingParams)
        {
            // Расчётная температура воздуха помещения по ГОСТ 30494-96 Таблица 1
            double tInt        = GetRoomDesignTemperature(room, parameters);
            double deltaT      = tInt - parameters.ExternalTemperature;
            double floorDeltaT = tInt - parameters.FloorTemperature;

            // Трансмиссионные теплопотери Qогр (внутри уже с β-коэффициентами)
            var transmission = CalculateTransmissionLoss(room, tInt, deltaT, floorDeltaT, buildingParams, parameters);

            return new RoomComputation
            {
                Room    = room,
                TInt    = tInt,
                DeltaT  = deltaT,
                QOgr    = transmission.TotalLoss,
                Details = transmission.Details,
                Betas   = transmission.BetaCoefficients,
                Reduced = transmission.Reduced,
                QVn     = CalculateInternalHeat(room, parameters)
            };
        }

        /// <summary>
        /// Распределяет нагрузку на нагрев наружного воздуха.
        ///
        /// По ТЗ норма считается НА КВАРТИРУ: L = max(Σ приток по жилым комнатам,
        /// Σ вытяжка по кухне и санузлам), а полученная величина за вычетом бытовых
        /// тепловыделений квартиры распределяется по комнатам С ОКНАМИ пропорционально
        /// их площади — потому что отопительные приборы стоят именно там.
        ///
        /// Отступление от буквы ТЗ: распределяется не готовая мощность, а РАСХОД L,
        /// после чего каждая комната греет свою долю воздуха на свою ΔT. При одинаковых
        /// tв это тождественно тексту ТЗ, а при разных (жилая 21 °C, кухня 19 °C)
        /// физически корректнее — иначе пришлось бы выбирать одну ΔT на всю квартиру,
        /// а какую именно, ТЗ не оговаривает.
        ///
        /// Помещения без номера квартиры считаются покомнатно: общедомовые лестницы
        /// и лифтовые холлы в квартирную норму не входят и идут отдельным расчётом.
        /// </summary>
        private void AssignVentilation(List<RoomComputation> computations, CalculationParameters parameters)
        {
            var perRoom = computations;

            if (parameters.Ventilation == VentilationMethod.AirChangeRate)
            {
                if (parameters.AirChangeRatePerHour > 0)
                {
                    AssignByAirChangeRate(computations, parameters);
                    return;
                }

                // Считать по кратности, не зная кратности, нельзя: любое число,
                // взятое здесь от себя, ушло бы в отчёт как расход воздуха.
                Logger.Warn(
                    "[Воздухообмен] выбран расчёт ПО КРАТНОСТИ, но кратность не задана " +
                    "(AirChangeRatePerHour = 0). Расчёт идёт по квартирной норме ТЗ. " +
                    "Кратность задаётся в файле объекта <модель>.QOVETER-узлы.json.");
            }

            if (parameters.UseApartmentGrouping ||
                parameters.Ventilation == VentilationMethod.AirChangeRate)
            {
                var apartments = computations
                    .Where(c => !string.IsNullOrWhiteSpace(c.Room.Apartment))
                    .GroupBy(c => c.Room.Apartment.Trim())
                    .ToList();

                foreach (var apartment in apartments)
                {
                    var rooms = apartment.ToList();

                    // Квартира — это ОДНА строка номера. Если в модели номера
                    // повторяются (две секции в одном файле, нумерация с начала на
                    // каждом этаже), разные квартиры сольются в одну, и
                    // L = max(Σ приток, Σ вытяжка) посчитается по объединённому
                    // набору помещений — норма выйдет заниженной, потому что max
                    // берётся один раз вместо двух. Молча этого допускать нельзя:
                    // признак слияния — помещения одной квартиры на разных уровнях.
                    var levels = rooms
                        .Select(c => c.Room.LevelId)
                        .Distinct()
                        .ToList();
                    if (levels.Count > 1)
                    {
                        Logger.Warn(
                            $"[Квартира {apartment.Key}] помещения на {levels.Count} разных уровнях " +
                            $"({string.Join(", ", rooms.Select(c => c.Room.LevelName).Distinct())}) — " +
                            "либо двухуровневая квартира, либо номера квартир в модели " +
                            "повторяются и разные квартиры слиты в одну. Проверьте параметр " +
                            "номера квартиры: воздухообмен считается на объединённый набор.");
                    }

                    AssignApartmentVentilation(apartment.Key, rooms, parameters);
                }

                perRoom = computations
                    .Where(c => string.IsNullOrWhiteSpace(c.Room.Apartment))
                    .ToList();
            }

            foreach (var computation in perRoom)
            {
                computation.QVent = ThermalConstants.AirFlowToWatts
                                  * GetAirFlowRate(computation.Room, parameters)
                                  * ThermalConstants.AirDensity
                                  * ThermalConstants.AirSpecificHeat
                                  * computation.DeltaT;
                computation.QVnSubtracted = computation.QVn;
            }
        }

        /// <summary>
        /// Воздухообмен ПО КРАТНОСТИ: L = n · V для каждого помещения.
        ///
        /// <para>Отличается от нормируемого расхода принципиально: там норма
        /// привязана к площади жилых комнат и к назначению кухни и санузла,
        /// здесь — к объёму помещения. Поэтому нагрузку получают ВСЕ помещения,
        /// а не только те, где стоят приборы: у кратности нет понятия «комната,
        /// на которую отнесена норма квартиры».</para>
        /// </summary>
        private static void AssignByAirChangeRate(List<RoomComputation> computations,
                                                  CalculationParameters parameters)
        {
            double n = parameters.AirChangeRatePerHour;
            double totalFlow = 0;

            foreach (var computation in computations)
            {
                var room = computation.Room;
                double volume = room.Volume > 0
                    ? room.Volume
                    : room.Area * (room.Height > 0 ? room.Height : 3.0);

                double airFlow = n * volume;
                totalFlow += airFlow;

                computation.QVent = ThermalConstants.AirFlowToWatts
                                  * airFlow
                                  * ThermalConstants.AirDensity
                                  * ThermalConstants.AirSpecificHeat
                                  * computation.DeltaT;
                computation.QVnSubtracted = computation.QVn;
            }

            Logger.Info(
                $"[Воздухообмен] по КРАТНОСТИ n = {n:F2} 1/ч на {computations.Count} помещений, " +
                $"суммарный расход {totalFlow:F0} м³/ч. Это НАСТРОЙКА ОБЪЕКТА: норматив " +
                "нормирует расход (приток 3 м³/(ч·м²), вытяжка кухни и санузла), " +
                "а не кратность.");
        }

        /// <summary>
        /// Помещения, по которым нормируется воздухообмен квартиры: полный состав
        /// из <see cref="CalculationParameters.ApartmentComposition"/>, если он передан,
        /// иначе — то, что пришло на расчёт.
        /// </summary>
        private static List<RoomData> ResolveApartmentComposition(
            string apartment, List<RoomComputation> rooms, CalculationParameters parameters)
        {
            var inCalculation = rooms.Select(c => c.Room).ToList();

            var all = parameters.ApartmentComposition;
            if (all == null || all.Count == 0) return inCalculation;

            var full = all
                .Where(r => !string.IsNullOrWhiteSpace(r.Apartment) &&
                            string.Equals(r.Apartment.Trim(), apartment, StringComparison.Ordinal))
                .ToList();

            if (full.Count == 0) return inCalculation;

            if (full.Count > inCalculation.Count)
            {
                var excluded = full
                    .Where(r => !inCalculation.Any(c => c.Id == r.Id))
                    .Select(r => $"{r.Number} «{r.Name}»")
                    .ToList();
                Logger.Debug(
                    $"[Квартира {apartment}] норма воздухообмена считается по полному составу " +
                    $"({full.Count} помещений); вне расчёта: {string.Join(", ", excluded)}");
            }

            return full;
        }

        private void AssignApartmentVentilation(string apartment, List<RoomComputation> rooms,
                                                CalculationParameters parameters)
        {
            // Норма считается по ПОЛНОМУ составу квартиры, а не по тому, что осталось
            // в расчёте: санузел, снятый инженером с расчёта, из квартиры никуда
            // не делся и продолжает определять вытяжную норму. Раньше снятая галочка
            // уменьшала L и молча срезала мощность у остальных комнат этой квартиры.
            var normRooms = ResolveApartmentComposition(apartment, rooms, parameters);

            double supply = normRooms
                .Where(r => ThermalConstants.SupplyRatedCategories.Contains(r.Category))
                .Sum(r => r.Area * ThermalConstants.LivingRoomAirFlow);

            double exhaust = normRooms
                .Where(r => !ThermalConstants.SupplyRatedCategories.Contains(r.Category))
                .Sum(r => GetAirFlowRate(r, parameters));

            double airFlow = Math.Max(supply, exhaust);
            double apartmentInternalHeat = rooms.Sum(c => c.QVn);

            // Приборы стоят у окон — туда и уходит нагрузка. Если окон в квартире нет
            // вообще (внутренняя квартира-студия без остекления — в жилых домах не бывает,
            // но модель может быть неполной), раскидываем по всем помещениям, чтобы
            // мощность не потерялась молча.
            var receivers = rooms.Where(c => c.HasWindows).ToList();
            if (receivers.Count == 0)
            {
                Logger.Debug($"[Квартира {apartment}] Нет помещений с окнами — " +
                             "нагрузка на нагрев воздуха распределена по всем помещениям");
                receivers = rooms;
            }

            double totalArea = receivers.Sum(c => c.Room.Area);
            if (totalArea <= 0)
            {
                Logger.Debug($"[Квартира {apartment}] Нулевая площадь помещений-приёмников — " +
                             "нагрузка на нагрев воздуха не распределена");
                return;
            }

            foreach (var computation in receivers)
            {
                double share = computation.Room.Area / totalArea;
                computation.QVent = ThermalConstants.AirFlowToWatts
                                  * (airFlow * share)
                                  * ThermalConstants.AirDensity
                                  * ThermalConstants.AirSpecificHeat
                                  * computation.DeltaT;
                computation.QVnSubtracted = apartmentInternalHeat * share;
            }

            Logger.Debug($"[Квартира {apartment}] помещений {rooms.Count}, " +
                         $"приток {supply:F1} м³/ч, вытяжка {exhaust:F1} м³/ч, " +
                         $"принято L = {airFlow:F1} м³/ч на {receivers.Count} комнат с окнами");
        }

        private CalculationResult BuildResult(RoomComputation computation, CalculationParameters parameters)
        {
            var room = computation.Room;

            // Q_расч по ТЗ, формула (1): Q_огр + Q_инф/вент − Q_вн.
            // Q_вн вычитается, только если включён режим SubtractInternalHeatGains
            // (по умолчанию off — для подбора отопительных приборов берём без вычета,
            // запас идёт через SafetyFactor).
            double qTotal = computation.QOgr + computation.QVent;
            if (parameters.SubtractInternalHeatGains)
                qTotal -= computation.QVnSubtracted;

            // Запас: Qфинал = Qитого × SafetyFactor (если включён)
            double qFinal = parameters.UseSafetyFactor
                ? qTotal * parameters.SafetyFactor
                : qTotal;

            double extWindowsArea = room.Windows.Sum(w => w.Area);

            return new CalculationResult
            {
                RoomName = room.DisplayName,
                Apartment = room.Apartment ?? string.Empty,
                Q_ogr    = Math.Round(computation.QOgr,  1),
                Q_vent   = Math.Round(computation.QVent, 1),
                // Инфильтрация. Решение принято 2026-08-04 по ТЗ: формула (1) даёт единый
                // член Qинф/вент по расходу L = max(приток, вытяжка). Отдельного слагаемого
                // в ТЗ нет — в отчётах колонки «Q инф» больше нет, а CalculateInfiltrationLoss
                // остаётся проверочным методом под тестом (СП 50.13330 п. 9.2).
                Q_inf    = 0,
                Q_vn     = Math.Round(computation.QVn,   1),
                Q_total  = Math.Round(qTotal, 1),
                Q_final  = Math.Round(qFinal, 1),
                Q_walls   = Math.Round(computation.Details.WallLoss,   1),
                Q_windows = Math.Round(computation.Details.WindowLoss, 1),
                Q_doors   = Math.Round(computation.Details.DoorLoss,   1),
                Q_floor   = Math.Round(computation.Details.FloorLoss,  1),
                Q_roof    = Math.Round(computation.Details.RoofLoss,   1),
                BetaCoefficients = computation.Betas,
                BetaDetails      = computation.Betas,
                R_conditional = Math.Round(computation.Reduced?.ConditionalR ?? 0, 3),
                R_reduced     = Math.Round(computation.Reduced?.ReducedR     ?? 0, 3),
                Homogeneity   = Math.Round(computation.Reduced?.Homogeneity  ?? 1, 3),
                IsReducedProvisional = computation.Reduced?.IsProvisional ?? false,
                BridgeNodesCounted   = computation.Reduced?.Nodes.Count ?? 0,
                BridgeNodesSkipped   = computation.Reduced?.SkippedNodes.Count ?? 0,
                WallConstructionName = computation.Reduced?.Construction.ToString(),
                IsWallConstructionAssumed =
                    computation.Reduced?.ConstructionOrigin == WallConstructionOrigin.DominantOfObject ||
                    computation.Reduced?.ConstructionOrigin == WallConstructionOrigin.NonStreetEnclosure,
                HasEnclosures        = computation.Reduced?.HasEnclosures ?? false,
                AnchorU              = computation.Reduced?.AnchorU ?? 0,
                Orientation  = room.Orientation,
                RoomData     = room,
                Area         = room.Area,
                WallArea     = room.WallArea,
                WindowArea   = room.WindowArea,
                DoorArea     = room.DoorArea,
                FloorNumber  = room.FloorNumber,
                ExtWallsArea   = Math.Round(room.WallArea,  2),
                ExtWindowsArea = Math.Round(extWindowsArea, 2)
            };
        }


        /// <summary>
        /// Сводка по мостикам холода за прогон.
        ///
        /// <para><b>Зачем отдельной строкой.</b> Приведение включено по умолчанию,
        /// и вопрос «посчиталось ли оно на самом деле» решается ровно тремя числами:
        /// какой тип конструкции распознан, сколько узлов взято из СП 230 и сколько
        /// пропущено за отсутствием таблицы. Без сводки ответ пришлось бы собирать
        /// подсчётом строк в журнале на сотни тысяч записей — а это уже стоило
        /// одного прогона Revit (см. историю пробы фасада).</para>
        ///
        /// <para><b>Как читать.</b> Конструкция <c>Unknown</c> означает, что слои
        /// ограждения не опознались и ни одна таблица приложения Г не подобрана:
        /// r останется единицей, мостики в итог не войдут. Ненулевое «пропущено» —
        /// названный недолёт: узел геометрия нашла, а норматив для него молчит.</para>
        /// </summary>
        private static void LogReducedSummary(List<RoomComputation> computations,
                                              CalculationParameters parameters)
        {
            if (parameters.ReducedResistance == ReducedResistanceMode.Off)
            {
                Logger.Info("[Мостики] приведение выключено: считается R УСЛОВНОЕ. " +
                            "Норматив требует приведённого — итог занижен.");
                return;
            }

            var rooms = computations
                .Where(c => c.Reduced != null && c.Reduced.ConditionalU > 0)
                .ToList();
            if (rooms.Count == 0) return;

            if (parameters.ReducedResistance == ReducedResistanceMode.HomogeneityFactor)
            {
                Logger.Info($"[Мостики] режим коэффициента однородности: r = {parameters.HomogeneityFactor:F2} " +
                            $"на {rooms.Count} помещениях. Число задал инженер, СП 230 не запрашивался.");
                return;
            }

            // Средний r по дому — взвешенный по U·A, а не арифметический: комнату
            // с одной стеной и угловую нельзя складывать с равным весом.
            double sumConditional = 0, sumReduced = 0;
            foreach (var c in rooms)
            {
                double area = c.Room.WallArea > 0
                    ? c.Room.WallArea
                    : c.Room.Walls?.Where(w => w.IsExternal).Sum(w => w.Area) ?? 0;
                sumConditional += c.Reduced.ConditionalU * area;
                sumReduced     += c.Reduced.ReducedU     * area;
            }
            double rWeighted = sumReduced > 0 ? sumConditional / sumReduced : 1.0;

            int counted = rooms.Sum(c => c.Reduced.Nodes.Count);
            int skipped = rooms.Sum(c => c.Reduced.SkippedNodes.Count);

            Logger.Info(
                $"[Мостики] помещений {rooms.Count}, узлов по СП 230 учтено {counted}, " +
                $"пропущено {skipped}. Средний r по дому = {rWeighted:F3} " +
                $"(U_пр выше U_усл на {(rWeighted > 0 ? (1 / rWeighted - 1) * 100 : 0):F1}%).");

            // Помещение без наружных ограждений узлов не имеет ПО ПОСТРОЕНИЮ. До
            // 2026-08-20 оно попадало в ту же строку «конструкция Unknown», что
            // и помещение с неопознанным ограждением, и сводка одинаково называла
            // отсутствие недолёта и недолёт.
            int noEnclosures = rooms.Count(c => !c.Reduced.HasEnclosures);
            if (noEnclosures > 0)
            {
                Logger.Info($"    без наружных ограждений: помещений {noEnclosures} — " +
                            "узлов у них нет по построению, это не недолёт расчёта");
            }

            foreach (var group in rooms.Where(c => c.Reduced.HasEnclosures)
                                       .GroupBy(c => new
                                       {
                                           c.Reduced.Construction,
                                           c.Reduced.ConstructionOrigin
                                       })
                                       .OrderByDescending(g => g.Count()))
            {
                Logger.Info($"    конструкция {group.Key.Construction} " +
                            $"({DescribeOrigin(group.Key.ConstructionOrigin)}): " +
                            $"помещений {group.Count()}, " +
                            $"узлов {group.Sum(c => c.Reduced.Nodes.Count)}, " +
                            $"пропущено {group.Sum(c => c.Reduced.SkippedNodes.Count)}");
            }

            // Группировка по (узел, вариант): выпуклый и вогнутый углы — РАЗНЫЕ
            // строки таблиц СП 230 с разным знаком Ψ. В одной строке сводки их
            // сумма и диапазон Ψ читались бы как ошибка каталога.
            foreach (var group in rooms.SelectMany(c => c.Reduced.Nodes)
                                       .GroupBy(n => new { n.Type, n.Variant })
                                       .OrderByDescending(g => Math.Abs(g.Sum(n => n.Loss))))
            {
                string variant = group.Key.Type == ThermalBridgeType.ExternalCorner
                    ? (group.Key.Variant == BridgeVariant.Concave ? " вогнутый" : " выпуклый")
                    : "";
                Logger.Info($"    узел {group.Key.Type}{variant}: {group.Count()} шт, " +
                            $"Σ(Ψ·l) = {group.Sum(n => n.Loss):F1} Вт/К, " +
                            $"Ψ = {group.Min(n => n.Psi):F3}…{group.Max(n => n.Psi):F3} " +
                            $"({group.First().Reference}, исполнение «{group.First().Execution}»)");
            }

            // Исполнение узла из модели Revit не вытаскивается: положение рамы
            // относительно утеплителя, перфорация плиты, нахлёст — это чертёж узла,
            // а не геометрия здания. Где инженер его не задал, берётся ХУДШЕЕ по
            // потерям — и об этом обязана быть строка, потому что цена допущения
            // велика: у оконного узла СФТК Ψ = 0,092 при раме у утеплителя (Г.33)
            // против 0,433 при раме, смещённой от него (Г.35, по примечанию СП
            // худший вариант). Задаётся в CalculationParameters.NodeDetails.Execution.
            int assumed = rooms.SelectMany(c => c.Reduced.Nodes).Count(n => n.IsExecutionAssumed);
            if (assumed > 0)
            {
                Logger.Warn(
                    $"    исполнение узла не задано у {assumed} узлов — принято ХУДШЕЕ по потерям " +
                    "(оценка в запас). Знаете свой узел — укажите его в настройках расчёта: " +
                    "«FrameAtInsulation», «FrameShiftedIntoInsulation», «ThermalInsert» и т. п.");
            }

            foreach (var group in rooms.SelectMany(c => c.Reduced.SkippedNodes)
                                       .GroupBy(t => t)
                                       .OrderByDescending(g => g.Count()))
            {
                Logger.Warn($"    узел {group.Key} НЕ УЧТЁН у {group.Count()} помещений — " +
                            "таблицы СП 230 для их конструкции нет, теплопотери занижены");
            }

            // ── Тарельчатые анкеры (СП 230 таблица Г.4) ─────────────────────────
            //
            // Точечный элемент: потери на штуку, а не на метр. Плотность крепежа
            // из модели не вытаскивается — это раскладка дюбелей в проекте фасада.
            // Не задана — не считаем и ГОВОРИМ об этом: подставить «типовые 5 шт/м²»
            // значит выдумать число, а цена его заметна (6 анкеров с χ = 0,004 дают
            // 0,024 Вт/(м²·К), около 8% к U = 0,316).
            var withAnchors = rooms.Where(c => c.Reduced.AnchorU > 0).ToList();
            if (withAnchors.Count > 0)
            {
                var first = withAnchors[0].Reduced;
                Logger.Info(
                    $"    анкеры: {first.AnchorsPerM2:F1} шт/м², χ = {first.AnchorChi:F4} Вт/°С " +
                    $"({SP230Catalog.AnchorReference}) → +{first.AnchorU:F3} Вт/(м²·К) " +
                    $"к U у {withAnchors.Count} помещений");
            }
            else
            {
                Logger.Info(
                    "    анкеры фасада НЕ УЧТЕНЫ: плотность крепежа (шт/м²) не задана. " +
                    "СП 230 таблица Г.4 считает их отдельным точечным элементом, в Ψ угла " +
                    "они не входят. Теплопотери на этом занижены. Задаётся полем " +
                    "AnchorsPerM2 в файле объекта <модель>.QOVETER-узлы.json.");
            }

            // ── Мостики ПОЭТАЖНО ────────────────────────────────────────────────
            //
            // Зачем. Первый и последний этажи расходятся с расчётом проектировщика
            // сильнее типовых — это стояло в плане отдельным хвостом и разбиралось
            // разбором журнала. Причина видна сразу, если разложить нагрузку узлов
            // по этажам: цокольный узел (Г.39–Г.40) и парапет (Г.41–Г.52) есть
            // ТОЛЬКО у них, и Ψ там самые большие в каталоге — 0,45…0,71 и 0,55…0,84
            // против 0,10…0,28 у оконного откоса. Плюс оба узла зависят от признаков,
            // которых в модели нет (утепление плиты цоколя, утепление парапета):
            // не заданы — берётся ХУДШЕЕ, и вся цена этого допущения ложится
            // на два этажа из пятнадцати.
            var byFloor = rooms
                .Where(c => c.Reduced.Nodes.Count > 0)
                .GroupBy(c => c.Room.FloorNumber)
                .OrderBy(g => g.Key)
                .ToList();

            if (byFloor.Count > 1)
            {
                Logger.Info("    нагрузка узлов по этажам (Σ(Ψ·l), Вт/К — и какие узлы только здесь):");
                foreach (var floor in byFloor)
                {
                    var nodes = floor.SelectMany(c => c.Reduced.Nodes).ToList();
                    string onlyHere = string.Join(", ", nodes
                        .Where(n => n.Type == ThermalBridgeType.BaseJunction ||
                                    n.Type == ThermalBridgeType.Parapet)
                        .GroupBy(n => n.Type)
                        .Select(g => $"{g.Key} {g.Sum(n => n.Loss):F1}"));

                    Logger.Info($"      этаж {floor.Key}: помещений {floor.Count()}, " +
                                $"Σ(Ψ·l) = {nodes.Sum(n => n.Loss):F1}" +
                                (string.IsNullOrEmpty(onlyHere) ? "" : $"  ← {onlyHere}"));
                }
            }

            // ── Сторож на неправдоподобный r ────────────────────────────────────
            //
            // Типовой диапазон r для многослойных стен — 0,7…0,92. Значение ниже
            // означает, что узлы съедают больше трети сопротивления стены, а это
            // для современного фасада физически неправдоподобно и всегда указывает
            // на одно из двух: длина узла посчитана не по той стене либо площадь
            // стены в знаменателе мала (маленькая комната с большим окном).
            // Сторож не правит число — он не даёт ему пройти молча.
            var implausible = rooms
                .Where(c => c.Reduced.Nodes.Count > 0 &&
                            c.Reduced.Homogeneity > 0 && c.Reduced.Homogeneity < 0.6)
                .OrderBy(c => c.Reduced.Homogeneity)
                .ToList();
            if (implausible.Count > 0)
            {
                Logger.Warn(
                    $"    у {implausible.Count} помещений r ниже 0,60 при типовом диапазоне " +
                    "0,70…0,92 — узлы съедают больше трети сопротивления стены. " +
                    "Проверьте площадь наружных стен и длины узлов у этих помещений: " +
                    string.Join("; ", implausible.Take(5).Select(c =>
                        $"«{c.Room.Name}» r={c.Reduced.Homogeneity:F3} S_ст={c.Room.WallArea:F1} м²")) +
                    (implausible.Count > 5 ? " …" : ""));
            }

            // Конструкция, принятая допущением, — отдельной строкой. Число из
            // прошлой строки («помещений N») этого не покажет: там она стоит
            // наравне с прочитанной из модели, а цена у них разная.
            int assumedConstruction = rooms.Count(c =>
                c.Reduced.HasEnclosures &&
                (c.Reduced.ConstructionOrigin == WallConstructionOrigin.DominantOfObject ||
                 c.Reduced.ConstructionOrigin == WallConstructionOrigin.NonStreetEnclosure));
            if (assumedConstruction > 0)
            {
                Logger.Info(
                    $"    у {assumedConstruction} помещений конструкция ПРИНЯТА, а не прочитана " +
                    "из модели (преобладающая по объекту либо ограждение не на улицу) — " +
                    "Ψ у них взяты по таблицам этой конструкции.");
            }
        }

        /// <summary>Как называется источник конструкции в сводке прогона.</summary>
        private static string DescribeOrigin(WallConstructionOrigin origin)
        {
            switch (origin)
            {
                case WallConstructionOrigin.NormativeInsulation:
                    return "утеплитель достроен по нормируемому R";
                case WallConstructionOrigin.NonStreetEnclosure:
                    return "уличных стен нет, взято ограждение к лоджии/шахте";
                case WallConstructionOrigin.DominantOfObject:
                    return "ПРИНЯТА преобладающая по объекту";
                default:
                    return "по слоям модели";
            }
        }

        private (double TotalLoss, LossDetails Details, Dictionary<string, double> BetaCoefficients,
                 ReducedResistanceResult Reduced)
            CalculateTransmissionLoss(RoomData room, double tInt, double deltaT, double floorDeltaT,
                                      BuildingParameters buildingParams, CalculationParameters parameters)
        {
            var details = new LossDetails();
            var betaCoefficients = CalculateBetaCoefficients(room, buildingParams);
            double betaSum = betaCoefficients.Values.Sum();

            // 1. Теплопотери через наружные стены.
            //    Сначала определяем U УСЛОВНОЕ (одномерный слоёный пирог), затем —
            //    если включено — приводим его к U ПРИВЕДЁННОМУ с учётом мостиков.
            double wallArea = room.WallArea > 0
                ? room.WallArea
                : room.Walls?.Where(w => w.IsExternal).Sum(w => w.Area) ?? 0;

            // Заглублённая часть стен уходит из обычного счёта: она считается
            // зональным методом (СП 50.13330.2024 Г.7) по температуре наружного
            // воздуха, но со своими сопротивлениями зон. Посчитать её здесь ЖЕ
            // при полной ΔT и без грунта — ровно та ошибка, из-за которой подвал
            // пришлось снять с расчёта целиком.
            double buriedWallArea = room.Walls?
                .Where(w => w.IsExternal && w.BuriedFraction > 0)
                .Sum(w => w.Area * w.BuriedFraction) ?? 0;

            wallArea = Math.Max(0, wallArea - buriedWallArea);

            double conditionalU = GetConditionalWallU(room, wallArea);
            var reduced = _reducedResistance.Calculate(
                room, conditionalU, parameters.ReducedResistance,
                parameters.HomogeneityFactor, buildingParams.FloorHeight,
                room.WallConstruction, parameters.NodeDetails);

            if (wallArea > 0 && reduced.ReducedU > 0)
            {
                // Σ(U·A·ΔT) по каждой стене со СВОЕЙ разностью температур: за стеной
                // может быть не улица, а лоджия (5 °C) или лестничная клетка (16 °C).
                // Приведение (мостики) — множитель на всю сумму: узлы считаются
                // по геометрии помещения целиком, разложить их по стенам нельзя.
                double reductionFactor = conditionalU > 0 ? reduced.ReducedU / conditionalU : 1.0;
                double wallUADeltaT = WallUADeltaT(room, tInt, deltaT, wallArea, conditionalU, parameters);

                details.WallLoss = wallUADeltaT * reductionFactor * (1 + betaSum);

                Logger.Debug(
                    $"[Стены] {room.Name}: S={wallArea:F2}м² U_усл={conditionalU:F3} " +
                    $"U_пр={reduced.ReducedU:F3} ΔT={deltaT} β={betaSum:F2} → Q={details.WallLoss:F1}Вт");
            }

            // 1а. Заглублённые стены — зональный метод СП 50.13330.2024 Г.7.
            //     Надбавки β к ним не применяются: они про обдув и инсоляцию,
            //     а стена в грунте не обдувается и не освещается.
            if (room.GroundWallZones != null && room.GroundWallZones.Count > 0)
            {
                double buriedLoss = GroundContact.HeatLossW(
                    GroundEnclosure.Wall, room.GroundWallZones, deltaT,
                    GroundContact.BaseSoilConductivity, room.GroundWallResistance);

                details.WallLoss += buriedLoss;

                Logger.Debug(
                    $"[Грунт] {room.Name}: стены в грунте — " +
                    GroundContact.Describe(GroundEnclosure.Wall, room.GroundWallZones,
                                           GroundContact.BaseSoilConductivity, room.GroundWallResistance) +
                    $", R конструкции {room.GroundWallResistance:F2} → Q={buriedLoss:F1} Вт");
            }

            // 2. Теплопотери через окна
            //    Коэффициент β применяется к окнам так же, как к стенам (методика Audytor CO)
            foreach (var window in room.Windows)
            {
                double windowDeltaT = SurfaceDeltaT(window, tInt, deltaT, room, parameters);
                details.WindowLoss += window.CalculateHeatLoss(windowDeltaT) * (1 + betaSum);
            }

            // 3. Теплопотери через наружные двери
            foreach (var door in room.Doors.Where(d => d.IsExternal))
            {
                double doorDeltaT = SurfaceDeltaT(door, tInt, deltaT, room, parameters);
                // Для двери берём ОБЩУЮ высоту здания (β = k·H_здания, не H_этажа)
                double doorBeta = door.CalculateDoorBetaCoefficient(buildingParams.TotalHeight, buildingParams.DoorBetaCoefficients);
                details.DoorLoss += door.CalculateHeatLoss(doorDeltaT) * (1 + betaSum + doorBeta);

                if (doorBeta > 0)
                    betaCoefficients["Наружная дверь"] = doorBeta;
            }

            // 4. Теплопотери через пол.
            //
            //    Пол НА ГРУНТЕ считается зональным методом СП 50.13330.2024 Г.7:
            //    полосами по 2 м от контура здания, каждая со своим сопротивлением.
            //    Разность температур — до НАРУЖНОГО ВОЗДУХА: демпфирование грунта
            //    уже сидит в сопротивлениях зон, второй раз его учитывать нельзя.
            //
            //    Прежний путь (U пола × площадь × ΔT до подставленных 5 °C) остаётся
            //    для пола над НЕотапливаемым подвалом — там за полом действительно
            //    воздух, а не грунт. Само число 5 °C по-прежнему настройка, а не
            //    норматив, и это отдельная работа.
            if (room.GroundFloorZones != null && room.GroundFloorZones.Count > 0)
            {
                double floorR = GetFloorLayersR(room);

                details.FloorLoss = GroundContact.HeatLossW(
                    GroundEnclosure.Floor, room.GroundFloorZones, deltaT,
                    GroundContact.BaseSoilConductivity, floorR);

                Logger.Debug(
                    $"[Грунт] {room.Name}: пол по грунту — " +
                    GroundContact.Describe(GroundEnclosure.Floor, room.GroundFloorZones,
                                           GroundContact.BaseSoilConductivity, floorR) +
                    $", R конструкции {floorR:F2} → Q={details.FloorLoss:F1} Вт");
            }
            else if (room.IsFirstFloor)
            {
                double kFloor = GetFloorUValue(room);
                details.FloorLoss = kFloor * room.Area * floorDeltaT;
            }

            // 5. Теплопотери через потолок/крышу последнего этажа
            if (room.IsLastFloor)
            {
                double kRoof = GetRoofUValue(room);
                details.RoofLoss = kRoof * room.Area * deltaT;
            }

            double totalLoss = details.WallLoss + details.WindowLoss + details.DoorLoss + details.FloorLoss + details.RoofLoss;

            return (totalLoss, details, betaCoefficients, reduced);
        }

        /// <summary>
        /// Σ(U·A·ΔT) по наружным ограждениям помещения, Вт — до приведения и надбавок.
        ///
        /// Стены с разной ΔT нельзя складывать по площади: ограждение на лоджию
        /// (5 °C) теряет втрое меньше такого же ограждения на улицу (−29 °C).
        /// Если реальных стен в помещении нет (старые фикстуры v1/v2 их не возят),
        /// откатываемся к прежнему поведению — вся площадь по наружной ΔT.
        /// </summary>
        private double WallUADeltaT(RoomData room, double tInt, double outdoorDeltaT,
                                    double wallArea, double conditionalU,
                                    CalculationParameters parameters)
        {
            var external = room.Walls?.Where(w => w.IsExternal).ToList();
            if (external == null || external.Count == 0) return wallArea * conditionalU * outdoorDeltaT;

            // Считается только НАДЗЕМНАЯ часть: заглублённая посчитана зонами.
            double sumArea = external.Sum(w => w.Area * (1 - Clamp01(w.BuriedFraction)));
            if (sumArea <= 0) return 0;

            double sum = 0;
            foreach (var wall in external)
            {
                double u = wall.UValue > 0 ? wall.UValue : conditionalU;
                double area = wall.Area * (1 - Clamp01(wall.BuriedFraction));
                if (area <= 0) continue;

                sum += u * area * SurfaceDeltaT(wall, tInt, outdoorDeltaT, room, parameters);
            }

            // room.WallArea — та же сумма нетто-площадей, что и Σ w.Area; расхождение
            // возможно только у самодельных фикстур, и тогда масштабируем к отчётной.
            if (Math.Abs(sumArea - wallArea) > 0.01 && sumArea > 0)
                sum *= wallArea / sumArea;

            return sum;
        }

        private static double Clamp01(double value) =>
            value < 0 ? 0 : (value > 1 ? 1 : value);

        /// <summary>
        /// Расчётная разность температур для ограждения, К.
        /// <paramref name="adjacent"/> = null — наружный воздух; иначе НЕотапливаемое
        /// помещение, температура которого берётся из той же таблицы, что и tв
        /// самих помещений (лоджия 5 °C, лестница и тамбур 16 °C).
        ///
        /// Если температура соседа не ниже расчётной температуры помещения, ΔT = 0:
        /// отрицательных теплопотерь у ограждающей конструкции быть не должно,
        /// а «подогрев от соседа» в расчёте отопления не учитывают.
        /// </summary>
        private double SurfaceDeltaT(IAdjacentSurface surface, double tInt, double outdoorDeltaT,
                                     RoomData room, CalculationParameters parameters)
        {
            var adjacent = surface?.AdjacentCategory;
            if (!adjacent.HasValue) return outdoorDeltaT;

            // Приоритет: посчитанная балансом температура ЭТОГО соседнего объёма →
            // таблица по категории → наружный воздух. Баланс точнее таблицы даже
            // внутри одной категории: угловая лоджия с двумя стеклянными сторонами
            // и лоджия в нише — это разные температуры, а категория у них одна.
            double? tAdjacent = null;
            UnheatedRoomTemperature computed;
            if (surface.AdjacentRoomId > 0 && _unheatedTemperatures != null &&
                _unheatedTemperatures.TryGetValue(surface.AdjacentRoomId, out computed))
            {
                tAdjacent = computed.Temperature;
            }

            if (!tAdjacent.HasValue)
                tAdjacent = parameters.RoomTemperatures?.GetTemperature(adjacent.Value);

            if (!tAdjacent.HasValue)
            {
                Logger.Debug(
                    $"[Ограждение] {room.Name}: за стеной {adjacent.Value}, но расчётной " +
                    "температуры для этой категории в таблице нет — принята наружная");
                return outdoorDeltaT;
            }

            return Math.Max(0, tInt - tAdjacent.Value);
        }

        /// <summary>
        /// R слоёв пола, м²·°С/Вт — слагаемое конструкции в формуле (Г.16).
        /// Без документа Revit конструкцию не прочитать: тогда ноль, то есть
        /// голая зона и потери в запас.
        /// </summary>
        private double GetFloorLayersR(RoomData room)
        {
            return _floorRoofCalculator?.GetFloorLayersR(room) ?? 0;
        }

        /// <summary>Температуры неотапливаемых объёмов текущего расчёта, ключ — Id помещения.</summary>
        private Dictionary<int, UnheatedRoomTemperature> _unheatedTemperatures;

        /// <summary>
        /// Что получилось у баланса — для отчёта. Это ВХОД расчёта наравне с t наруж,
        /// и он обязан быть виден на бумаге: за температурой лоджии стоит расчёт
        /// по СП 50.13330 п. 5.2, а не число из таблицы, и инженер должен различать
        /// эти два случая.
        /// </summary>
        public List<UnheatedRoomTemperature> UnheatedTemperatures =>
            _unheatedTemperatures?.Values.ToList() ?? new List<UnheatedRoomTemperature>();

        /// <summary>
        /// Посчитанные температуры — в журнал поимённо. Это ВХОДНЫЕ данные расчёта
        /// не хуже t наруж: ошибка в них масштабируется на все ограждения к лоджиям
        /// (на 76-СУЗДАЛ.23 это 1 252 м²), а проверить их можно только глазами
        /// инженера.
        /// </summary>
        private void LogUnheatedTemperatures(CalculationParameters parameters)
        {
            if (_unheatedTemperatures == null || _unheatedTemperatures.Count == 0) return;

            var byBalance = _unheatedTemperatures.Values.Where(v => v.FromBalance).ToList();
            var byTable   = _unheatedTemperatures.Values.Where(v => !v.FromBalance).ToList();

            if (byBalance.Count > 0)
            {
                Logger.Info(
                    $"[Баланс] температура неотапливаемых объёмов посчитана для {byBalance.Count} " +
                    $"помещений (СП 50.13330 п. 5.2): от {byBalance.Min(v => v.Temperature):F1} " +
                    $"до {byBalance.Max(v => v.Temperature):F1} °C, " +
                    $"среднее {byBalance.Average(v => v.Temperature):F1} °C при t наруж " +
                    $"{parameters.ExternalTemperature:F0} °C. Воздухообмен самих объёмов " +
                    "в баланс не входит — значит это ВЕРХНЯЯ оценка температуры и нижняя оценка потерь.");

                foreach (var v in byBalance.OrderBy(v => v.Temperature).Take(40))
                {
                    Logger.Debug(
                        $"  [Баланс] {v.RoomName} ({v.Category}): {v.Temperature:F1} °C, " +
                        $"тёплая сторона {v.UAWarm:F1} Вт/К, холодная {v.UACold:F1} Вт/К — {v.Note}");
                }
            }

            if (byTable.Count > 0)
            {
                Logger.Info(
                    $"[Баланс] ещё для {byTable.Count} неотапливаемых помещений баланс не собрался — " +
                    "принята температура из таблицы. Причина по каждому — строкой ниже в журнале.");
                foreach (var v in byTable.Take(20))
                    Logger.Debug($"  [Баланс] {v.RoomName} ({v.Category}): {v.Temperature:F1} °C — {v.Note}");
            }
        }

        /// <summary>
        /// U УСЛОВНОЕ наружных стен помещения, Вт/(м²·К) — одномерный слоёный пирог.
        /// Приоритет: реальные стены из модели (средневзвешенное по площади) →
        /// восстановление из 1/U_avg → типовое U кирпичной стены 510 мм по СП 50.13330.
        /// </summary>
        private static double GetConditionalWallU(RoomData room, double wallArea)
        {
            var external = room.Walls?.Where(w => w.IsExternal).ToList();
            if (external != null && external.Count > 0 && wallArea > 0)
            {
                double weighted = external.Sum(w => w.UValue * w.Area);
                if (weighted > 0) return weighted / external.Sum(w => w.Area);
            }

            if (room.AverageInverseUValue > 0) return 1.0 / room.AverageInverseUValue;

            return ThermalConstants.WallUDefault;
        }

        private Dictionary<string, double> CalculateBetaCoefficients(RoomData room, BuildingParameters buildingParams)
        {
            var coefficients = new Dictionary<string, double>();

            // 1. Коэффициент ориентации.
            //    Ручной режим: ориентация уровня, к которому относится помещение;
            //    если для уровня не задана — общая по зданию (ключ 0).
            string orientation = room.Orientation ?? "Север";
            if (buildingParams.IsManualOrientation)
            {
                orientation = buildingParams.GetOrientation(room.LevelId, orientation);
            }

            if (ThermalConstants.OrientationBeta.TryGetValue(orientation, out double betaOrientation))
            {
                coefficients.Add($"Ориентация ({orientation})", betaOrientation);
            }

            // По ТЗ: угловые помещения (КРОМЕ жилых) с 2+ наружными стенами получают
            // β = 0.05. Надбавка ОДНА — и вот почему это надо помнить.
            //
            // Здесь их было две: «Угловое помещение» по room.IsCorner и «Много наружных
            // стен» по room.NumberOfExternalWalls > 1, обе по 0.05. Пока IsCorner считался
            // ОТДЕЛЬНОЙ эвристикой, условия могли разойтись, и две записи выглядели
            // осмысленно. С 2026-08-06 IsCorner ВЫВОДИТСЯ из числа наружных стен
            // (GeometryCollector.CalculateRoomAreasFromBoundary) — условия стали
            // тождественными, и каждое нежилое угловое помещение получало 0.10 вместо 0.05.
            // На 76-СУЗДАЛ.23 это 167 строк журнала с β = 0,20 при арифметическом
            // максимуме 0,15; Q_огр таких помещений был завышен на 4,3%.
            //
            // Если когда-нибудь понадобится РАЗДЕЛИТЬ «угол» и «много наружных стен» —
            // сначала развести условия, а потом уже добавлять вторую запись.
            bool isResidential = ThermalConstants.ResidentialCategories.Contains(room.Category);

            if (room.IsCorner && !isResidential)
                coefficients.Add("Угловое помещение", ThermalConstants.BetaCornerRoom);

            if (room.Height > ThermalConstants.HighRoomThresholdM)
                coefficients.Add("Высокое помещение", ThermalConstants.BetaHighRoom);

            return coefficients;
        }

        /// <summary>
        /// Расчётная температура по типу помещения.
        /// Источники:
        ///   ГОСТ 30494-2011 Таблица 1 — нижняя граница оптимальной, холодный период
        ///   Audytor CO — справочная таблица типов помещений
        ///   СП 54.13330.2022 — жилые здания
        /// </summary>
        private double GetRoomDesignTemperature(RoomData room, CalculationParameters parameters)
        {
            // Таблица температур ключуется КАТЕГОРИЕЙ. Разбор имени здесь был
            // последним местом, где строка решала нормативный вопрос: «Лифтовой холл»
            // попадал на ключ «холл» раньше, чем на «лифтов», а «Нежилое помещение» —
            // на «жил». Категория определяется один раз, в классификаторе.
            var manualTemp = parameters.RoomTemperatures?.GetTemperature(room.Category);

            if (manualTemp.HasValue)
            {
                // ГОСТ 30494-96 Таблица 1: в районах с t_н ≤ −31 °C оптимальная температура
                // ЖИЛОЙ КОМНАТЫ поднимается с 20…22 до 21…23 °C. Кухня, санузел и подсобные
                // помещения этой надбавки не получают — у них своя строка таблицы.
                // Категория берётся из RoomCategory, разбора строк здесь больше нет.
                if (parameters.ExternalTemperature <= ThermalConstants.ColdRegionThreshold &&
                    ThermalConstants.LivingRoomCategories.Contains(room.Category))
                {
                    return manualTemp.Value + 1.0;
                }

                return manualTemp.Value;
            }

            // Если не найдено в таблице, используем глобальную температуру из UI
            return parameters.InternalTemperature;
        }

        /// <summary>
        /// Норма расхода воздуха ОДНОГО помещения L, м³/ч — по категории, без разбора строк.
        /// Нормы — в <see cref="ThermalConstants"/> со ссылками на ТЗ и СП 54.13330.2022 Т. 9.1:
        /// жилые — приток 3 м³/(ч·м²); кухня — 60 (эл.) / 100 (газ); ванная и совмещённый
        /// санузел — 50; раздельный санузел, туалет, постирочная — 25; прочие — кратность 0.5.
        ///
        /// Для квартиры эти величины не складываются, а сравниваются:
        /// L_кв = max(Σ приток, Σ вытяжка) — см. <see cref="AssignApartmentVentilation"/>.
        /// Покомнатно норма применяется только для помещений без номера квартиры
        /// (общедомовых) или при выключенной квартирной группировке.
        ///
        /// Qвент [Вт] = K · L · ρ · c · ΔT, где K = 1/3.6 (кДж/ч → Вт).
        /// </summary>
        internal double GetAirFlowRate(RoomData room, CalculationParameters parameters)
        {
            if (room.Category == RoomCategory.Kitchen)
            {
                return parameters.GasStoves
                    ? ThermalConstants.KitchenGasExhaust
                    : ThermalConstants.KitchenElectricExhaust;
            }

            double exhaust;
            if (ThermalConstants.ExhaustRateByCategory.TryGetValue(room.Category, out exhaust))
                return exhaust;

            if (ThermalConstants.SupplyRatedCategories.Contains(room.Category))
                return room.Area * ThermalConstants.LivingRoomAirFlow;

            // Коридоры, кладовые, лестницы, тамбуры: нормы вытяжки нет — кратность объёма.
            return room.Volume > 0
                ? room.Volume * ThermalConstants.UtilityAirChangeRate
                : room.Area * room.Height * ThermalConstants.UtilityAirChangeRate;
        }

        /// <summary>
        /// Инфильтрация через окна по СП 50.13330 п. 9.2 (упрощённая формула).
        /// G_inf [кг/ч] = ΣAокон / Ru × (ΔP/10)^(2/3),  Q_inf [Вт] = K · G_inf · c · ΔT.
        /// При L (м³/ч): G = L · ρ, поэтому формула приводится к виду через объёмный расход.
        /// </summary>
        /// <remarks>
        /// internal, а не private, чтобы формулу проверяли автотесты: в общий поток расчёта
        /// метод пока не включён (см. <c>qInf = 0</c> выше и решение по Q_инф в PLAN.md,
        /// этап 2), и без прямого теста он деградировал бы незаметно.
        /// </remarks>
        internal double CalculateInfiltrationLoss(RoomData room, double deltaT)
        {
            if (room.WindowArea <= 0.01) return 0;

            double pressureRatio = System.Math.Pow(
                ThermalConstants.InfiltrationPressureDiff / 10.0,
                2.0 / 3.0);
            // G [кг/ч] — массовый расход воздуха через окна
            double massFlow = room.WindowArea / ThermalConstants.WindowAirResistance * pressureRatio;
            // Q [Вт] = K · G · c · ΔT;  K = 1/3.6 для кДж/ч → Вт
            return ThermalConstants.AirFlowToWatts
                 * massFlow
                 * ThermalConstants.AirSpecificHeat
                 * deltaT;
        }

        private double CalculateInternalHeat(RoomData room, CalculationParameters parameters)
        {
            double areaPerPerson = parameters.OccupancyDensity;
            double heatGainPerM2;

            if (areaPerPerson <= 20)
            {
                heatGainPerM2 = ThermalConstants.InternalHeatHighDensity;
            }
            else if (areaPerPerson >= 45)
            {
                heatGainPerM2 = ThermalConstants.InternalHeatLowDensity;
            }
            else
            {
                double slope = (ThermalConstants.InternalHeatLowDensity - ThermalConstants.InternalHeatHighDensity) / (45 - 20);
                heatGainPerM2 = ThermalConstants.InternalHeatHighDensity + slope * (areaPerPerson - 20);
            }

            // Корректировка для типов помещений (через enum — без сравнения строкой)
            heatGainPerM2 *= GetRoomTypeCoefficient(room.Category);

            return heatGainPerM2 * room.Area;
        }

        private double GetRoomTypeCoefficient(RoomCategory category)
        {
            switch (category)
            {
                case RoomCategory.Storage:
                case RoomCategory.Corridor:
                case RoomCategory.Wardrobe:
                    return 0.5;
                case RoomCategory.Kitchen:
                case RoomCategory.DiningRoom:
                    return 1.2;
                case RoomCategory.Bathroom:
                    return 0.8;
                case RoomCategory.Balcony:
                    return 0.3;
                case RoomCategory.Office:
                    return 1.1;
                default:
                    return 1.0;
            }
        }

        /// <summary>
        /// U пола: реальная конструкция из модели → fallback на ThermalConstants.
        /// </summary>
        private double GetFloorUValue(RoomData room)
        {
            if (_floorRoofCalculator != null)
                return _floorRoofCalculator.GetFloorUValue(room);

            // Fallback при использовании конструктора без Document — та же таблица,
            // что и внутри FloorRoofThermalCalculator: раньше это были две копии.
            return ThermalConstants.FloorUFallback(room.Category);
        }

        /// <summary>
        /// U кровли/потолка: реальная конструкция из модели → fallback на ThermalConstants.
        /// </summary>
        private double GetRoofUValue(RoomData room)
        {
            if (_floorRoofCalculator != null)
                return _floorRoofCalculator.GetRoofUValue(room);

            return ThermalConstants.RoofUFallback(room.Name, room.Type);
        }

        private CalculationResult CreateSummaryResult(List<CalculationResult> results, CalculationParameters parameters)
        {
            var validResults = results.Where(r => string.IsNullOrEmpty(r.ErrorMessage)).ToList();
            
            double totalHeatLoss = validResults.Sum(r => r.Q_final);
            double totalOgr = validResults.Sum(r => r.Q_ogr);
            double totalVent = validResults.Sum(r => r.Q_vent);
            double totalInf = validResults.Sum(r => r.Q_inf);
            double totalVn = validResults.Sum(r => r.Q_vn);
            double totalArea = validResults.Sum(r => r.Area);
            double totalWallArea = validResults.Sum(r => r.WallArea);
            double totalWindowArea = validResults.Sum(r => r.WindowArea);
            double totalDoorArea = validResults.Sum(r => r.DoorArea);

            // Q_total_summary = сумма Q_total строк (которые сами = Q_огр + Q_инф/вент
            // по формуле (1) ТЗ). Считаем напрямую через r.Q_total, чтобы итог совпал.
            double totalRoomQ = validResults.Sum(r => r.Q_total);

            string scope = string.IsNullOrWhiteSpace(parameters?.ScopeName)
                ? "ПО ЗДАНИЮ"
                : parameters.ScopeName;

            return new CalculationResult
            {
                RoomName = $"ИТОГО {scope}",
                Q_ogr = Math.Round(totalOgr, 1),
                Q_vent = Math.Round(totalVent, 1),
                Q_inf = Math.Round(totalInf, 1),
                Q_vn = Math.Round(totalVn, 1),
                Q_total = Math.Round(totalRoomQ, 1),
                Q_final = Math.Round(totalHeatLoss, 1),
                Area = Math.Round(totalArea, 1),
                WallArea = Math.Round(totalWallArea, 1),
                WindowArea = Math.Round(totalWindowArea, 1),
                DoorArea = Math.Round(totalDoorArea, 1),
                ExtWallsArea = Math.Round(validResults.Sum(r => r.ExtWallsArea), 1),
                ExtWindowsArea = Math.Round(validResults.Sum(r => r.ExtWindowsArea), 2),
                IsSummary = true
            };
        }

        // Вспомогательная структура для детализации
        private struct LossDetails
        {
            public double WallLoss;
            public double WindowLoss;
            public double DoorLoss;
            public double FloorLoss;
            public double RoofLoss;
        }
    }
}