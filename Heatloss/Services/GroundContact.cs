using System;
using System.Collections.Generic;
using System.Linq;

namespace QOVETER.Services
{
    /// <summary>
    /// Ограждение, контактирующее с грунтом: пол по грунту либо стена в грунте.
    /// У них РАЗНЫЕ таблицы базовых сопротивлений (Г.3 и Г.4).
    /// </summary>
    public enum GroundEnclosure
    {
        /// <summary>Пол по грунту — таблица Г.3.</summary>
        Floor,

        /// <summary>Стена в грунте (заглублённая) — таблица Г.4.</summary>
        Wall
    }

    /// <summary>Площадь одной зоны, м².</summary>
    public struct GroundZoneArea
    {
        /// <summary>Номер зоны: 1…4.</summary>
        public int Zone;

        /// <summary>Площадь, попавшая в эту зону, м².</summary>
        public double AreaM2;

        public GroundZoneArea(int zone, double areaM2)
        {
            Zone = zone;
            AreaM2 = areaM2;
        }
    }

    /// <summary>
    /// Теплопотери через ограждения в грунте по ЗОНАЛЬНОЙ методике
    /// СП 50.13330.2024, приложение Г, пункт Г.7 «Инженерная методика расчета
    /// теплопотерь через ограждающие конструкции (стены и пол) в грунте».
    ///
    /// <para><b>Зачем это здесь.</b> До 2026-08-19 пол первого этажа считался через
    /// подставленную «температуру под полом» 5 °C (<c>CalculationParameters.FloorTemperature</c>),
    /// а заглублённые ограждения — по температуре НАРУЖНОГО ВОЗДУХА без всякого учёта
    /// грунта. Из-за второго подвал пришлось снять с расчёта целиком: его стены
    /// получали полную ΔT, как будто дом стоит на сваях. Зональный метод закрывает
    /// оба случая и не требует ни решателя, ни данных, которых нет в модели.</para>
    ///
    /// <para><b>Суть метода.</b> Ограждение делится полосами по 2 м вдоль контура
    /// здания; каждая полоса — зона со своим сопротивлением теплопередаче, и оно
    /// тем выше, чем дальше от контура: путь теплоты через грунт длиннее. Всё,
    /// что не попало в первые три зоны, относится к четвёртой. Сопротивление зоны
    /// по формулам (Г.16) и (Г.18):</para>
    ///
    /// <code>R_i = R_баз_i · (1,6 / λ_гр) + δ_ут / λ_ут</code>
    ///
    /// <para>где 1,6 Вт/(м·°С) — базовая расчётная теплопроводность грунта, которую
    /// СП предписывает принимать при отсутствии документального подтверждения иной.</para>
    ///
    /// <para><b>Температура за ограждением — НАРУЖНОГО ВОЗДУХА, а не грунта.</b>
    /// Демпфирование грунта уже сидит в самих R по зонам, поэтому второй раз
    /// его учитывать нельзя: ΔT берётся такая же, как для стен. Это видно из
    /// примечания в конце Г.7 — для ГОДОВОГО потребления СП велит заменять среднюю
    /// температуру отопительного периода на среднегодовую именно потому, что
    /// в остальном расчёт идёт по температуре наружного воздуха.</para>
    ///
    /// <para><b>Чего здесь намеренно нет.</b> Слагаемые Ψ·L из формул (Г.15) и (Г.17)
    /// — удельные потери в месте стыка пола со стеной по СП 230. Стык считается
    /// каталогом мостиков (<see cref="ReducedResistanceCalculator"/>), и складывать
    /// его сюда значило бы посчитать узел дважды.</para>
    /// </summary>
    public static class GroundContact
    {
        /// <summary>Ширина зоны (полосы), м. СП 50.13330.2024 Г.7.</summary>
        public const double ZoneWidthM = 2.0;

        /// <summary>Зон всего четыре: в четвёртую относят всё, что не попало в первые три.</summary>
        public const int ZoneCount = 4;

        /// <summary>
        /// Базовая расчётная теплопроводность грунта, Вт/(м·°С). СП 50.13330.2024,
        /// формулы (Г.16) и (Г.18): принимается 1,6 «в случае отсутствия
        /// документального подтверждения иной расчётной теплопроводности грунта,
        /// граничащего с фундаментом здания».
        /// </summary>
        public const double BaseSoilConductivity = 1.6;

        /// <summary>
        /// Таблица Г.3 — базовые сопротивления теплопередаче зон для ПОЛА по грунту,
        /// (м²·°С)/Вт. Индекс 0 — зона I.
        /// </summary>
        public static readonly IReadOnlyList<double> FloorBaseResistance =
            new[] { 2.1, 3.8, 5.2, 7.7 };

