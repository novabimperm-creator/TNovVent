using QOVETER.Services;
using System;
using System.Collections.Generic;
using System.Linq;

namespace QOVETER.Models
{
    /// <summary>
    /// Параметры конструкции наружной стены, по которым выбирается строка и столбец
    /// в таблицах приложения Г СП 230.1325800.2015.
    ///
    /// Заполняется автоматически из слоёв <c>CompoundStructure</c>; те параметры,
    /// которые из модели не вытаскиваются (неполные свойства материалов, стена
    /// смоделирована одним слоем), задаются вручную в настройках расчёта.
    /// </summary>
    public class WallConstructionProfile
    {
        /// <summary>Тип конструкции — определяет, какую таблицу СП брать.</summary>
        public WallConstructionType Construction { get; set; } = WallConstructionType.Unknown;

        /// <summary>
        /// Откуда взялась конструкция. Ψ без указания источника непроверяемо — то же
        /// правило, что у исполнения узла и у λ материала: значение, принятое
        /// допущением, обязано отличаться в отчёте от прочитанного из модели.
        /// </summary>
        public WallConstructionOrigin Origin { get; set; } = WallConstructionOrigin.FromLayers;

        /// <summary>Толщина кладки d_кл, мм (ось таблицы Г.27).</summary>
        public double? WallThicknessMm { get; set; }

        /// <summary>
        /// Теплопроводность основания λ_о (λ_кам для кладки), Вт/(м·°С) —
        /// столбец таблиц Г.27 и Г.28. Это внутренний несущий слой, а не утеплитель.
        /// </summary>
        public double? BaseConductivity { get; set; }

        /// <summary>Термическое сопротивление слоя утеплителя R_ут, м²·°С/Вт (ось таблицы Г.28).</summary>
        public double? InsulationResistance { get; set; }

        /// <summary>
        /// Толщина основания стены d_о, мм — признак выбора таблицы для узла плиты
        /// перекрытия при внутреннем утеплении (Г.24–Г.26, значения 200 и 400 мм).
        /// </summary>
        public double? BaseThicknessMm { get; set; }

        /// <summary>
        /// Комплексный параметр облицовочного листа панели: толщина × теплопроводность,
        /// Вт/°С — ось таблиц Г.50–Г.52 (парапет тонкостенных панелей). Сетка СП
        /// охватывает и гипсоволокнистые или цементно-стружечные листы, и стальную
        /// облицовку сэндвич-панелей вплоть до 2,2 мм. По примечанию СП именно этот
        /// параметр влияет на узел основным образом.
        /// </summary>
        public double? FacingComplexParameter { get; set; }

        /// <summary>
        /// Минимальная добавка, при которой стену имеет смысл считать утеплённой,
        /// м²·°С/Вт. Ниже этого порога разница между сборкой и нормой — счётный
        /// шум (округления, сопротивления теплоотдаче), а не слой утеплителя.
        /// </summary>
        public const double MinimumInsulationR = 0.5;

        /// <summary>
        /// Достраивает утеплитель, которого нет в модели, но который требует норматив.
        ///
        /// <para><b>Зачем.</b> Для U этот пробел уже закрыт: ограждение к наружному
        /// воздуху не может быть хуже базового требуемого сопротивления
        /// (<see cref="Services.NormativeResistance"/>, СП 50.13330.2012 табл. 3) —
        /// дом с такой стеной не прошёл бы экспертизу, значит утеплителя нет
        /// В МОДЕЛИ, а не в доме. Но КОНСТРУКЦИЯ при этом оставалась «голой кладкой»:
        /// в одном месте расчёта мы признавали, что утеплитель обязан быть,
        /// а в соседнем считали, что его нет.</para>
        ///
        /// <para><b>Чего это стоило</b> на 76-СУЗДАЛ.23 (прогон 2026-08-19 15:36):
        /// у 173 помещений конструкция вышла <see cref="WallConstructionType.MasonryWithBrickFacing"/>,
        /// и они получили узел плиты перекрытия (181 шт, Σ(Ψ·l) = 85,9 Вт/К),
        /// которого при наружном утеплении по СП 230 раздел Г.3 нет вовсе,
        /// а оконный откос посчитался по таблице Г.30 (кладка) вместо Г.33 (СФТК) —
        /// при том что исполнение узла инженер уточнил у АР именно для Г.33.</para>
        ///
        /// <para><b>Как считается R утеплителя.</b> Из сопротивления ТЕЛА всей
        /// конструкции после подъёма до нормы вычитается сопротивление основания:
        /// <c>R_ут = (1/U − Rsi − Rse) − d_осн/λ_осн</c>. Проверка на реальной стене:
        /// силикатный блок 250 (λ_Б 0,87) при нормируемом R = 3,17 даёт R_ут = 2,71,
        /// а найденный в модели утеплитель 140 мм — 2,92. Два независимых пути
        /// сходятся, и это тот же приём сверки, что был с U = 0,30 против 0,316.</para>
        ///
        /// <para>Ничего не достраивается, если утеплитель в сборке УЖЕ найден,
        /// если основание неизвестно (по чему тогда вычитать) или если добавка
        /// меньше <see cref="MinimumInsulationR"/>.</para>
        /// </summary>
        /// <param name="uAfterNormativeFloor">U ограждения после подъёма до нормируемого, Вт/(м²·К).</param>
        public WallConstructionProfile WithNormativeInsulation(double uAfterNormativeFloor)
        {
            if (uAfterNormativeFloor <= 0) return this;
            if (InsulationResistance.HasValue) return this;
            if (!BaseConductivity.HasValue || BaseConductivity.Value <= 0) return this;
            if (!BaseThicknessMm.HasValue || BaseThicknessMm.Value <= 0) return this;

            double bodyR = Services.EnclosureThermal.BodyRFromU(uAfterNormativeFloor);
            double baseR = (BaseThicknessMm.Value / 1000.0) / BaseConductivity.Value;
            double insulationR = bodyR - baseR;

            if (insulationR < MinimumInsulationR) return this;

            return new WallConstructionProfile
            {
                Construction = WallConstructionType.ExternalInsulationThinFacing,
                Origin = WallConstructionOrigin.NormativeInsulation,
                BaseConductivity = BaseConductivity,
                BaseThicknessMm = BaseThicknessMm,
                WallThicknessMm = WallThicknessMm,
                InsulationResistance = insulationR,
                FacingComplexParameter = FacingComplexParameter
            };
        }

