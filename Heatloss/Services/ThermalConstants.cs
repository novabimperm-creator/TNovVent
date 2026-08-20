using QOVETER.Models;
using System;
using System.Collections.Generic;

namespace QOVETER.Services
{
    /// <summary>
    /// Нормативные константы теплотехники, собранные в одном месте.
    /// Каждая величина снабжена ссылкой на пункт СП/ГОСТ. При обновлении нормативной
    /// базы правки делаются здесь — все расчётные сервисы потребляют значения отсюда.
    /// </summary>
    public static class ThermalConstants
    {
        // ─── Воздух (для расчёта тепловой мощности на нагрев приточного воздуха) ───

        /// <summary>Плотность сухого воздуха при +20 °C, кг/м³ (СП 50.13330 п. 5.2).</summary>
        public const double AirDensity = 1.2;

        /// <summary>Удельная теплоёмкость сухого воздуха при постоянном давлении, кДж/(кг·К).</summary>
        public const double AirSpecificHeat = 1.006;

        /// <summary>
        /// Коэффициент перевода из формулы Q [Вт] = K · L [м³/ч] · ρc [кДж/(м³·К)] · ΔT.
        /// K = 1/3.6 (кДж/ч → Вт). Используется как константа 0.28 в формулах СП 60.13330.
        /// </summary>
        public const double AirFlowToWatts = 0.2778;   // 1/3.6, более точно чем 0.28

        // ─── Поверхностные сопротивления теплопередаче (СП 50.13330 Таблица 4) ───

        /// <summary>Внутреннее поверхностное сопротивление стены/пола/потолка, м²·К/Вт. αsi = 8.7 → Rsi ≈ 0.115.</summary>
        public const double Rsi = 0.13;

        /// <summary>Наружное поверхностное сопротивление стены, м²·К/Вт. αse = 23 → Rse ≈ 0.043.</summary>
        public const double Rse = 0.04;

        /// <summary>Сумма Rsi + Rse для расчёта полного сопротивления стены.</summary>
        public const double RsiPlusRse = Rsi + Rse;   // 0.17

        // ─── Коэффициенты β по ориентации (СП 60.13330 п. 6.4.2) ───
        // Прим.: значения зависят от редакции СП и региональных норм. Текущие — из ТЗ
        // проекта; при разногласиях с заказчиком сверять по актуальной редакции.

        public static readonly IReadOnlyDictionary<string, double> OrientationBeta =
            new Dictionary<string, double>
            {
                { "Север",         0.10 },
                { "Северо-Восток", 0.10 },
                { "Восток",        0.10 },
                { "Северо-Запад",  0.10 },
                { "Юго-Восток",    0.05 },
                { "Запад",         0.05 },
                { "Юг",            0.00 },
                { "Юго-Запад",     0.00 }
            };

        // ─── Прочие надбавки (β) к трансмиссионным потерям ───

        /// <summary>
        /// Надбавка для углового помещения (≥2 наружные стены), ТЗ.
        ///
        /// Рядом стояла вторая константа <c>BetaMultipleExternalWalls</c> = 0.05
        /// «для помещения с >1 наружной стеной». Условие её применения было
        /// тождественно условию этой (<c>IsCorner</c> выводится из числа наружных
        /// стен), поэтому каждое нежилое угловое помещение получало 0.10 вместо 0.05.
        /// Константа удалена намеренно: пока критерий один, значение тоже должно
        /// быть одно, иначе развилка воспроизведётся.
        /// </summary>
        public const double BetaCornerRoom = 0.05;

        /// <summary>Надбавка для помещения высотой более 4 м.</summary>
        public const double BetaHighRoom = 0.02;

        /// <summary>Граничная высота, выше которой применяется BetaHighRoom.</summary>
        public const double HighRoomThresholdM = 4.0;

        // ─── Коэффициенты для наружных дверей (β = k · H, СП 60.13330) ───

        public static readonly IReadOnlyDictionary<string, double> DoorBetaCoefficients =
            new Dictionary<string, double>
            {
                { "Тройные двери с двумя тамбурами", 0.20 },
                { "Двойные двери с тамбуром",        0.27 },
                { "Двойные двери без тамбура",       0.34 },
                { "Одинарные двери",                 0.22 }
            };

        // ─── Сопротивления типовых ограждений (fallback, если не читается из модели) ───

        /// <summary>U пола на грунте / над неотапл. подвалом, Вт/(м²·К). СП 50.13330: R≈4.3 → U≈0.23.</summary>
        public const double FloorUDefault = 0.23;