        /// <summary>
        /// Таблица Г.4 — базовые сопротивления теплопередаче зон для СТЕН в грунте,
        /// (м²·°С)/Вт. Индекс 0 — зона I.
        /// </summary>
        public static readonly IReadOnlyList<double> WallBaseResistance =
            new[] { 1.05, 1.9, 2.6, 3.85 };

        /// <summary>Базовое сопротивление зоны по таблице Г.3 либо Г.4.</summary>
        public static double BaseResistance(GroundEnclosure enclosure, int zone)
        {
            var table = enclosure == GroundEnclosure.Wall ? WallBaseResistance : FloorBaseResistance;
            int index = Clamp(zone, 1, ZoneCount) - 1;
            return table[index];
        }

        /// <summary>
        /// Сопротивление теплопередаче зоны, (м²·°С)/Вт — формулы (Г.16) и (Г.18).
        /// </summary>
        /// <param name="enclosure">Пол по грунту либо стена в грунте.</param>
        /// <param name="zone">Номер зоны 1…4.</param>
        /// <param name="soilConductivity">
        /// λ грунта, Вт/(м·°С). Ноль или меньше — принимается базовая 1,6 по СП.
        /// </param>
        /// <param name="insulationResistance">
        /// Слагаемое δ_ут/λ_ут из формул (Г.16) и (Г.18) — термическое сопротивление
        /// САМОЙ КОНСТРУКЦИИ пола или стены, м²·°С/Вт, без сопротивлений теплоотдаче.
        ///
        /// <para>Принимается готовым сопротивлением, а не парой «толщина и λ»:
        /// конструкция в модели многослойная, и её R уже считает
        /// <c>WallThermalCalculator.CalculateLayeredR</c>. Ноль — данных о полах
        /// нет; тогда считается голая зона, и это оценка В ЗАПАС.</para>
        /// </param>
        public static double ZoneResistance(GroundEnclosure enclosure, int zone,
                                            double soilConductivity = BaseSoilConductivity,
                                            double insulationResistance = 0)
        {
            double lambdaSoil = soilConductivity > 0 ? soilConductivity : BaseSoilConductivity;

            double r = BaseResistance(enclosure, zone) * (BaseSoilConductivity / lambdaSoil);

            if (insulationResistance > 0) r += insulationResistance;

            return r;
        }

        /// <summary>
        /// Номер зоны для точки, удалённой на <paramref name="distanceM"/> от контура
        /// здания. Полосы по 2 м; всё, что дальше третьей полосы, — четвёртая зона.
        /// </summary>
        /// <param name="distanceM">Расстояние от контура здания, м.</param>
        /// <param name="effectiveBandM">
        /// Эффективная полоса для пола НИЖЕ уровня земли, м. По Г.7: «пол по грунту
        /// наращивается эффективной полосой вдоль контура здания, шириной равной
        /// половине средней высоты стен в грунте. Отсчёт зон начинают с эффективной
        /// полосы» — то есть пол подвала начинается не с первой зоны, а с той,
        /// куда попала бы точка, отстоящая от контура на эту полосу.
        /// </param>
        public static int ZoneOf(double distanceM, double effectiveBandM = 0)
        {
            double d = Math.Max(0, distanceM) + Math.Max(0, effectiveBandM);

            // Проверка ДО приведения к int, а не после. Помещение в глубине здания
            // может не иметь рядом ни одного отрезка контура — тогда расстояние
            // приходит как double.MaxValue, а (int)(MaxValue/2) переполняется
            // в int.MinValue и зажимается в ПЕРВУЮ зону: самое холодное место
            // дома объявлялось самым тёплым. Поймано тестом
            // Test_InnerRoomIsZoneFour.
            if (double.IsNaN(d)) return ZoneCount;
            if (d >= ZoneWidthM * (ZoneCount - 1)) return ZoneCount;

            int zone = (int)Math.Floor(d / ZoneWidthM) + 1;
            return Clamp(zone, 1, ZoneCount);
        }

        /// <summary>
        /// Удельный тепловой поток ограждения в грунте, Вт/°С — знаменатель формул
        /// (Г.15) и (Г.17): Σ A_i / R_i.
        ///
        /// <para>Считается именно он, а не R приведённое: сумма проводимостей
        /// складывается с остальными ограждениями помещения напрямую, а R_пр —
        /// величина для отчёта, и получается из неё же делением площади.</para>
        /// </summary>
        public static double Conductance(GroundEnclosure enclosure,
                                         IEnumerable<GroundZoneArea> zones,
                                         double soilConductivity = BaseSoilConductivity,
                                         double insulationResistance = 0)
        {
            if (zones == null) return 0;

            double sum = 0;
            foreach (var zone in zones)
            {
                if (zone.AreaM2 <= 0) continue;
                double r = ZoneResistance(enclosure, zone.Zone, soilConductivity, insulationResistance);
                if (r > 0) sum += zone.AreaM2 / r;
            }
            return sum;
        }

