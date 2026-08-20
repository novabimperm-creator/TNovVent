using System.Collections.Generic;
using System.Globalization;

namespace QOVETER.Services
{
    /// <summary>Что за наружной гранью ограждения.</summary>
    public enum BeyondWall
    {
        /// <summary>Наружный воздух.</summary>
        OutdoorAir,

        /// <summary>
        /// Замкнутая пустота ВНУТРИ здания: вентиляционная или лифтовая шахта,
        /// технический короб. Не улица, и полной ΔT там быть не может.
        /// </summary>
        Shaft
    }

    /// <summary>Что нашла проба на одном шаге марша наружу.</summary>
    public struct VoidProbeSample
    {
        public VoidProbeSample(double distanceM, bool hasWall, bool hasRoom)
        {
            DistanceM = distanceM;
            HasWall   = hasWall;
            HasRoom   = hasRoom;
        }

        /// <summary>Расстояние от границы помещения наружу, м.</summary>
        public double DistanceM { get; }

        /// <summary>Точку накрывает тело стены.</summary>
        public bool HasWall { get; }

        /// <summary>В точке найдено помещение (не то же самое).</summary>
        public bool HasRoom { get; }
    }

    /// <summary>Разбор марша: улица или шахта, ширина пустоты и чем это решено.</summary>
    public class VoidVerdict
    {
        public BeyondWall Kind      { get; set; }
        public double     WidthM    { get; set; }
        public string     Note      { get; set; }
    }

    /// <summary>
    /// «Пусто за стеной» ≠ «улица».
    ///
    /// <para><b>Что чинит.</b> Наружность сегмента границы решалась пробой на 60 см:
    /// нет помещения — значит улица. Но <c>GetRoomAtPoint</c> одинаково молчит и над
    /// тротуаром, и внутри вентшахты, а шахта проходит СКВОЗЬ отапливаемый объём.
    /// Замер на 76-СУЗДАЛ.23 (прогон 2026-08-12): 25 помещений — санузлы 4–5 м² —
    /// получали 127 м² «наружных» стен и 5,9% Q огр всего здания, по 104 Вт/м²
    /// против типовых 39. Тот же класс дефекта, что был у колонн: там проба
    /// не выходила из тела элемента, здесь — не выходит из здания.</para>
    ///
    /// <para><b>Чем шахта отличается от улицы.</b> Она ЗАМКНУТА: марш наружу
    /// пересекает конструкцию, попадает в пустоту ограниченной ширины, а за
    /// пустотой снова находит конструкцию и за ней — помещение. Над улицей марш
    /// упирается в пустоту, которая не кончается.</para>
    ///
    /// <para><b>Почему правило живёт отдельным файлом без Revit.</b> Пробой на 3 м
    /// эту задачу уже пытались решить 2026-08-06 и откатили: одна точка вместо
    /// профиля сняла часть завышения у МОП-коридоров и одновременно потеряла
    /// настоящие наружные стены кухонь. Разница между тем вариантом и этим —
    /// не в дальности пробы, а в том, ЧТО считается доказательством; такое обязано
    /// проверяться автотестами на профилях, а не только на модели.</para>
    /// </summary>
    public static class EnclosedVoid
    {
        /// <summary>Первый шаг марша от границы помещения, м.</summary>
        public const double FirstStepM = 0.10;

        /// <summary>Шаг марша, м. Мельче толщины любой стены шахты (кирпич 120 мм).</summary>
        public const double StepM = 0.10;

        /// <summary>
        /// Дальше этого марш не идёт, м. Предел = максимальная ширина пустоты
        /// плюс запас на конструкции с обеих сторон.
        /// </summary>
        public const double LimitM = 2.60;

        /// <summary>
        /// Уже этого пустота шахтой не считается, м.
        ///
        /// <para>Внизу диапазона стоят два чужих явления: деформационный шов между
        /// секциями (50–200 мм) и вентилируемая прослойка фасада — «Фасад Зазор225»
        /// на этой же модели. Обе сообщаются с наружным воздухом, и принимать их
        /// за шахту нельзя. Порог взят выше обеих, с учётом того, что марш меряет
        /// ширину шагами по 100 мм и округляет её вверх.</para>
        /// </summary>
        public const double MinWidthM = 0.35;

        /// <summary>
        /// Шире этого пустота шахтой не считается, м. Выше — уже не шахта,
        /// а световой карман или разрыв между секциями, то есть наружный воздух.
        /// Отброшенные по этому порогу пишутся в журнал: если на модели их окажется
        /// много, порог придётся пересматривать по замеру, а не по памяти.
        /// </summary>
        public const double MaxWidthM = 1.60;

        /// <summary>
        /// Разбор профиля марша. Улица — исход по умолчанию: она же и была
        /// поведением до этой правки, поэтому любая неуверенность оставляет
        /// расчёт прежним, а не меняет числа молча.
        /// </summary>
        public static VoidVerdict Classify(IList<VoidProbeSample> samples)
        {
            if (samples == null || samples.Count == 0)
                return Outdoor(0, "проба не дала ни одной точки");

            int i = 0;

            // 1. Сплошная зона конструкций сразу за границей: отделка, несущая
            //    стена, слои фасада либо стенка самой шахты.
            while (i < samples.Count && samples[i].HasWall && !samples[i].HasRoom) i++;

            if (i >= samples.Count)
                return Outdoor(0, "конструкции до конца марша, пустоты нет");

            if (samples[i].HasRoom)
                return Outdoor(0, Fmt("помещение вплотную за конструкцией, {0} м", samples[i].DistanceM));

            // 2. Пустота.
            int start = i;
            while (i < samples.Count && !samples[i].HasWall && !samples[i].HasRoom) i++;

            double width = samples[i - 1].DistanceM - samples[start].DistanceM + StepM;

            if (i >= samples.Count)
                return Outdoor(width, Fmt("пустота не кончилась за {0} м — улица", LimitM));

            if (samples[i].HasRoom)
                return Outdoor(width, "за пустотой сразу помещение, ограждающей конструкции между ними нет");

            // 3. За пустотой конструкция. Шахта доказана только помещением за ней:
            //    иначе с той стороны может быть что угодно, включая соседний дом.
            while (i < samples.Count && !samples[i].HasRoom) i++;

            if (i >= samples.Count)
                return Outdoor(width, Fmt("за пустотой {0} м конструкция, но помещения за ней нет", width));

            if (width < MinWidthM)
                return Outdoor(width, Fmt("пустота {0} м — шов или прослойка фасада, а не шахта", width));

            if (width > MaxWidthM)
                return Outdoor(width, Fmt("пустота {0} м для шахты велика — принята за наружный воздух", width));

            return new VoidVerdict
            {
                Kind   = BeyondWall.Shaft,
                WidthM = width,
                Note   = Fmt("замкнутая пустота {0} м", width) +
                         Fmt(", за ней помещение на {0} м — шахта внутри здания", samples[i].DistanceM)
            };
        }

        private static VoidVerdict Outdoor(double width, string note)
        {
            return new VoidVerdict { Kind = BeyondWall.OutdoorAir, WidthM = width, Note = note };
        }

        private static string Fmt(string format, double value)
        {
            return string.Format(CultureInfo.CurrentCulture, format, value.ToString("F2", CultureInfo.CurrentCulture));
        }
    }
}