        /// <summary>Та же конструкция, но с другой пометкой об источнике.</summary>
        public WallConstructionProfile WithOrigin(WallConstructionOrigin origin)
        {
            return new WallConstructionProfile
            {
                Construction = Construction,
                Origin = origin,
                BaseConductivity = BaseConductivity,
                BaseThicknessMm = BaseThicknessMm,
                WallThicknessMm = WallThicknessMm,
                InsulationResistance = InsulationResistance,
                FacingComplexParameter = FacingComplexParameter
            };
        }

        /// <summary>
        /// Подпись конструкции для группировки по объекту: тип плюс все числовые оси,
        /// по которым СП выбирает строку таблицы. Две стены с одним типом, но разной
        /// λ основания — разные конструкции, и складывать их площади нельзя.
        /// </summary>
        public string Signature
        {
            get
            {
                return string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "{0}|{1:F3}|{2:F0}|{3:F0}|{4:F2}",
                    Construction, BaseConductivity ?? 0, BaseThicknessMm ?? 0,
                    WallThicknessMm ?? 0, InsulationResistance ?? 0);
            }
        }

        /// <summary>Заполнено ли достаточно, чтобы вообще искать значение в СП.</summary>
        public bool IsUsable =>
            Construction != WallConstructionType.Unknown &&
            (WallThicknessMm.HasValue || InsulationResistance.HasValue) &&
            BaseConductivity.HasValue;

        /// <summary>Значение оси таблицы по её имени из JSON-каталога.</summary>
        public double? GetAxisValue(string axis)
        {
            switch (axis)
            {
                case "WallThicknessMm":         return WallThicknessMm;
                case "BaseConductivity":        return BaseConductivity;
                case "InsulationResistance":    return InsulationResistance;
                case "BaseThicknessMm":         return BaseThicknessMm;
                case "FacingComplexParameter":  return FacingComplexParameter;
                default:                        return null;
            }
        }

