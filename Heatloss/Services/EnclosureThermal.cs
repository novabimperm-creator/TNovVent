using System;
using System.Collections.Generic;

namespace QOVETER.Services
{
    /// <summary>
    /// Сборка сопротивления ограждения из НЕСКОЛЬКИХ элементов модели.
    ///
    /// Класс намеренно не знает про Revit: что именно стоит перед несущей
    /// конструкцией, определяет <see cref="GeometryCollector"/> геометрией
    /// модели, а как из этого получается U — считается здесь и проверяется
    /// автотестами без Revit.
    ///
    /// <para><b>Зачем это вообще нужно.</b> В российских моделях фасадная система
    /// живёт ОТДЕЛЬНЫМИ элементами перед несущей стеной: «Наруж стена
    /// Силикатныйблок250», перед ней «Фасад ГИх2 Пеноплэкс100 Мембрана»,
    /// перед ней вентзазор и облицовка. Расчёт, берущий ОДИН элемент,
    /// обязательно ошибается: возьмёт кладку — потеряет весь утеплитель,
    /// возьмёт утеплитель — потеряет кладку. На 76-СУЗДАЛ.23 (прогон 2026-08-12)
    /// так шло 72% площади наружных стен: 5 785 м² считались по типовому
    /// <see cref="ThermalConstants.WallUDefault"/>, хотя в модели лежат
    /// настоящие слои с R = 3,03.</para>
    ///
    /// <para><b>Сопротивления теплоотдаче — один раз на сборку.</b> У каждого
    /// элемента берётся R его ТЕЛА (<see cref="BodyRFromU"/>), а Rsi+Rse
    /// добавляются к сумме однажды: иначе каждый лишний элемент модели
    /// добавлял бы 0,17 из воздуха.</para>
    /// </summary>
    public static class EnclosureThermal
    {
        /// <summary>
        /// R тела конструкции (без сопротивлений теплоотдаче) из её U.
        /// </summary>
        public static double BodyRFromU(double uValue)
        {
            if (uValue <= 0) return 0;
            return Math.Max(0, 1.0 / uValue - ThermalConstants.RsiPlusRse);
        }

        /// <summary>
        /// U сборки, Вт/(м²·К), из R тел её слоёв. Пустая сборка невозможна —
        /// вызывающий обязан проверить, что хоть один слой посчитан.
        /// </summary>
        public static double UValueFromBodyR(IEnumerable<double> bodyR)
        {
            double r = ThermalConstants.RsiPlusRse;
            if (bodyR != null)
            {
                foreach (double layer in bodyR)
                    if (layer > 0) r += layer;
            }
            return 1.0 / r;
        }

        /// <summary>
        /// Отступ от границы помещения, с которого начинается поиск фасадной
        /// системы, м. Проба обязана стартовать сразу ЗА внешней гранью несущей
        /// стены: ближе она попадёт в саму стену, дальше — перескочит утеплитель.
        ///
        /// <para><b>Слагаемых два только когда элемента два.</b> Границу помещения
        /// в российских моделях обычно образует отдельный отделочный слой
        /// («Отделка Штук15»), а несущая стена стоит за ним ВТОРЫМ элементом —
        /// тогда до её внешней грани идут обе толщины. Но отделка есть не везде:
        /// где её нет, несущая стена и есть граничная, ОДИН элемент, и вторая
        /// толщина берётся из воздуха.</para>
        ///
        /// <para><b>Цена ошибки измерена</b> на 76-СУЗДАЛ.23, прогон 2026-08-17:
        /// силикатный блок 250 мм при двойном счёте давал старт 0,52 м вместо
        /// 0,27 — а утеплитель 140 мм лежит на 0,25…0,39 м, то есть проба
        /// начиналась уже ЗА ним. В сводке это видно прямо: у одного и того же
        /// типа стены на улицу 1 370,6 м² собрались с U = 0,30, а 479,7 м²
        /// остались голой кладкой с U = 2,19; разница ровно в том, стоит ли
        /// перед блоком отделочный слой.</para>
        /// </summary>
        /// <param name="boundaryWidthM">Толщина стены, образующей границу помещения, м.</param>
        /// <param name="structuralWidthM">Толщина несущей стены, м.</param>
        /// <param name="sameElement">
        /// Граничная и несущая стена — один элемент модели (отделочного слоя нет).
        /// </param>
        public static double FacadeProbeStartM(double boundaryWidthM, double structuralWidthM,
                                               bool sameElement)
        {
            const double clearanceM = 0.02;
            double behind = sameElement
                ? Math.Max(boundaryWidthM, structuralWidthM)
                : boundaryWidthM + structuralWidthM;
            return Math.Max(0, behind) + clearanceM;
        }

        /// <summary>
        /// U колонны, Вт/(м²·К).
        ///
        /// У колонны нет <c>CompoundStructure</c> — это монолит одного материала,
        /// поэтому её собственное R = d/λ, куда добавляется R найденного
        /// перед ней покрытия.
        ///
        /// <para>Колонна в наружной стене бывает и голой — тогда это сильнейший
        /// мостик холода, U ≈ 3,4 против 0,51 у стены, — и закрытой снаружи тем же
        /// пирогом, что и стена. Какой из двух случаев на объекте, решает модель,
        /// а не настройка: плагин универсальный, и оба случая встречаются
        /// в реальных проектах.</para>
        /// </summary>
        public static double ColumnUValue(double thicknessM, double conductivity,
                                          IEnumerable<double> coverBodyR = null)
        {
            if (thicknessM <= 0) thicknessM = ThermalConstants.ColumnThicknessDefaultM;
            if (conductivity <= 0.01) conductivity = ThermalConstants.ReinforcedConcreteConductivity;

            var layers = new List<double> { thicknessM / conductivity };
            if (coverBodyR != null) layers.AddRange(coverBodyR);

            return UValueFromBodyR(layers);
        }
    }
}