        /// <summary>U пола в санузле/ванной (тёплый пол).</summary>
        public const double FloorUBathroom = 0.30;

        /// <summary>U пола балкона/лоджии.</summary>
        public const double FloorUBalcony = 0.50;

        /// <summary>U совмещённой плоской кровли, Вт/(м²·К). СП 50.13330: R≈5.0 → U≈0.20.</summary>
        public const double RoofUDefault = 0.20;

        /// <summary>U чердачного перекрытия.</summary>
        public const double RoofUAttic = 0.22;

        /// <summary>U скатной крыши мансарды (меньший слой утеплителя).</summary>
        public const double RoofUMansard = 0.25;

        /// <summary>
        /// Нормативное U пола, когда реальную конструкцию из модели прочитать не удалось.
        /// Живёт здесь, а не в двух местах: до 2026-08-06 одна копия правил была в
        /// <c>CalculationEngine</c>, вторая — в <c>FloorRoofThermalCalculator</c>,
        /// и они успели разойтись (во второй сравнение типа помещения шло с заглавной
        /// буквы по уже приведённой к нижнему регистру строке — ветка была мертва).
        /// </summary>
        public static double FloorUFallback(RoomCategory category)
        {
            switch (category)
            {
                case RoomCategory.Bathroom: return FloorUBathroom;
                case RoomCategory.Balcony:  return FloorUBalcony;
                default:                    return FloorUDefault;
            }
        }

        /// <summary>
        /// Нормативное U покрытия, когда реальную конструкцию прочитать не удалось.
        /// Мансарда и чердак распознаются по имени или типу помещения.
        /// </summary>
        public static double RoofUFallback(string roomName, string roomType)
        {
            string name = (roomName ?? string.Empty).ToLowerInvariant();
            string type = (roomType ?? string.Empty).ToLowerInvariant();

            if (name.Contains("мансард") || type.Contains("мансард")) return RoofUMansard;
            if (name.Contains("чердак")  || type.Contains("чердак"))  return RoofUAttic;
            return RoofUDefault;
        }

        /// <summary>U наружной стены — типовое для кирпичной кладки 510 мм (fallback).</summary>
        public const double WallUDefault = 0.51;

        /// <summary>
        /// U витражного (навесного) остекления, Вт/(м²·К) — fallback, когда у типа
        /// нет ни слоёв, ни аналитического сопротивления.
        ///
        /// Стоечно-ригельная система с терморазрывом и стеклопакетом даёт
        /// R₀ ≈ 0,55 м²·К/Вт (СП 50.13330 Табл. 3 для заполнений светопроёмов;
        /// у витража доля непрозрачных стоек и ригелей больше, чем у окна,
        /// поэтому берётся хуже типового окна 1,33).
        ///
        /// ⚠ Значение НЕ выписано из норматива построчно — как и Ψ, это оценка
        /// порядка величины. Но подставлять сюда `WallUDefault` = 0,51, то есть
        /// считать стекло кирпичом в полметра, — заведомо неверно втрое.
        /// </summary>
        public const double CurtainWallUDefault = 1.8;

        /// <summary>
        /// Теплопроводность железобетона, Вт/(м·К) — СП 50.13330 Приложение Т
        /// (тяжёлый бетон на цементном вяжущем, условия эксплуатации Б).
        /// Применяется к колоннам, если материал элемента в модели не заполнен.
        /// </summary>
        public const double ReinforcedConcreteConductivity = 2.04;

        /// <summary>
        /// Толщина колонны в направлении теплового потока по умолчанию, м —
        /// когда габарит из модели за пределами разумного диапазона.
        /// Взято «в запас»: тонкая колонна холоднее толстой.
        /// </summary>
        public const double ColumnThicknessDefaultM = 0.25;

        // ─── Внутренние тепловыделения (СП 50.13330 / методика расчёта) ───

        /// <summary>Бытовые тепловыделения при плотности заселения ≤ 20 м²/чел, Вт/м².</summary>
        public const double InternalHeatHighDensity = 17.0;

        /// <summary>Бытовые тепловыделения при плотности заселения ≥ 45 м²/чел, Вт/м².</summary>
        public const double InternalHeatLowDensity = 10.0;

        // ─── Воздухообмен (ТЗ, раздел «Qвент»; СП 54.13330.2022 Таблица 9.1) ───
        //
        // По ТЗ норма считается НА КВАРТИРУ: L = max(Σ приток по жилым комнатам,
        // Σ вытяжка по кухне и санузлам). Квартирной группировки пока нет (этап 3),
        // поэтому норма применяется покомнатно — это приближение, а не сама методика.
        // Отсюда же расхождение с инженерной таблицей по маленьким кухням.