        /// <summary>
        /// Разбирает слои стены и определяет тип конструкции с параметрами.
        ///
        /// Правила распознавания (по разбивке приложения А СП 230):
        ///   • есть выраженный слой утеплителя (λ ≤ 0.1) снаружи от несущего →
        ///     стена с наружным утеплением, таблица Г.28;
        ///   • утеплитель внутри (ближе к помещению), чем несущий слой →
        ///     внутреннее утепление: углы по СП не учитываются;
        ///   • утеплителя нет, есть массивная кладка → кладка с облицовкой, таблица Г.27;
        ///   • общая толщина мала (менее 200 мм) и есть утеплитель → тонкостенная панель:
        ///     углы по СП не учитываются.
        ///
        /// Слои перечисляются СНАРУЖИ ВНУТРЬ либо изнутри наружу — направление задаётся
        /// параметром, потому что в Revit порядок зависит от того, как смоделирован тип.
        /// </summary>
        public static WallConstructionProfile FromLayers(IList<WallLayer> layers, bool outsideFirst = true)
        {
            var profile = new WallConstructionProfile();
            if (layers == null || layers.Count == 0) return profile;

            var ordered = outsideFirst ? layers.ToList() : layers.Reverse().ToList();

            // Утеплитель — слой с наименьшей теплопроводностью, если она достаточно мала.
            var insulation = ordered
                .Where(l => l.Conductivity > 0 && l.Conductivity <= 0.1 && l.ThicknessM > 0.01)
                .OrderBy(l => l.Conductivity)
                .FirstOrDefault();

            // Несущее основание — самый толстый слой из НЕутеплителя.
            var baseLayer = ordered
                .Where(l => l != insulation && l.Conductivity > 0.1 && l.ThicknessM > 0.03)
                .OrderByDescending(l => l.ThicknessM)
                .FirstOrDefault();

            if (baseLayer != null)
            {
                profile.BaseConductivity = baseLayer.Conductivity;
                // Толщина основания — ось таблиц Г.24–Г.26 (внутреннее утепление).
                profile.BaseThicknessMm = Math.Round(baseLayer.ThicknessM * 1000.0, 1);
            }

            double totalThickness = ordered.Sum(l => l.ThicknessM);

            if (insulation != null)
                profile.InsulationResistance = insulation.ThicknessM / insulation.Conductivity;

            // Тонкостенная панель распознаётся ДО проверки на несущий слой:
            // у сэндвича его нет вовсе — только два тонких листа облицовки
            // и утеплитель между ними.
            if (insulation != null && totalThickness < 0.2)
            {
                profile.Construction = WallConstructionType.ThinPanel;

                // Комплексный параметр облицовки панели — толщина × теплопроводность
                // наружного листа. Для сэндвича со стальной облицовкой 0,5 мм
                // (λ ≈ 58) это ≈ 0,029 Вт/°С, что попадает в середину сетки СП.
                var facing = ordered.FirstOrDefault(l => l.Conductivity > 0.1 && l.ThicknessM > 0);
                if (facing != null)
                    profile.FacingComplexParameter = facing.ThicknessM * facing.Conductivity;

                return profile;
            }

            if (insulation != null && baseLayer != null)
            {
                int insulationIndex = ordered.IndexOf(insulation);
                int baseIndex = ordered.IndexOf(baseLayer);

                if (insulationIndex < baseIndex)
                {
                    // Утеплитель снаружи от несущего слоя. Отличаем трёхслойную стену
                    // с облицовкой кирпичом от штукатурного или вентилируемого фасада:
                    // у трёхслойной снаружи от утеплителя лежит СВОЙ массивный слой
                    // кладки, а у СФТК — тонкая штукатурка или экран.
                    // У этих конструкций разные таблицы для узла плиты перекрытия
                    // (Г.11–Г.16 против «не учитывать вовсе»).
                    var outerLayer = ordered
                        .Take(insulationIndex)
                        .Where(l => l.Conductivity > 0.1)
                        .OrderByDescending(l => l.ThicknessM)
                        .FirstOrDefault();

                    profile.Construction = outerLayer != null && outerLayer.ThicknessM >= 0.08
                        ? WallConstructionType.ThreeLayerBrickFacing
                        : WallConstructionType.ExternalInsulationThinFacing;
                }
                else
                {
                    profile.Construction = WallConstructionType.InternalInsulation;
                }
            }
            else if (baseLayer != null)
            {
                profile.Construction = WallConstructionType.MasonryWithBrickFacing;
                profile.WallThicknessMm = baseLayer.ThicknessM * 1000.0;
            }

            return profile;
        }
    }

    /// <summary>
    /// Откуда взята конструкция ограждения для выбора таблиц приложения Г СП 230.
    ///
    /// <para>Различать обязательно. До 2026-08-20 всё, что не разобралось по слоям,
    /// сваливалось в <see cref="WallConstructionType.Unknown"/> — 231 помещение
    /// из 585 на 76-СУЗДАЛ.23, — и сводка прогона одинаково называла три разных
    /// положения дел: у помещения нет наружных ограждений вовсе (узлов и быть
    /// не может), ограждения есть, но все к лоджии или шахте, и ограждение
    /// на улицу есть, а в модели от него осталась одна отделка 15 мм.
    /// Первое — не недолёт, два других — недолёты разной природы и с разной ценой.</para>
    /// </summary>
    public enum WallConstructionOrigin
    {
        /// <summary>Слои сборки разобраны — конструкция прочитана из модели.</summary>
        FromLayers = 0,

        /// <summary>
        /// Утеплителя в модели нет, но U поднят до нормируемого — утеплитель достроен
        /// вычитанием основания из тела нормируемой стены (СП 50.13330.2012 табл. 3).
        /// </summary>
        NormativeInsulation,

        /// <summary>
        /// Уличных ограждений у помещения нет: конструкция взята с ограждения
        /// к лоджии, шахте или лестнице — того самого, через которое и идут потери.
        /// </summary>
        NonStreetEnclosure,

        /// <summary>
        /// Ограждение на улицу есть, но в модели от него остался только отделочный
        /// слой: несущая стена за ним не найдена, разбирать нечего. Принята
        /// ПРЕОБЛАДАЮЩАЯ наружная конструкция объекта — та же, что у остальных стен
        /// этого дома. Допущение, и в отчёте оно названо.
        /// </summary>
        DominantOfObject
    }

    /// <summary>Слой стены для классификации конструкции: толщина и теплопроводность.</summary>
    public class WallLayer
    {
        public string Material { get; set; }
        /// <summary>Толщина слоя, м.</summary>
        public double ThicknessM { get; set; }
        /// <summary>Теплопроводность λ, Вт/(м·°С). 0 — не прочиталась из материала.</summary>
        public double Conductivity { get; set; }
    }
}
