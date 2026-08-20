using QOVETER.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;

namespace QOVETER.Services
{
    /// <summary>Тип конструкции наружной стены — определяет, какую таблицу СП 230 брать.</summary>
    public enum WallConstructionType
    {
        /// <summary>Не удалось классифицировать по слоям — таблица выбирается вручную.</summary>
        Unknown = 0,

        /// <summary>Кладка из блоков лёгкого, особо лёгкого и ячеистого бетона или
        /// крупноформатных камней с облицовкой кирпичом.</summary>
        MasonryWithBrickFacing,

        /// <summary>Стена с наружным утеплением и тонкой облицовкой: СФТК либо
        /// вентилируемый фасад. Сюда же СП относит трёхслойные стены с эффективным
        /// утеплителем и облицовкой кирпичом (примечание к таблице Г.28).</summary>
        ExternalInsulationThinFacing,

        /// <summary>Тонкостенная панель, в том числе сэндвич.</summary>
        ThinPanel,

        /// <summary>Стена с внутренним утеплением.</summary>
        InternalInsulation,

        /// <summary>
        /// Трёхслойная стена с облицовкой кирпичом — у неё отдельная таблица для
        /// оконного узла (Г.32), а для угла СП разрешает применять таблицу Г.28.
        /// </summary>
        ThreeLayerBrickFacing
    }

    /// <summary>
    /// Дискретные признаки узла: по ним таблица выбирается целиком, интерполяции
    /// между ними нет — это конструктивные решения, а не непрерывные величины.
    /// Заполняются из модели, где возможно, иначе задаются в настройках расчёта.
    /// </summary>
    public class BridgeSelectors
    {
        /// <summary>Толщина оконной рамы, мм (60 / 80 / 120 в таблицах СП).</summary>
        public double? FrameMm { get; set; }

        /// <summary>Зуб (четверть) при установке окна, мм: 0 или 60.</summary>
        public double? NotchMm { get; set; }

        /// <summary>Нахлёст утеплителя на раму, мм: 0, 20 или 60.</summary>
        public double? OverlapMm { get; set; }

        /// <summary>Термическое сопротивление утеплителя на плите перекрытия, м²·°С/Вт.</summary>
        public double? SlabInsulationR { get; set; }

        /// <summary>Эффективная толщина плиты перекрытия dп, мм (160 или 210 в таблицах СП).</summary>
        public double? SlabThicknessMm { get; set; }

        /// <summary>
        /// Перфорация плиты перекрытия — отношение a/b длины термовкладышей к
        /// расстоянию между ними: 0 (без перфорации), 1, 3 или 5.
        /// СП прямо пишет, что узлы без перфорации в современных конструкциях
        /// недопустимы, а 3/1 — типовое для современного строительства.
        /// </summary>
        public double? SlabPerforationRatio { get; set; }

        /// <summary>
        /// Толщина основания стены d_о, мм (200 или 400 в таблицах Г.24–Г.26).
        /// Заполняется автоматически из слоёв: это несущий слой конструкции.
        /// </summary>
        public double? BaseThicknessMm { get; set; }

        /// <summary>
        /// Высота дополнительного утепления парапета от верха кровли h_ут, мм
        /// (0, 200 или 500 в таблицах Г.41–Г.49). По примечанию СП влияет слабо:
        /// утепление парапета со стороны покрытия не даёт ощутимого результата.
        /// </summary>
        public double? ParapetInsulationMm { get; set; }

        /// <summary>
        /// Термическое сопротивление утеплителя НА СТЕНЕ R_ут1, м²·°С/Вт —
        /// признак выбора таблицы парапета. По примечанию СП для стен с наружным
        /// утеплением и трёхслойных практически не влияет на этот узел.
        /// </summary>
        public double? WallInsulationR { get; set; }