        /// <summary>
        /// Приведённое сопротивление теплопередаче ограждения в грунте, (м²·°С)/Вт —
        /// формулы (Г.15) и (Г.17) без слагаемых Ψ·L (стык считается каталогом
        /// мостиков, см. описание класса). Ноль — площадей нет.
        /// </summary>
        public static double ReducedResistance(GroundEnclosure enclosure,
                                               IEnumerable<GroundZoneArea> zones,
                                               double soilConductivity = BaseSoilConductivity,
                                               double insulationResistance = 0)
        {
            var list = (zones ?? Enumerable.Empty<GroundZoneArea>()).ToList();
            double area = list.Sum(z => Math.Max(0, z.AreaM2));
            if (area <= 0) return 0;

            double conductance = Conductance(enclosure, list, soilConductivity, insulationResistance);
            return conductance > 0 ? area / conductance : 0;
        }

        /// <summary>
        /// Теплопотери через ограждение в грунте, Вт.
        /// </summary>
        /// <param name="deltaT">
        /// t_в − t_н, °С. Именно наружного воздуха: демпфирование грунта уже учтено
        /// в сопротивлениях зон, см. описание класса.
        /// </param>
        public static double HeatLossW(GroundEnclosure enclosure,
                                       IEnumerable<GroundZoneArea> zones, double deltaT,
                                       double soilConductivity = BaseSoilConductivity,
                                       double insulationResistance = 0)
        {
            if (deltaT <= 0) return 0;
            return Conductance(enclosure, zones, soilConductivity, insulationResistance) * deltaT;
        }

        /// <summary>
        /// Разбивает СТЕНУ В ГРУНТЕ на зоны: полосы вдоль контура здания высотой 2 м,
        /// отсчёт сверху, от уровня земли (СП 50.13330.2024 Г.7, рисунок Г.2).
        /// </summary>
        /// <param name="lengthM">Длина стены по контуру, м.</param>
        /// <param name="topDepthM">Глубина верха стены от уровня земли, м (0 — от земли).</param>
        /// <param name="bottomDepthM">Глубина низа стены от уровня земли, м.</param>
        public static List<GroundZoneArea> SplitWallByDepth(double lengthM,
                                                            double topDepthM, double bottomDepthM)
        {
            var result = new List<GroundZoneArea>();
            if (lengthM <= 0) return result;

            double top = Math.Max(0, Math.Min(topDepthM, bottomDepthM));
            double bottom = Math.Max(0, Math.Max(topDepthM, bottomDepthM));
            if (bottom - top <= 1e-9) return result;

            for (int zone = 1; zone <= ZoneCount; zone++)
            {
                double bandTop = (zone - 1) * ZoneWidthM;
                // Четвёртая зона — всё, что глубже трёх полос.
                double bandBottom = zone == ZoneCount ? double.MaxValue : zone * ZoneWidthM;

                double overlap = Math.Min(bottom, bandBottom) - Math.Max(top, bandTop);
                if (overlap > 1e-9) result.Add(new GroundZoneArea(zone, overlap * lengthM));
            }

            return result;
        }

        /// <summary>
        /// Собирает одинаковые зоны в одну строку — чтобы в отчёт и журнал шли
        /// четыре числа, а не список длиной в количество сегментов.
        /// </summary>
        public static List<GroundZoneArea> Merge(IEnumerable<GroundZoneArea> zones)
        {
            return (zones ?? Enumerable.Empty<GroundZoneArea>())
                .Where(z => z.AreaM2 > 0)
                .GroupBy(z => Clamp(z.Zone, 1, ZoneCount))
                .OrderBy(g => g.Key)
                .Select(g => new GroundZoneArea(g.Key, g.Sum(z => z.AreaM2)))
                .ToList();
        }

        /// <summary>Строка для журнала: площади и сопротивления по зонам.</summary>
        public static string Describe(GroundEnclosure enclosure, IEnumerable<GroundZoneArea> zones,
                                      double soilConductivity = BaseSoilConductivity,
                                      double insulationResistance = 0)
        {
            var merged = Merge(zones);
            if (merged.Count == 0) return "зон нет";

            return string.Join(", ", merged.Select(z =>
                $"зона {Roman(z.Zone)} {z.AreaM2:F1} м² " +
                $"(R={ZoneResistance(enclosure, z.Zone, soilConductivity, insulationResistance):F2})"));
        }

        private static string Roman(int zone)
        {
            switch (zone)
            {
                case 1:  return "I";
                case 2:  return "II";
                case 3:  return "III";
                default: return "IV";
            }
        }

        private static int Clamp(int value, int min, int max) =>
            value < min ? min : (value > max ? max : value);
    }
}