        /// <summary>Норма приточного воздуха для жилых комнат, м³/(ч·м² пола).</summary>
        public const double LivingRoomAirFlow = 3.0;

        // Норма притока «30 м³/ч на человека» здесь была, но не применялась нигде:
        // по ТЗ квартирная норма — L = max(Σ приток по площади, Σ вытяжка), человека
        // в ней нет. Константа удалена, чтобы её не приняли за действующее правило.

        /// <summary>Вытяжка кухни с электроплитой, м³/ч.</summary>
        public const double KitchenElectricExhaust = 60.0;

        /// <summary>Вытяжка кухни с газовой плитой, м³/ч (в ТЗ не оговорено, СП 54.13330).</summary>
        public const double KitchenGasExhaust = 100.0;

        /// <summary>Вытяжка раздельного санузла, туалета, постирочной, м³/ч (ТЗ).</summary>
        public const double ToiletExhaust = 25.0;

        /// <summary>Вытяжка ванной и совмещённого санузла, м³/ч (ТЗ).</summary>
        public const double CombinedBathroomExhaust = 50.0;

        /// <summary>Кратность воздухообмена для нежилых помещений без нормы вытяжки, 1/ч.</summary>
        public const double UtilityAirChangeRate = 0.5;

        /// <summary>
        /// Фиксированная вытяжная норма по категории помещения, м³/ч.
        /// Категории, которых здесь нет, считаются по притоку (жилые) или
        /// по кратности объёма (подсобные) — см. <see cref="SupplyRatedCategories"/>.
        /// Кухня в таблице отсутствует намеренно: её норма зависит от типа плиты.
        /// </summary>
        public static readonly IReadOnlyDictionary<RoomCategory, double> ExhaustRateByCategory =
            new Dictionary<RoomCategory, double>
            {
                { RoomCategory.Bathroom, CombinedBathroomExhaust },  // ванная, совмещённый санузел
                { RoomCategory.Toilet,   ToiletExhaust },            // раздельный санузел, туалет
                { RoomCategory.Laundry,  ToiletExhaust }             // постирочная, прачечная
            };

        /// <summary>
        /// Категории, для которых расход считается по НОРМЕ ПРИТОКА 3 м³/(ч·м²)
        /// жилой площади (ТЗ). Для остальных нежилых берётся кратность объёма.
        /// </summary>
        public static readonly IReadOnlyCollection<RoomCategory> SupplyRatedCategories =
            new HashSet<RoomCategory>
            {
                RoomCategory.LivingRoom,
                RoomCategory.Bedroom,
                RoomCategory.ChildRoom,
                RoomCategory.DiningRoom,
                RoomCategory.Office,
                RoomCategory.Other
            };

        /// <summary>
        /// Категории, которые по умолчанию НЕ попадают в расчёт квартирного отопления.
        ///
        /// Балконы и лоджии — неотапливаемые (расчётная температура 5 °C), а лестницы,
        /// лифтовые холлы, тамбуры, внеквартирные коридоры, котельные и подвалы
        /// нормативно идут отдельным расчётом на общедомовую систему.
        ///
        /// Снимаются автоматически, но не безвозвратно: галочку можно вернуть,
        /// а на плане такие помещения видно штриховкой.
        /// </summary>
        public static readonly IReadOnlyCollection<RoomCategory> UnheatedOrCommonCategories =
            new HashSet<RoomCategory>
            {
                RoomCategory.Balcony,
                RoomCategory.Stairs,
                RoomCategory.Lobby,
                RoomCategory.Vestibule,
                RoomCategory.Boiler,
                RoomCategory.Basement,
                // Венткамеры, насосные, электрощитовые, узлы связи — инженерные
                // помещения с отдельным режимом; как котельная, в квартирный
                // расчёт не входят. Галочку можно вернуть вручную.
                RoomCategory.Technical,
                // Шахта не отапливается ни по какой системе. Здесь она нужна
                // не столько для снятия галочки (помещением шахту моделируют
                // редко), сколько для правила «отапливаемый сосед делает стену
                // внутренней»: без этой строки стена в шахту-помещение теряла бы
                // потери ЦЕЛИКОМ — ошибка в другую сторону от той, что чинится.
                RoomCategory.Shaft
            };