        /// <summary>
        /// ИСПОЛНЕНИЕ узла — значение поля <c>Variant</c> в каталоге:
        /// «Perforated» (обычная перфорация), «ThermalInsert» (несущие
        /// теплоизоляционные элементы, НТЭ), «TwoLayerInsulationWithAirGap»,
        /// «FrameAtInsulation» / «FrameShiftedIntoInsulation» /
        /// «FrameShiftedFromInsulation» / «FrameBehindFacing» (положение оконной рамы).
        ///
        /// Без этого признака целые таблицы СП были НЕДОСТИЖИМЫ: у Г.6 (перфорация
        /// 1/1) и Г.10 (НТЭ 1/1) одинаковые числовые признаки, и выбор между ними
        /// решался порядком строк в JSON-ресурсе. То же у оконных узлов Г.33–Г.35,
        /// где положение рамы меняет Ψ втрое (0,054 против 0,156 при R_ут=1,5).
        ///
        /// Пусто — исполнение не задано: из равных берётся ХУДШЕЕ по потерям,
        /// как и остальные умолчания узлов («в запас», см. CalculationParameters.NodeDetails).
        /// </summary>
        public string Execution { get; set; }

        /// <summary>
        /// Тарельчатых анкеров на квадратный метр фасада, шт/м² — плотность крепежа
        /// фасадной системы. Из модели Revit НЕ вытаскивается: это раскладка дюбелей
        /// в проекте фасада, а не геометрия здания.
        ///
        /// <para><b>Умолчания нет намеренно.</b> Пусто — анкеры не считаются вовсе,
        /// и сводка прогона это называет. Подставить «типовые 5 шт/м²» значит снова
        /// выдумать число: у СФТК и вентфасада раскладка разная, у угловых зон
        /// здания она вдвое плотнее середины. Цена вопроса при этом заметна —
        /// 6 анкеров с χ = 0,004 дают 0,024 Вт/(м²·К), то есть около 8% к U = 0,316.</para>
        /// </summary>
        public double? AnchorsPerM2 { get; set; }

        /// <summary>
        /// L₁ — расстояние от края стального распорного элемента анкера до тарелки
        /// дюбеля, мм (ось таблицы Г.4). Пусто при заданной плотности — берётся
        /// худший случай L₁ ≤ 2 мм, χ = 0,006 Вт/°С.
        /// </summary>
        public double? AnchorL1Mm { get; set; }

        public double? GetAxisValue(string axis)
        {
            switch (axis)
            {
                case "FrameMm":              return FrameMm;
                case "NotchMm":              return NotchMm;
                case "OverlapMm":            return OverlapMm;
                case "SlabInsulationR":      return SlabInsulationR;
                case "SlabThicknessMm":      return SlabThicknessMm;
                case "SlabPerforationRatio": return SlabPerforationRatio;
                case "BaseThicknessMm":      return BaseThicknessMm;
                case "ParapetInsulationMm":  return ParapetInsulationMm;
                case "WallInsulationR":      return WallInsulationR;
                default:                     return null;
            }
        }
    }

    /// <summary>Вариант узла: для углов СП различает выпуклый и вогнутый.</summary>
    public enum BridgeVariant
    {
        Convex = 0,
        Concave
    }

    /// <summary>
    /// Каталог удельных потерь теплоты через теплотехнические неоднородности
    /// по приложению Г СП 230.1325800.2015.
    ///
    /// Значения выписаны из текста СП дословно и лежат во встроенном ресурсе
    /// <c>Resources/SP230Bridges.json</c> вместе с номером таблицы и осями параметров.
    /// Каждая таблица двумерная: строки и столбцы — параметры конструкции
    /// (толщина кладки, теплопроводность основания, термическое сопротивление
    /// утеплителя). Между узлами сетки — линейная интерполяция; за пределами сетки
    /// значение зажимается по краю, потому что экстраполяцию СП не предусматривает.
    ///
    /// Важное из текста СП, раздел Г.4: угол рассматривается как ЧИСТО ГЕОМЕТРИЧЕСКИЙ
    /// элемент. Связи и крепёж рядом с углом учитываются отдельными элементами.
    /// Выпуклый угол даёт положительные удельные потери, вогнутый — ОТРИЦАТЕЛЬНЫЕ.
    /// Для тонкостенных панелей и стен с внутренним утеплением угол не учитывается вовсе.
    /// </summary>
    public static class SP230Catalog
    {
        private const string ResourceName = "QOVETER.Resources.SP230Bridges.json";

        private static readonly Lazy<SP230CatalogDto> Data = new Lazy<SP230CatalogDto>(LoadResource);

