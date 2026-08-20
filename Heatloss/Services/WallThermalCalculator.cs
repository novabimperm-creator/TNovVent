using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace QOVETER.Services
{
    /// <summary>
    /// Единственная реализация расчёта теплотехнических характеристик стены.
    /// Источник правды для R и U — заменяет три параллельных пути, которые были
    /// в GeometryCollector, LevelElementScanner и MainWindow.ScanWalls_Click.
    ///
    /// Приоритет источников:
    /// 1. Послойный расчёт через WallType.GetCompoundStructure() + ThermalAsset
    /// 2. Параметр ANALYTICAL_THERMAL_RESISTANCE
    /// 3. Кастомные параметры ("Сопротивление теплопередаче", "R_стены", …)
    /// 4. R по типу конструкции из wall_types.json (ручной ввод инженера)
    /// 5. Норматив: λ материала по СП 50.13330.2012 прил. Т × толщину Wall.Width
    /// 6. Типовое U — только когда материал не распознан вовсе
    /// </summary>
    public class WallThermalCalculator
    {
        private readonly Document _document;

        public WallThermalCalculator(Document document)
        {
            _document = document ?? throw new ArgumentNullException(nameof(document));
        }

        public WallThermalResult Calculate(Wall wall)
        {
            var result = new WallThermalResult { UValue = ThermalConstants.WallUDefault };
            if (wall == null) return result;

            // 1. Послойный расчёт
            if (TryLayered(wall, result)) return result;

            // 2. Аналитическое сопротивление
            if (TryAnalytical(wall, result)) return result;

            // 3. Кастомный параметр
            if (TryCustomParameter(wall, result)) return result;

            // 4. Значение, заданное инженером по ТИПУ конструкции. Стоит выше
            //    эмпирики и ниже данных модели: проектное R из раздела АР лучше
            //    типового умолчания, но данные самой модели главнее.
            if (TryTypeOverride(wall, result)) return result;

            // 5. Норматив: λ материала по СП 50.13330.2012 прил. Т, толщина из модели
            EstimateByMaterial(wall, result);
            return result;
        }

        public double GetUValue(Wall wall) => Calculate(wall).UValue;

        /// <summary>
        /// U ограждения, собранного из НЕСКОЛЬКИХ элементов модели: несущая стена
        /// плюс найденные перед ней фасадные конструкции.
        ///
        /// <para><b>Почему одного элемента мало.</b> Фасадная система в российских
        /// моделях — отдельные стены перед несущей. Взять из них один элемент значит
        /// гарантированно ошибиться: кладка без утеплителя завышает потери, утеплитель
        /// без кладки занижает. Что именно выигрывало отбор, до 2026-08-12 зависело
        /// от того, чей габаритный параллелепипед первым накрыл пробу.</para>
        ///
        /// <para><b>Правило по данным.</b> В сумму идут только слои с РЕАЛЬНЫМИ
        /// данными модели (слои, аналитическое R, пользовательский параметр).
        /// Слой, у которого теплопроводность не задана, даёт ноль и пишется
        /// в журнал — это оценка в запас. Подставлять ему типовое U нельзя:
        /// <see cref="ThermalConstants.WallUDefault"/> = 0,51 — это типовое значение
        /// для стены В СБОРЕ, УЖЕ включающее утеплитель, и прибавлять к нему
        /// найденный в модели утеплитель значит посчитать его дважды.</para>
        ///
        /// <para>Если фасадных слоёв не нашлось или ни у одного нет данных,
        /// возвращается обычный расчёт по несущей стене — прежнее поведение.</para>
        /// </summary>
        public WallThermalResult CalculateAssembly(Wall structural, IList<Wall> facadeLayers)
        {
            var baseResult = Calculate(structural);
            if (facadeLayers == null || facadeLayers.Count == 0) return baseResult;

            var bodyR = new List<double>();
            var described = new List<string>();
            var ignored = new List<string>();
            bool usedNormative = false;

            foreach (var layer in facadeLayers)
            {
                if (layer == null) continue;

                var layerResult = Calculate(layer);
                if (!IsCountableInAssembly(layerResult.Source))
                {
                    ignored.Add($"«{layer.WallType?.Name ?? layer.Name}» (материал не распознан)");
                    continue;
                }

                double r = EnclosureThermal.BodyRFromU(layerResult.UValue);
                if (r <= 0) continue;

                bodyR.Add(r);
                usedNormative |= layerResult.UsedNormativeLambda;
                described.Add($"«{layer.WallType?.Name ?? layer.Name}» R={r:F2}" +
                              (layerResult.UsedNormativeLambda ? " (СП 50 прил. Т)" : ""));
            }

            if (bodyR.Count == 0)
            {
                // Слои перед стеной ЕСТЬ, но ни один не удалось посчитать: ни λ
                // в модели, ни распознанного материала в имени типа. Молчать
                // здесь нельзя — стена уходит на типовое U, то есть на число,
                // к дому отношения не имеющее. Раньше сюда попадало ВСЁ
                // (584 строки за прогон 2026-08-13); теперь — только
                // действительно неизвестные материалы, и это рабочий список
                // на пополнение MaterialConductivity.Catalog.
                if (ignored.Count > 0)
                {
                    Logger.Debug(
                        $"[Wall U] перед «{structural?.WallType?.Name ?? structural?.Name}» " +
                        $"найдено, но не посчитано: {string.Join(", ", ignored)} — " +
                        $"стена осталась на {DescribeSource(baseResult.Source)}, U={baseResult.UValue:F3}");
                }
                return baseResult;
            }

            // Несущая стена входит своим телом, только если её R взят из модели.
            // Иначе она даёт ноль: см. про двойной счёт в описании метода.
            double structuralBodyR = 0;
            string structuralNote;
            if (IsCountableInAssembly(baseResult.Source))
            {
                structuralBodyR = EnclosureThermal.BodyRFromU(baseResult.UValue);
                usedNormative |= baseResult.UsedNormativeLambda;
                structuralNote = $"несущая «{structural?.WallType?.Name ?? structural?.Name}» R={structuralBodyR:F2}";
            }
            else
            {
                structuralNote = $"несущая «{structural?.WallType?.Name ?? structural?.Name}» " +
                                 "МАТЕРИАЛ НЕ РАСПОЗНАН — не учтена, в запас";
            }

            var all = new List<double> { structuralBodyR };
            all.AddRange(bodyR);

            var result = new WallThermalResult
            {
                UValue = EnclosureThermal.UValueFromBodyR(all),
                Source = WallThermalSource.Assembly,
                UsedNormativeLambda = usedNormative,
                LayerCount = baseResult.LayerCount,
                LayerDescription = string.Join(" + ", new[] { structuralNote }.Concat(described))
            };
            result.RValue = 1.0 / result.UValue;

            Logger.Debug(
                $"[Wall U] сборка: {result.LayerDescription}" +
                (ignored.Count > 0 ? $" | не учтено: {string.Join(", ", ignored)}" : "") +
                $" → R={result.RValue:F2}, U={result.UValue:F3} (было {baseResult.UValue:F3})");

            return result;
        }

        /// <summary>
        /// Значение получено из модели, а не подставлено оценкой. Только такие
        /// слои складываются в сборку: сумма оценок — выдуманное число.
        /// </summary>
        /// <summary>
        /// R из файла <c>wall_types.json</c> — проектное значение, вписанное
        /// инженером по типу конструкции. Нужно там, где в модели теплотехники
        /// нет вовсе: подставлять вместо неё типовое U значит выдавать за расчёт
        /// число, не имеющее отношения к дому.
        /// </summary>
        private bool TryTypeOverride(Wall wall, WallThermalResult result)
        {
            try
            {
                string typeName = wall.WallType?.Name ?? wall.Name;
                double r;
                string source;
                if (!WallTypeOverrides.TryGet(typeName, out r, out source)) return false;

                result.RValue = r;
                result.UValue = 1.0 / r;
                result.Source = WallThermalSource.CustomParameter;
                result.LayerDescription = $"R задан по типу конструкции ({source})";
                Logger.Debug($"[Wall U] {typeName}: R={r:F2} из wall_types.json ({source}) → U={result.UValue:F3}");
                return true;
            }
            catch (Exception ex)
            {
                Logger.Debug($"[Wall U] type override: {ex.Message}");
                return false;
            }
        }

        /// <summary>Откуда взято значение — словами, для журнала и отчёта.</summary>
        public static string DescribeSource(WallThermalSource source)
        {
            switch (source)
            {
                case WallThermalSource.LayeredCompoundStructure: return "расчёт по слоям";
                case WallThermalSource.AnalyticalParameter:      return "аналитическое R";
                case WallThermalSource.CustomParameter:          return "параметр модели";
                case WallThermalSource.Assembly:                 return "сборку";
                case WallThermalSource.NormativeByMaterial:      return "λ по СП 50 прил. Т (в модели λ нет)";
                default:                                         return "типовое значение (данных нет)";
            }
        }

        /// <summary>
        /// Значение получено из модели, а не подставлено оценкой. Публично —
        /// потому что этот же вопрос решает отчёт: сколько площади посчитано
        /// по данным, а сколько по умолчанию.
        /// </summary>
        public static bool IsFromModel(WallThermalSource source)
        {
            return source == WallThermalSource.LayeredCompoundStructure ||
                   source == WallThermalSource.AnalyticalParameter ||
                   source == WallThermalSource.CustomParameter;
        }

        /// <summary>
        /// Слой можно складывать в сборку ограждения.
        ///
        /// <para>Шире, чем <see cref="IsFromModel"/>, ровно на один случай:
        /// λ, взятую из таблицы СП 50.13330.2012 прил. Т. Складывать её ЗАКОННО,
        /// потому что это послойный расчёт, а не сумма оценок: толщина каждого
        /// слоя взята из модели, теплопроводность — из норматива по материалу
        /// этого слоя. Тот же расчёт делает проектировщик вручную.</para>
        ///
        /// <para><b>А вот <see cref="WallThermalSource.Default"/> складывать
        /// нельзя</b> — и в этом вся разница. <see cref="ThermalConstants.WallUDefault"/>
        /// = 0,51 описывает стену В СБОРЕ, вместе с утеплителем; прибавить к ней
        /// найденный в модели утеплитель значит посчитать его дважды.</para>
        ///
        /// <para>Практический смысл правки: до неё в сборку шли только данные
        /// модели, а их нет — за прогон 2026-08-13 фасадные слои находились
        /// 584 раза и НИ РАЗУ не были посчитаны, сборок собралось 0. Кладка
        /// уходила на типовое U, найденный перед ней утеплитель пропадал.</para>
        /// </summary>
        public static bool IsCountableInAssembly(WallThermalSource source)
        {
            return IsFromModel(source) || source == WallThermalSource.NormativeByMaterial;
        }

        /// <summary>
        /// Слои стены снаружи внутрь — для классификации конструкции по СП 230
        /// (<see cref="Models.WallConstructionProfile"/>). Пустой список означает,
        /// что конструкцию не удалось опознать ни по слоям, ни по имени типа:
        /// тогда таблицы СП подобрать нельзя и нужен ручной ввод.
        ///
        /// <para><b>λ берётся оттуда же, откуда и в расчёте U.</b> Сначала из модели
        /// (<see cref="ReadConductivity"/>), а где её нет — из таблицы СП 50.13330.2012
        /// приложение Т по имени материала. Без этого шага на 76-СУЗДАЛ.23 список
        /// возвращался с нулевыми λ у ВСЕХ слоёв: классификатор не находил ни
        /// утеплителя (λ ≤ 0,1), ни несущего слоя (λ &gt; 0,1), конструкция оставалась
        /// <c>Unknown</c>, и ни одна таблица приложения Г СП 230 не подбиралась —
        /// то есть мостики холода не считались бы даже при включённом режиме.</para>
        ///
        /// <para><b>Стена одним материалом.</b> Когда <c>CompoundStructure</c> нет
        /// или ни один слой не дал λ, конструкция описывается ОДНИМ слоем: имя типа
        /// как материал, <c>Wall.Width</c> как толщина. Это ровно то же допущение,
        /// на котором стоит <see cref="EstimateByMaterial"/> — «Фасад Утеплитель НГ 140
        /// под штукатурку» и есть слой утеплителя 140 мм.</para>
        /// </summary>
        public List<Models.WallLayer> GetLayers(Wall wall)
        {
            var result = new List<Models.WallLayer>();
            if (wall == null) return result;
            try
            {
                // ⚠ Классификация обязана описывать ТУ ЖЕ конструкцию, из которой
                // получено U. Если U взято по имени типа (СП 50 прил. Т), то и слой
                // ровно один — вся стена этим материалом; разбирать при этом
                // CompoundStructure нельзя.
                //
                // Цена ошибки измерена на прогоне 2026-08-19 14:25: у фасадных
                // элементов в слоях распознаётся посторонняя мелочь (штукатурка,
                // мембрана), а сам утеплитель остаётся с λ = 0 — потому что имя
                // материала не распознаётся, а имя ТИПА не читается вовсе. Условие
                // «ни один слой не дал λ» при этом не срабатывало, и утеплитель
                // молча выпадал: за прогон R_ут не заполнился НИ РАЗУ у 397 стен,
                // конструкция везде вышла «кладка», а не «наружное утепление».
                // Следствие — лишний узел плиты перекрытия (по Г.3 при наружном
                // утеплении его нет) и оконный узел по чужой таблице.
                if (Calculate(wall).Source == WallThermalSource.NormativeByMaterial)
                {
                    var byType = NormativeLayerFromTypeName(wall);
                    if (byType != null)
                    {
                        result.Add(byType);
                        return result;
                    }
                }

                var cs = (_document.GetElement(wall.GetTypeId()) as WallType)?.GetCompoundStructure();
                if (cs != null)
                {
                    foreach (var layer in cs.GetLayers())
                    {
                        double thickness = UnitUtils.ConvertFromInternalUnits(layer.Width, UnitTypeId.Meters);
                        if (thickness <= 0) continue;

                        double lambda = 0;
                        string name = "";
                        if (layer.MaterialId != ElementId.InvalidElementId)
                        {
                            var material = _document.GetElement(layer.MaterialId) as Material;
                            if (material != null)
                            {
                                name = material.Name;
                                lambda = ReadConductivity(material);
                            }
                        }

                        // λ в модели нет — тот же норматив, что и в расчёте U.
                        if (lambda <= 0) lambda = NormativeLambda(name);

                        result.Add(new Models.WallLayer
                        {
                            Material = name,
                            ThicknessM = thickness,
                            Conductivity = lambda
                        });
                    }
                }

                if (!result.Any(l => l.Conductivity > 0))
                {
                    var single = NormativeLayerFromTypeName(wall);
                    if (single != null)
                    {
                        result.Clear();
                        result.Add(single);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Debug($"GetLayers: не удалось прочитать слои стены {wall?.Id}: {ex.Message}");
            }
            return result;
        }

        /// <summary>
        /// Слои ВСЕГО ограждения снаружи внутрь: фасадные элементы, стоящие перед
        /// несущей стеной, плюс сама несущая стена.
        ///
        /// <para><b>Зачем.</b> Тип конструкции по СП 230 решает, какие узлы вообще
        /// существуют и по какой таблице считать Ψ. По одной несущей стене он
        /// определяется НЕВЕРНО: утеплитель в российских моделях лежит отдельным
        /// элементом, и кладка без него классифицируется как
        /// <see cref="WallConstructionType.MasonryWithBrickFacing"/> вместо
        /// <see cref="WallConstructionType.ExternalInsulationThinFacing"/>. Разница
        /// не косметическая: у кладки с облицовкой выход плиты перекрытия — мостик
        /// (таблицы Г.5–Г.10), а при наружном утеплении плита закрыта утеплителем
        /// и мостиком не является (раздел Г.3). Ошибка в типе конструкции добавила бы
        /// дому узел на всю длину наружных стен КАЖДОГО этажа.</para>
        ///
        /// <para>Стек фасада приходит от <c>GeometryCollector</c> изнутри наружу —
        /// в порядке марша пробы, — поэтому здесь он разворачивается.</para>
        /// </summary>
        public List<Models.WallLayer> GetAssemblyLayers(Wall structural, IList<Wall> facadeStack)
        {
            var layers = new List<Models.WallLayer>();

            if (facadeStack != null)
            {
                for (int i = facadeStack.Count - 1; i >= 0; i--)
                {
                    if (facadeStack[i] == null) continue;
                    layers.AddRange(GetLayers(facadeStack[i]));
                }
            }

            if (structural != null) layers.AddRange(GetLayers(structural));
            return layers;
        }

        /// <summary>
        /// λ материала по СП 50.13330.2012 приложение Т, условия эксплуатации Б.
        /// Ноль — материал по имени не распознан.
        /// </summary>
        private static double NormativeLambda(string name)
        {
            MaterialConductivity.MaterialEntry entry;
            if (string.IsNullOrWhiteSpace(name) ||
                !MaterialConductivity.TryResolve(name, out entry)) return 0;
            return entry.Lambda(MaterialConductivity.DefaultCondition);
        }

        /// <summary>
        /// Стена как ОДИН слой: материал распознан по имени типа, толщина — из модели.
        /// null, если имя типа не распознано или толщина не читается.
        /// </summary>
        private Models.WallLayer NormativeLayerFromTypeName(Wall wall)
        {
            string typeName = wall.WallType?.Name ?? wall.Name ?? "";
            double lambda = NormativeLambda(typeName);
            if (lambda <= 0) return null;

            double thicknessM = 0;
            try { thicknessM = UnitUtils.ConvertFromInternalUnits(wall.Width, UnitTypeId.Meters); }
            catch (Exception ex) { Logger.Debug($"GetLayers: Wall.Width недоступна: {ex.Message}"); }
            if (thicknessM <= 0) return null;

            return new Models.WallLayer
            {
                Material = typeName,
                ThicknessM = thicknessM,
                Conductivity = lambda
            };
        }

        /// <summary>
        /// Послойный расчёт R конструкции (без Rsi+Rse) для произвольной CompoundStructure.
        /// Используется не только для стен, но и для полов и кровли.
        /// </summary>
        public double CalculateLayeredR(CompoundStructure cs, out int layerCount, out string layerDescription)
        {
            layerCount = 0;
            layerDescription = string.Empty;
            if (cs == null) return 0;

            double rTotal = 0;
            var descriptions = new List<string>();
            foreach (var layer in cs.GetLayers())
            {
                double thicknessM = UnitUtils.ConvertFromInternalUnits(layer.Width, UnitTypeId.Meters);
                if (thicknessM < 0.001) continue;

                string matName = "—";
                double lambda = 0;
                if (layer.MaterialId != ElementId.InvalidElementId)
                {
                    var mat = _document.GetElement(layer.MaterialId) as Material;
                    if (mat != null)
                    {
                        matName = mat.Name ?? "—";
                        lambda = ReadConductivity(mat);
                    }
                }
                bool isAir = IsAirLayer(matName);
                if (lambda > 0.01 && !isAir) rTotal += thicknessM / lambda;

                descriptions.Add(isAir
                    ? $"{thicknessM * 1000:F0}мм {matName} (прослойка — не учтена)"
                    : lambda > 0
                        ? $"{thicknessM * 1000:F0}мм {matName} (λ={lambda:F3})"
                        : $"{thicknessM * 1000:F0}мм {matName}");
            }
            layerCount = cs.LayerCount;
            layerDescription = string.Join(" | ", descriptions);
            return rTotal;
        }

        private bool TryLayered(Wall wall, WallThermalResult result)
        {
            try
            {
                var wallType = _document.GetElement(wall.GetTypeId()) as WallType;
                var cs = wallType?.GetCompoundStructure();
                if (cs == null) return false;

                double rLayers = 0;
                bool hasThermalLayer = false;
                var layerDescriptions = new List<string>();

                foreach (var layer in cs.GetLayers())
                {
                    double thicknessM = UnitUtils.ConvertFromInternalUnits(layer.Width, UnitTypeId.Meters);
                    if (thicknessM < 0.001) continue;

                    string matName = "—";
                    double lambda = 0;

                    if (layer.MaterialId != ElementId.InvalidElementId)
                    {
                        var mat = _document.GetElement(layer.MaterialId) as Material;
                        if (mat != null)
                        {
                            matName = mat.Name ?? "—";
                            lambda = ReadConductivity(mat);
                        }
                    }

                    bool isAir = IsAirLayer(matName);

                    if (lambda > 0.01 && !isAir)
                    {
                        rLayers += thicknessM / lambda;
                        hasThermalLayer = true;
                    }

                    double thicknessMm = thicknessM * 1000.0;
                    layerDescriptions.Add(isAir
                        ? $"{thicknessMm:F0}мм {matName} (прослойка — не учтена)"
                        : lambda > 0
                            ? $"{thicknessMm:F0}мм {matName} (λ={lambda:F3})"
                            : $"{thicknessMm:F0}мм {matName}");
                }

                if (!hasThermalLayer || rLayers <= 0.05) return false;

                result.RValue = rLayers + ThermalConstants.RsiPlusRse;
                result.UValue = 1.0 / result.RValue;
                result.Source = WallThermalSource.LayeredCompoundStructure;
                result.LayerDescription = string.Join(" | ", layerDescriptions);
                result.LayerCount = cs.LayerCount;
                Logger.Debug($"[Wall U] {wall.Name}: R={result.RValue:F2} → U={result.UValue:F3} (послойный)");
                return true;
            }
            catch (Exception ex)
            {
                Logger.Warn($"[Wall U] послойный расчёт не удался для {wall.Id.IntegerValue}", ex);
                return false;
            }
        }

        private bool TryAnalytical(Wall wall, WallThermalResult result)
        {
            try
            {
                var p = wall.get_Parameter(BuiltInParameter.ANALYTICAL_THERMAL_RESISTANCE);
                double rSI;
                if (!RevitThermalParameter.TryReadResistance(p, $"Стена {wall.Name}", out rSI))
                    return false;

                result.RValue = rSI;
                result.UValue = 1.0 / rSI;
                result.Source = WallThermalSource.AnalyticalParameter;
                Logger.Debug($"[Wall U] {wall.Name}: R_analytical={rSI:F2} → U={result.UValue:F3}");
                return true;
            }
            catch (Exception ex)
            {
                Logger.Debug($"[Wall U] analytical: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// R из пользовательского параметра («Сопротивление теплопередаче», «R_стены»…).
        ///
        /// Здесь стояло <c>result.RValue = p.AsDouble()</c> — БЕЗ конверсии единиц
        /// и без проверки диапазона, тогда как соседний <see cref="TryAnalytical"/>
        /// тот же по смыслу параметр конвертировал. Если параметр заведён
        /// типизированным (а «Сопротивление теплопередаче» так заводят обычно),
        /// R выходил в 5,678 раза больше, U — во столько же раз меньше, и стена
        /// молча становилась почти непроницаемой. Ветка вдобавок ничего не писала
        /// в журнал — в отличие от двух соседних, — так что след не оставался.
        /// Теперь чтение идёт через <see cref="RevitThermalParameter"/>, единственное
        /// место в проекте, где решается вопрос единиц.
        /// </summary>
        private bool TryCustomParameter(Wall wall, WallThermalResult result)
        {
            try
            {
                var wallTypeEl = _document.GetElement(wall.GetTypeId());
                foreach (var name in CustomResistanceParameterNames)
                {
                    var p = wall.LookupParameter(name) ?? wallTypeEl?.LookupParameter(name);
                    double rSI;
                    if (!RevitThermalParameter.TryReadResistance(p, $"Стена {wall.Name}", out rSI))
                        continue;

                    result.RValue = rSI;
                    result.UValue = 1.0 / rSI;
                    result.Source = WallThermalSource.CustomParameter;
                    Logger.Debug($"[Wall U] {wall.Name}: R из «{name}» = {rSI:F2} → U={result.UValue:F3}");
                    return true;
                }
            }
            catch (Exception ex)
            {
                Logger.Debug($"[Wall U] custom param: {ex.Message}");
            }
            return false;
        }

        /// <summary>
        /// R конструкции по НОРМАТИВУ: толщина берётся из модели (<c>Wall.Width</c>),
        /// теплопроводность — из таблицы СП 50.13330.2012 прил. Т по распознанному
        /// в имени типа материалу (<see cref="MaterialConductivity"/>).
        ///
        /// <para><b>Что здесь стояло раньше.</b> Четыре ветки по подстрокам имени
        /// с вписанными в код λ: 0,12 для газобетона, 0,70 для кирпича, 0,74 для
        /// бетона и U = 0,35 для сэндвича. Ни одна не имела ссылки на норматив,
        /// и все четыре расходились с ним: у газобетона ρ = 600 по прил. Т
        /// λ_Б = 0,26 (вдвое больше), у железобетона — 2,04, а не 1,74 (это λ_А
        /// бетона на гравии, другая строка и другие условия эксплуатации).
        /// Главное же — самый большой тип этого дома, «Наруж стена
        /// Силикатныйблок250» на 2 097 м², не попадал ни в одну ветку и уходил
        /// на типовое U = 0,51, то есть на число, к дому отношения не имеющее.</para>
        ///
        /// <para><b>Почему теперь ветки не нужны.</b> Толщина у стены в модели
        /// есть всегда и точная; не хватает только λ, а λ определяется материалом,
        /// и материал назван в имени типа. Поэтому решение сведено к одному
        /// вопросу — узнаём ли мы материал, — и ответ на него берётся из таблицы
        /// норматива со ссылкой на позицию.</para>
        /// </summary>
        private void EstimateByMaterial(Wall wall, WallThermalResult result)
        {
            string typeName = wall.WallType?.Name ?? wall.Name ?? "";

            // Толщина-заглушка задаётся В МЕТРАХ и подставляется ПОСЛЕ конвертации.
            // Раньше 0.5 подставлялось внутрь ConvertFromInternalUnits и трактовалось
            // как футы — на выходе получалось ≈0,152 м вместо задуманных 0,5 м,
            // то есть R занижался втрое для веток «кирпич»/«бетон»/«газобет».
            const double fallbackThicknessM = 0.5;

            // Толщина берётся СВОЙСТВОМ Wall.Width, а параметр — только запасным путём.
            // `WALL_ATTR_WIDTH_PARAM` живёт у ТИПА стены, и на экземпляре обычно
            // отдаёт null — та же ловушка, что с `FUNCTION_PARAM` (код-ревью
            // 2026-08-06). Из-за неё сюда подставлялась заглушка 0,5 м: «Монолит
            // Бетон200» считался полуметром бетона, R = 0,457 вместо 0,285,
            // то есть теплопотери занижались в полтора раза. На 76-СУЗДАЛ.23
            // ровно это давало U = 2,186 на 26% площади ограждений.
            double thicknessM = 0;
            try
            {
                thicknessM = UnitUtils.ConvertFromInternalUnits(wall.Width, UnitTypeId.Meters);
            }
            catch (Exception ex) { Logger.Debug($"[Wall U] Wall.Width недоступна: {ex.Message}"); }

            if (thicknessM <= 0)
            {
                var widthParam = wall.get_Parameter(BuiltInParameter.WALL_ATTR_WIDTH_PARAM);
                thicknessM = widthParam != null && widthParam.HasValue && widthParam.AsDouble() > 0
                    ? UnitUtils.ConvertFromInternalUnits(widthParam.AsDouble(), UnitTypeId.Meters)
                    : fallbackThicknessM;

                Logger.Debug(
                    $"[Wall U] {wall.Name}: толщина не прочитана, принято {thicknessM:F3} м");
            }

            MaterialConductivity.MaterialEntry entry;
            if (!MaterialConductivity.TryResolve(typeName, out entry) || thicknessM <= 0)
            {
                // Материал не распознан. Здесь и только здесь остаётся типовое
                // U — и оно ЗАЯВЛЯЕТСЯ как «данных нет», а не выдаётся за расчёт.
                result.UValue = ThermalConstants.WallUDefault;
                result.RValue = 1.0 / result.UValue;
                result.Source = WallThermalSource.Default;
                result.LayerDescription = $"материал «{typeName}» не распознан — принято типовое U";
                Logger.Debug($"[Wall U] {typeName}: материал не распознан → типовое U={result.UValue:F3}");
                return;
            }

            double lambda = entry.Lambda(MaterialConductivity.DefaultCondition);
            result.RValue = thicknessM / lambda + ThermalConstants.RsiPlusRse;
            result.UValue = 1.0 / result.RValue;
            result.Source = WallThermalSource.NormativeByMaterial;
            result.UsedNormativeLambda = true;
            result.LayerDescription =
                $"{thicknessM * 1000:F0}мм {entry.Describe(MaterialConductivity.DefaultCondition)}";

            Logger.Debug(
                $"[Wall U] {typeName}: {thicknessM * 1000:F0}мм / λ={lambda:F3} → " +
                $"R={result.RValue:F2}, U={result.UValue:F3} ({entry.SpRow})");

            // Сторож на модельный пробел. Ограждение с U > 2 — это либо голый
            // монолит без утеплителя (бывает, и это мостик холода), либо
            // отделочный слой, за которым не нашлось несущей конструкции.
            // Второе — дефект сбора, а не физика, и молча его пропускать нельзя.
            if (result.UValue > 2.0)
            {
                Logger.Warn(
                    $"[Wall U] «{typeName}»: U={result.UValue:F2} при толщине {thicknessM * 1000:F0} мм — " +
                    "проверьте, вся ли конструкция найдена (тонкий слой без несущей стены за ним)");
            }
        }

        /// <summary>
        /// Слой воздуха: замкнутая или вентилируемая прослойка.
        ///
        /// <para><b>Считать её по λ нельзя.</b> Воздух передаёт теплоту конвекцией
        /// и излучением, а не только теплопроводностью, поэтому СП 50.13330 даёт
        /// для замкнутых прослоек ТАБЛИЧНЫЕ сопротивления (порядка 0,15 м²·К/Вт),
        /// а слои за вентилируемой прослойкой не учитывает вовсе. Расчёт по
        /// λ = 0,024 даёт прослойке 225 мм сопротивление R = 9,17 — больше, чем
        /// у 300 мм пенополистирола. Именно это на 76-СУЗДАЛ.23 делало отдельные
        /// сегменты стен «тёплыми» с U = 0,109 (29 сегментов за прогон).</para>
        ///
        /// <para>Слой не учитывается вовсе — это оценка в запас: табличное
        /// сопротивление замкнутой прослойки мы при этом теряем. Подставить его
        /// нельзя, пока таблица не выписана из текста СП.</para>
        /// </summary>
        private static bool IsAirLayer(string materialName)
        {
            return LayerNaming.IsAirLayer(materialName);
        }

        /// <summary>
        /// Читает теплопроводность материала, Вт/(м·К). Приоритет:
        /// ThermalAsset → BuiltInParameter PHY_MATERIAL_PARAM_THERMAL_CONDUCTIVITY → LookupParameter "Теплопроводность".
        /// Все значения конвертируются через UnitUtils.
        /// </summary>
        public double ReadConductivity(Material mat)
        {
            if (mat == null) return 0;

            try
            {
                if (mat.ThermalAssetId != ElementId.InvalidElementId)
                {
                    var thermalAsset = _document.GetElement(mat.ThermalAssetId) as PropertySetElement;
                    var p = thermalAsset?.get_Parameter(BuiltInParameter.PHY_MATERIAL_PARAM_THERMAL_CONDUCTIVITY);
                    if (p != null && p.HasValue && p.AsDouble() > 0)
                    {
                        return UnitUtils.ConvertFromInternalUnits(
                            p.AsDouble(), UnitTypeId.WattsPerMeterKelvin);
                    }
                }

                var direct = mat.get_Parameter(BuiltInParameter.PHY_MATERIAL_PARAM_THERMAL_CONDUCTIVITY);
                if (direct != null && direct.HasValue && direct.AsDouble() > 0)
                {
                    return UnitUtils.ConvertFromInternalUnits(
                        direct.AsDouble(), UnitTypeId.WattsPerMeterKelvin);
                }

                var named = mat.LookupParameter("Теплопроводность")
                          ?? mat.LookupParameter("Thermal Conductivity");
                if (named != null && named.HasValue && named.AsDouble() > 0)
                {
                    return UnitUtils.ConvertFromInternalUnits(
                        named.AsDouble(), UnitTypeId.WattsPerMeterKelvin);
                }
            }
            catch (Exception ex)
            {
                Logger.Debug($"ReadConductivity: ошибка для {mat.Name}: {ex.Message}");
            }
            return 0;
        }

        private static readonly string[] CustomResistanceParameterNames =
        {
            "Сопротивление теплопередаче",
            "R_стены",
            "RT",
            "R-value"
        };
    }

    public enum WallThermalSource
    {
        Default = 0,
        LayeredCompoundStructure,
        AnalyticalParameter,
        CustomParameter,

        /// <summary>
        /// λ материала взята из таблицы СП 50.13330.2012 прил. Т по имени типа,
        /// толщина — из модели. Не данные модели, но и не выдуманное число:
        /// у значения есть позиция таблицы норматива.
        /// </summary>
        NormativeByMaterial,

        /// <summary>
        /// Сборка из нескольких элементов модели: несущая стена плюс фасадная
        /// система, стоящая перед ней отдельными стенами.
        /// </summary>
        Assembly
    }

    public class WallThermalResult
    {
        /// <summary>Полное сопротивление теплопередаче, м²·К/Вт (включая Rsi+Rse).</summary>
        public double RValue { get; set; }

        /// <summary>Коэффициент теплопередачи, Вт/(м²·К). U = 1/R.</summary>
        public double UValue { get; set; }

        public WallThermalSource Source { get; set; } = WallThermalSource.Default;

        /// <summary>
        /// В значение вошла λ из таблицы СП 50.13330.2012 прил. Т, а не из модели.
        ///
        /// <para>Отдельный признак нужен потому, что <see cref="WallThermalSource.Assembly"/>
        /// сам по себе не говорит, откуда взялись слои: сборка бывает и целиком
        /// из данных модели, и целиком по нормативу, и смешанной. Отчёт обязан
        /// делить площадь на ТРИ части, а не на две, — иначе «по данным модели»
        /// и «по таблице норматива» сливаются в одно и подписывающий отчёт
        /// не видит, что именно он подписывает.</para>
        /// </summary>
        public bool UsedNormativeLambda { get; set; }

        /// <summary>
        /// Сопротивление поднято до нормируемого по СП 50.13330 таблица 3:
        /// данные модели дали ограждению НА УЛИЦУ значение хуже, чем норматив
        /// вообще допускает. Это признак пробела в модели, а не свойство стены,
        /// и в отчёте он идёт отдельной строкой.
        /// </summary>
        public bool RaisedToNormative { get; set; }

        /// <summary>
        /// Значение целиком получено из модели (в том числе собранное из нескольких
        /// её элементов). Здесь, а не в <see cref="WallThermalCalculator.IsFromModel"/>,
        /// потому что для сборки ответ зависит от состава слоёв, а не от кода источника.
        /// </summary>
        public bool IsEntirelyFromModel =>
            !UsedNormativeLambda &&
            (WallThermalCalculator.IsFromModel(Source) || Source == WallThermalSource.Assembly);

        /// <summary>Описание слоёв через "|" — для отображения в UI.</summary>
        public string LayerDescription { get; set; } = "";

        public int LayerCount { get; set; }
    }
}