        /// <summary>
        /// Объёмы, расчётная температура которых считается ТЕПЛОВЫМ БАЛАНСОМ
        /// (СП 50.13330.2012 п. 5.2: «теплый чердак, техническое подполье,
        /// остекленная лоджия или балкон»).
        ///
        /// <para><b>Почему это НЕ тот же набор, что <see cref="UnheatedOrCommonCategories"/>.</b>
        /// Лестничная клетка, лифтовой холл и тамбур в том наборе стоят не потому,
        /// что они холодные, а потому что нормативно идут ОТДЕЛЬНЫМ расчётом
        /// на общедомовую систему. Отопление у них есть, и расчётная температура
        /// задана нормой — 16 °C. Посчитать их балансом значило бы объявить
        /// лестницу с окнами почти уличной и раздуть ΔT у всех примыкающих
        /// квартирных стен: ошибка того же рода, что чинится, только в другую
        /// сторону. Котельная, техпомещение и шахта сюда не входят по другой
        /// причине — режим их отопления задаёт проект, а не геометрия.</para>
        /// </summary>
        public static readonly IReadOnlyCollection<RoomCategory> BalanceTemperatureCategories =
            new HashSet<RoomCategory>
            {
                RoomCategory.Balcony,   // лоджия, балкон, терраса — названы в п. 5.2
                RoomCategory.Basement   // подвал и техподполье — названы там же
            };

        // ─── Категории «жилых» помещений ───

        /// <summary>
        /// «Жилые помещения» в смысле ТЗ: к ним НЕ применяется надбавка β = 0.05
        /// за угловое расположение и за две и более наружные стены.
        /// </summary>
        public static readonly IReadOnlyCollection<RoomCategory> ResidentialCategories =
            new HashSet<RoomCategory>
            {
                RoomCategory.LivingRoom,
                RoomCategory.Bedroom,
                RoomCategory.ChildRoom,
                RoomCategory.DiningRoom,
                RoomCategory.Office
            };

        /// <summary>
        /// «Жилые комнаты» в смысле ГОСТ 30494-96 Таблица 1: только они получают
        /// повышенную расчётную температуру в районах с t_н ≤ −31 °C.
        /// Уже, чем <see cref="ResidentialCategories"/>: столовая и кабинет сюда не входят.
        /// </summary>
        public static readonly IReadOnlyCollection<RoomCategory> LivingRoomCategories =
            new HashSet<RoomCategory>
            {
                RoomCategory.LivingRoom,
                RoomCategory.Bedroom,
                RoomCategory.ChildRoom
            };

        // ─── Инфильтрация через окна (СП 50.13330 п. 9.2) ───

        /// <summary>Сопротивление воздухопроницанию типового окна, м²·ч·Па^(2/3) / кг.
        /// СП 50.13330 Таблица 11: для стеклопакетов в ПВХ-переплёте Ru ≈ 0.45.</summary>
        public const double WindowAirResistance = 0.45;

        /// <summary>Расчётная разность давлений на наветренной стороне, Па (упрощённо для ΔP=10 Па).</summary>
        public const double InfiltrationPressureDiff = 10.0;

        // ─── Климатические особенности (ГОСТ 30494) ───

        /// <summary>Порог наружной температуры, ниже которого жилые помещения получают +1 °C по ГОСТ 30494.</summary>
        public const double ColdRegionThreshold = -31.0;

        /// <summary>
        /// Полоса вокруг порога, в которой результат СТУПЕНЧАТО зависит от точности
        /// t наруж, °C.
        ///
        /// Зачем: надбавка ГОСТ 30494 — не плавная поправка, а скачок расчётной
        /// температуры жилых комнат с 20 на 21 °C. Рядом с порогом ошибка в один
        /// градус меняет и ΔT, и tв разом: например, −29 → −31 даёт не 4%, а около
        /// 6% по помещению. Во встроенной таблице городов в эту полосу попадают
        /// 12 значений из 43 (Пермь стоит ровно на пороге), и ни одно из них
        /// не сверено с текстом норматива — см. <see cref="CityService"/>.
        /// </summary>
        public const double ColdRegionThresholdMargin = 2.0;

        /// <summary>
        /// Наружная температура настолько близка к порогу ГОСТ 30494, что точность
        /// её определения меняет результат ступенькой, а не пропорционально.
        /// </summary>
        public static bool IsNearColdRegionThreshold(double externalTemperature)
        {
            return Math.Abs(externalTemperature - ColdRegionThreshold) <= ColdRegionThresholdMargin;
        }
    }
}