        /// <summary>Ссылка на источник целиком — идёт в отчёт.</summary>
        public static string Source => Data.Value?.Source ?? "СП 230.1325800.2015";

        /// <summary>Все таблицы, загруженные из ресурса.</summary>
        public static IReadOnlyList<SP230TableDto> Tables =>
            (IReadOnlyList<SP230TableDto>)(Data.Value?.Tables) ?? new List<SP230TableDto>();

        /// <summary>
        /// Удельные потери теплоты Ψ, Вт/(м·°С), для узла при заданной конструкции стены.
        /// Возвращает null, если для такой комбинации таблицы в СП нет — тогда узел
        /// либо не учитывается (панели, внутреннее утепление), либо значение задаётся вручную.
        /// </summary>
        public static SP230Lookup Find(
            ThermalBridgeType node,
            WallConstructionType construction,
            WallConstructionProfile profile,
            BridgeVariant variant = BridgeVariant.Convex,
            BridgeSelectors selectors = null)
        {
            if (profile == null) return null;
            selectors = selectors ?? new BridgeSelectors();

            // СП рассматривает примыкание оконных И ДВЕРНЫХ блоков одним узлом (раздел Г.5),
            // поэтому дверной откос считается по тем же таблицам.
            var effectiveNode = node == ThermalBridgeType.DoorReveal
                ? ThermalBridgeType.WindowReveal
                : node;

            // СП 230, раздел Г.7: «для кладок из блоков лёгкого, особо лёгкого и
            // ячеистого бетона или крупноформатных камней сопряжение стены
            // с совмещённым кровельным покрытием близко по характеристикам
            // к аналогичному сопряжению плит перекрытия со стеной и соответствующие
            // значения могут быть найдены по таблицам Г.5–Г.10».
            if (effectiveNode == ThermalBridgeType.Parapet &&
                construction == WallConstructionType.MasonryWithBrickFacing)
            {
                effectiveNode = ThermalBridgeType.FloorSlab;
            }

            string nodeKey = effectiveNode.ToString();

            var candidates = Tables.Where(t =>
                string.Equals(t.Node, nodeKey, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(t.Construction, construction.ToString(), StringComparison.OrdinalIgnoreCase))
                .ToList();

            // Для трёхслойной стены СП даёт свои таблицы только по части узлов
            // (плита перекрытия Г.11–Г.16, оконный узел Г.32). Для угла оно прямо
            // разрешает брать Г.28 («в отсутствие других данных таблицу Г.28 можно
            // применять и для трёхслойных стен»), а цокольный узел Г.40 в заголовке
            // сам назван общим для наружного утепления и трёхслойной стены.
            if (candidates.Count == 0 && construction == WallConstructionType.ThreeLayerBrickFacing)
            {
                candidates = Tables.Where(t =>
                    string.Equals(t.Node, nodeKey, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(t.Construction,
                        WallConstructionType.ExternalInsulationThinFacing.ToString(),
                        StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }

            // Для углов вариант (выпуклый/вогнутый) — обязательный признак,
            // для остальных узлов он различает исполнение узла.
            if (effectiveNode == ThermalBridgeType.ExternalCorner)
            {
                candidates = candidates
                    .Where(t => string.Equals(t.Variant, variant.ToString(), StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }

            // Исполнение узла задано явно — берём только его. Пустой результат честнее
            // подмены: инженер указал НТЭ, а мы посчитали бы по обычной перфорации.
            if (!string.IsNullOrWhiteSpace(selectors.Execution))
            {
                var byExecution = candidates
                    .Where(t => string.Equals(t.Variant, selectors.Execution,
                                              StringComparison.OrdinalIgnoreCase))
                    .ToList();
                if (byExecution.Count > 0)
                {
                    candidates = byExecution;
                }
                else
                {
                    Logger.Debug($"SP230: исполнение «{selectors.Execution}» для узла {nodeKey} " +
                                 "в каталоге не найдено — выбор идёт по остальным признакам");
                }
            }

            var equallyClose = PickClosest(candidates, selectors);
            if (equallyClose.Count == 0) return null;

            SP230TableDto table = null;
            double psi = 0;
            bool clamped = false;
            double? row = null, column = null;

            // Среди равных по числовым признакам берём таблицу с НАИБОЛЬШИМИ потерями.
            // Раньше выигрывала первая по порядку в JSON — то есть расчёт зависел от
            // порядка строк в ресурсе, а половина внесённых таблиц (НТЭ, положение
            // рамы, двухслойное утепление с прослойкой) была недостижима в принципе.
            foreach (var candidate in equallyClose)
            {
                double? r = profile.GetAxisValue(candidate.RowAxis) ?? selectors.GetAxisValue(candidate.RowAxis);
                double? c = profile.GetAxisValue(candidate.ColumnAxis) ?? selectors.GetAxisValue(candidate.ColumnAxis);
                if (r == null || c == null)
                {
                    Logger.Debug($"SP230: для таблицы {candidate.Table} не хватает параметров " +
                                 $"({candidate.RowAxis}={r}, {candidate.ColumnAxis}={c})");
                    continue;
                }

                bool candidateClamped;
                double candidatePsi = Interpolate(candidate, r.Value, c.Value, out candidateClamped);

                if (table == null || candidatePsi > psi)
                {
                    table = candidate;
                    psi = candidatePsi;
                    clamped = candidateClamped;
                    row = r;
                    column = c;
                }
            }

            if (table == null) return null;

            if (equallyClose.Count > 1)
            {
                Logger.Debug($"SP230: исполнение узла {nodeKey} не задано, равнозначных таблиц " +
                             $"{equallyClose.Count} — взято худшее по потерям: " +
                             $"{table.Table} «{table.Variant}», Ψ={psi:F3}");
            }

            return new SP230Lookup
            {
                Psi        = psi,
                TableId    = table.Table,
                Title      = table.Title,
                Execution  = table.Variant,
                IsExecutionAssumed = equallyClose.Count > 1 &&
                                     string.IsNullOrWhiteSpace(selectors.Execution),
                IsClamped  = clamped,
                RowAxis    = table.RowAxis,
                RowValue   = row.Value,
                ColumnAxis = table.ColumnAxis,
                ColumnValue = column.Value
            };
        }

        /// <summary>
        /// Выбирает таблицу, ближайшую по дискретным признакам (толщина рамы, зуб,
        /// нахлёст, R утеплителя на плите). Интерполяции между этими признаками нет:
        /// это конструктивные решения, а не непрерывные величины. Если признак
        /// у таблицы задан, а у вызывающего нет — таблица штрафуется, но не
        /// отбрасывается: лучше посчитать по ближайшему исполнению, чем никак.
        /// </summary>
        /// <returns>
        /// ВСЕ таблицы с минимальным расстоянием, а не одна: у Г.6 и Г.10 числовые
        /// признаки совпадают полностью, и выбирать между ними по порядку в файле
        /// нельзя. Разрешение ничьей — в <see cref="Find"/>, по величине потерь.
        /// </returns>
        private static List<SP230TableDto> PickClosest(List<SP230TableDto> candidates, BridgeSelectors selectors)
        {
            if (candidates.Count <= 1) return candidates;

            var scored = new List<KeyValuePair<double, SP230TableDto>>();
            double bestScore = double.MaxValue;

            foreach (var table in candidates)
            {
                double score = 0;
                score += SelectorDistance(table.FrameMm, selectors.FrameMm, 100.0);
                score += SelectorDistance(table.NotchMm, selectors.NotchMm, 60.0);
                score += SelectorDistance(table.OverlapMm, selectors.OverlapMm, 60.0);
                score += SelectorDistance(table.SlabInsulationR, selectors.SlabInsulationR, 5.0);
                score += SelectorDistance(table.SlabThicknessMm, selectors.SlabThicknessMm, 50.0);
                score += SelectorDistance(table.SlabPerforationRatio, selectors.SlabPerforationRatio, 5.0);
                score += SelectorDistance(table.BaseThicknessMm, selectors.BaseThicknessMm, 200.0);
                score += SelectorDistance(table.ParapetInsulationMm, selectors.ParapetInsulationMm, 500.0);
                score += SelectorDistance(table.WallInsulationR, selectors.WallInsulationR, 4.5);

                scored.Add(new KeyValuePair<double, SP230TableDto>(score, table));
                if (score < bestScore) bestScore = score;
            }

            const double tieEpsilon = 1e-9;
            return scored
                .Where(p => p.Key <= bestScore + tieEpsilon)
                .Select(p => p.Value)
                .ToList();
        }

        /// <summary>
        /// Расстояние по одному признаку, нормированное на характерный масштаб.
        /// Признак не задан ни у таблицы, ни у вызывающего — расстояние нулевое.
        /// Задан только у таблицы — небольшой штраф, чтобы при прочих равных
        /// выигрывала таблица без лишних условий.
        /// </summary>
        private static double SelectorDistance(double? tableValue, double? requested, double scale)
        {
            if (!tableValue.HasValue) return requested.HasValue ? 0.25 : 0;
            if (!requested.HasValue) return 0.5;
            return Math.Abs(tableValue.Value - requested.Value) / scale;
        }

        /// <summary>
        /// Удельные потери теплоты χ, Вт/°С, для ОДНОГО тарельчатого анкера —
        /// СП 230.1325800.2015 таблица Г.4.
        ///
        /// <para><b>Это точечный элемент, а не линейный.</b> Потери считаются
        /// НА ШТУКУ и умножаются на число анкеров, тогда как всё остальное
        /// в приложении Г — Ψ на метр. Поэтому анкер не проходит через
        /// <see cref="Find"/>: там сетка Ψ, а здесь ступенчатая таблица χ.</para>
        ///
        /// <para><b>Ось — L₁, расстояние от края стального распорного элемента
        /// до тарелки дюбеля, мм.</b> Чем глубже сталь утоплена в утеплитель,
        /// тем меньше потери. Таблица ступенчатая, интерполяции СП не даёт:
        /// значение берётся по интервалу, в который попал L₁.</para>
        ///
        /// <para>СП 230 раздел Г.4 отдельно оговаривает, что крепёж рядом с углом
        /// в Ψ угла НЕ входит — это самостоятельный теплозащитный элемент.</para>
        /// </summary>
        public static double AnchorChi(double l1Mm)
        {
            if (l1Mm <= 2)  return 0.006;
            if (l1Mm <= 6)  return 0.005;
            if (l1Mm <= 11) return 0.004;
            if (l1Mm <= 16) return 0.003;
            if (l1Mm <= 24) return 0.0025;
            if (l1Mm <= 40) return 0.002;
            if (l1Mm <= 70) return 0.0015;
            return 0.001;
        }

        /// <summary>Ссылка на таблицу анкера — идёт в отчёт вместе со значением.</summary>
        public const string AnchorReference = "СП 230.1325800.2015, таблица Г.4 (тарельчатый анкер)";

        /// <summary>Двумерная линейная интерполяция с зажимом по краям сетки.</summary>
        internal static double Interpolate(SP230TableDto table, double row, double column, out bool clamped)
        {
            int r0, r1; double rt;
            int c0, c1; double ct;
            bool rowClamped = Locate(table.RowValues, row, out r0, out r1, out rt);
            bool colClamped = Locate(table.ColumnValues, column, out c0, out c1, out ct);
            clamped = rowClamped || colClamped;

            double v00 = table.Values[r0][c0];
            double v01 = table.Values[r0][c1];
            double v10 = table.Values[r1][c0];
            double v11 = table.Values[r1][c1];

            double top    = v00 + (v01 - v00) * ct;
            double bottom = v10 + (v11 - v10) * ct;
            return top + (bottom - top) * rt;
        }

        /// <summary>
        /// Находит пару соседних узлов сетки и долю между ними.
        /// Возвращает true, если значение вышло за пределы сетки и было зажато.
        /// </summary>
        private static bool Locate(List<double> axis, double value, out int i0, out int i1, out double t)
        {
            if (value <= axis[0])
            {
                i0 = i1 = 0; t = 0;
                return value < axis[0];
            }
            if (value >= axis[axis.Count - 1])
            {
                i0 = i1 = axis.Count - 1; t = 0;
                return value > axis[axis.Count - 1];
            }
            for (int i = 1; i < axis.Count; i++)
            {
                if (value <= axis[i])
                {
                    i0 = i - 1; i1 = i;
                    t = (value - axis[i0]) / (axis[i1] - axis[i0]);
                    return false;
                }
            }
            i0 = i1 = axis.Count - 1; t = 0;
            return false;
        }

        private static SP230CatalogDto LoadResource()
        {
            try
            {
                var assembly = Assembly.GetExecutingAssembly();
                using (var stream = assembly.GetManifestResourceStream(ResourceName))
                {
                    if (stream == null)
                    {
                        Logger.Error($"SP230: встроенный ресурс {ResourceName} не найден", null);
                        return new SP230CatalogDto();
                    }
                    var serializer = new DataContractJsonSerializer(typeof(SP230CatalogDto));
                    var dto = (SP230CatalogDto)serializer.ReadObject(stream);
                    Logger.Info($"SP230: загружено таблиц {dto?.Tables?.Count ?? 0}");
                    return dto ?? new SP230CatalogDto();
                }
            }
            catch (Exception ex)
            {
                Logger.Error("SP230: не удалось прочитать встроенный каталог", ex);
                return new SP230CatalogDto();
            }
        }
    }

    /// <summary>Результат выборки из СП: значение и полный след того, откуда оно взято.</summary>
    public class SP230Lookup
    {
        public double Psi { get; set; }
        /// <summary>Номер таблицы СП 230, например «Г.27».</summary>
        public string TableId { get; set; }
        public string Title { get; set; }

        /// <summary>Исполнение узла, по которому взята таблица (поле Variant каталога).</summary>
        public string Execution { get; set; }

        /// <summary>
        /// Исполнение узла инженером не задано, и из нескольких равнозначных таблиц
        /// выбрана худшая по потерям. Значение нормативное, но конструктив — принятый
        /// «в запас», и в отчёте это надо показывать.
        /// </summary>
        public bool IsExecutionAssumed { get; set; }
        /// <summary>Параметр конструкции вышел за пределы сетки таблицы и был зажат по краю.</summary>
        public bool IsClamped { get; set; }
        public string RowAxis { get; set; }
        public double RowValue { get; set; }
        public string ColumnAxis { get; set; }
        public double ColumnValue { get; set; }

        public string Reference =>
            $"СП 230.1325800.2015, таблица {TableId}"
            + (IsExecutionAssumed ? $" (исполнение не задано, принято «{Execution}» — в запас)" : "")
            + (IsClamped ? " (параметр вне сетки, зажат по краю)" : "");
    }

    [DataContract]
    public class SP230CatalogDto
    {
        [DataMember(Order = 1)] public string Source { get; set; }
        [DataMember(Order = 2)] public string Note { get; set; }
        [DataMember(Order = 3)] public List<SP230TableDto> Tables { get; set; } = new List<SP230TableDto>();
    }

    [DataContract]
    public class SP230TableDto
    {
        [DataMember(Order = 1)] public string Table { get; set; }
        [DataMember(Order = 2)] public string Node { get; set; }
        [DataMember(Order = 3)] public string Variant { get; set; }
        [DataMember(Order = 4)] public string Construction { get; set; }
        [DataMember(Order = 5)] public string Title { get; set; }
        // Дискретные признаки исполнения узла. null — признак к таблице неприменим.
        [DataMember(Order = 6)] public double? FrameMm { get; set; }
        [DataMember(Order = 7)] public double? NotchMm { get; set; }
        [DataMember(Order = 8)] public double? OverlapMm { get; set; }
        [DataMember(Order = 9)] public double? SlabInsulationR { get; set; }
        [DataMember(Order = 10)] public double? SlabThicknessMm { get; set; }
        [DataMember(Order = 11)] public double? SlabPerforationRatio { get; set; }
        [DataMember(Order = 12)] public double? BaseThicknessMm { get; set; }
        [DataMember(Order = 13)] public double? ParapetInsulationMm { get; set; }
        [DataMember(Order = 14)] public double? WallInsulationR { get; set; }
        [DataMember(Order = 15)] public string RowAxis { get; set; }
        [DataMember(Order = 16)] public List<double> RowValues { get; set; } = new List<double>();
        [DataMember(Order = 17)] public string ColumnAxis { get; set; }
        [DataMember(Order = 18)] public List<double> ColumnValues { get; set; } = new List<double>();
        [DataMember(Order = 19)] public List<List<double>> Values { get; set; } = new List<List<double>>();
    }
}
