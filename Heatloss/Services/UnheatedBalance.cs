using System;
using System.Collections.Generic;
using System.Linq;
using QOVETER.Models;

namespace QOVETER.Services
{
    /// <summary>Одна поверхность неотапливаемого объёма: её UA и температура ЗА ней.</summary>
    public class BalanceSurface
    {
        public BalanceSurface(double ua, double temperature, string source = null)
        {
            UA          = ua;
            Temperature = temperature;
            Source      = source ?? "";
        }

        /// <summary>U·A, Вт/К.</summary>
        public double UA { get; }

        /// <summary>Температура воздуха по ту сторону поверхности, °C.</summary>
        public double Temperature { get; }

        /// <summary>Чем эта поверхность является — для журнала.</summary>
        public string Source { get; }
    }

    /// <summary>Результат теплового баланса неотапливаемого объёма.</summary>
    public class BalanceResult
    {
        public bool   Computed    { get; set; }
        public double Temperature { get; set; }
        public double UAWarm      { get; set; }
        public double UACold      { get; set; }
        public string Note        { get; set; }
    }

    /// <summary>
    /// Расчётная температура НЕотапливаемого объёма — по тепловому балансу,
    /// а не по подставленному числу.
    ///
    /// <para><b>Норматив.</b> СП 50.13330.2012, п. 5.2, дословно: «Расчетную
    /// температуру воздуха в теплом чердаке, техническом подполье, остекленной
    /// лоджии или балконе при проектировании допускается принимать на основе
    /// расчета теплового баланса». Таблицы значений для лоджии норматив не даёт
    /// вовсе — поэтому любое фиксированное число здесь и было выдуманным.</para>
    ///
    /// <para><b>Что чинится.</b> До 2026-08-13 лоджия считалась по +5 °C из таблицы
    /// температур. При −29 °C на улице это эквивалент n = 14/48 = 0,29, то есть
    /// лоджия объявлялась вчетверо теплее улицы. Мера на 76-СУЗДАЛ.23: 1 252 м²
    /// ограждений выходят на лоджии, лестницы и тамбуры; у кухонь это 36% всех
    /// ограждений, у отдельных — 100%, и их оконно-балконные блоки 4,5 м² давали
    /// 88–92 Вт вместо примерно 390. Отсюда −24,2% по кухням против расчёта
    /// проектировщика.</para>
    ///
    /// <para><b>Формула.</b> Установившийся баланс без внутренних источников:
    /// сколько теплоты втекает через тёплые поверхности, столько же вытекает
    /// через холодные, откуда
    /// <c>t = Σ(U·A·t) / Σ(U·A)</c> по ВСЕМ поверхностям объёма.</para>
    ///
    /// <para><b>Чего в балансе нет — и это в отчёте сказано.</b> Воздухообмен
    /// самого объёма (инфильтрация через неплотности остекления лоджии) не
    /// учитывается: кратность для неё в модели не задана, а придумывать её
    /// нельзя — это ровно та ошибка, которая чинится. Инфильтрация делает объём
    /// ХОЛОДНЕЕ, поэтому посчитанная температура — верхняя оценка, а теплопотери
    /// через такое ограждение — нижняя.</para>
    /// </summary>
    public static class UnheatedBalance
    {
        /// <summary>
        /// Ниже этой суммарной проводимости баланс не строится, Вт/К. У объёма
        /// с одной случайной поверхностью «температура» получилась бы из ничего.
        /// </summary>
        public const double MinTotalUA = 0.5;

        public static BalanceResult Compute(IEnumerable<BalanceSurface> surfaces)
        {
            var all = (surfaces ?? Enumerable.Empty<BalanceSurface>())
                .Where(s => s != null && s.UA > 0 && !double.IsNaN(s.UA) && !double.IsInfinity(s.UA))
                .ToList();

            double totalUA = all.Sum(s => s.UA);
            if (all.Count == 0 || totalUA < MinTotalUA)
            {
                return new BalanceResult
                {
                    Computed = false,
                    Note = $"поверхностей {all.Count}, Σ U·A = {totalUA:F2} Вт/К — " +
                           "для баланса недостаточно данных модели"
                };
            }

            double t = all.Sum(s => s.UA * s.Temperature) / totalUA;

            // Средневзвешенное по построению лежит между крайними температурами;
            // проверка стоит сторожем на случай отрицательных U из битой модели.
            double min = all.Min(s => s.Temperature);
            double max = all.Max(s => s.Temperature);
            if (t < min || t > max)
            {
                return new BalanceResult
                {
                    Computed = false,
                    Note = $"баланс дал {t:F1} °C вне диапазона {min:F1}…{max:F1} °C — отброшено"
                };
            }

            return new BalanceResult
            {
                Computed    = true,
                Temperature = t,
                UAWarm      = all.Where(s => s.Temperature > t).Sum(s => s.UA),
                UACold      = all.Where(s => s.Temperature <= t).Sum(s => s.UA),
                Note        = $"Σ U·A = {totalUA:F1} Вт/К по {all.Count} поверхностям"
            };
        }
    }

    /// <summary>Посчитанная температура одного неотапливаемого помещения.</summary>
    public class UnheatedRoomTemperature
    {
        public int          RoomId      { get; set; }
        public string       RoomName    { get; set; }
        public RoomCategory Category    { get; set; }
        public double       Temperature { get; set; }
        public bool         FromBalance { get; set; }
        public double       UAWarm      { get; set; }
        public double       UACold      { get; set; }
        public string       Note        { get; set; }
    }

    /// <summary>
    /// Собирает поверхности неотапливаемых помещений из собранной модели
    /// и считает их температуры балансом.
    ///
    /// <para><b>Откуда берутся стороны.</b> Холодная — из ограждений САМОГО
    /// неотапливаемого помещения (остекление лоджии, её парапет): лоджия в модели
    /// это Room, и её ограждения собираются наравне с прочими. Тёплая — из
    /// ограждений СОСЕДНИХ отапливаемых помещений, помеченных
    /// <see cref="IAdjacentSurface.AdjacentRoomId"/>: стена «кухня — лоджия»
    /// лежит у кухни, а у лоджии её нет (за ней отапливаемый сосед, и ограждением
    /// она для лоджии не считается). Поэтому связь по Id обязательна — без неё
    /// тёплую сторону баланса взять неоткуда.</para>
    ///
    /// <para><b>Пол и потолок лоджии в баланс не входят:</b> сверху и снизу
    /// такая же лоджия с той же температурой, и в балансе эти члены сокращаются.
    /// Для самой верхней и самой нижней это упрощение в пользу тепла — записано
    /// в примечании результата.</para>
    /// </summary>
    public static class UnheatedVolumes
    {
        public static Dictionary<int, UnheatedRoomTemperature> Resolve(
            IList<RoomData> allRooms,
            double outdoorTemperature,
            Func<RoomData, double> designTemperature,
            Func<RoomCategory, double?> tableTemperature)
        {
            var result = new Dictionary<int, UnheatedRoomTemperature>();
            if (allRooms == null || allRooms.Count == 0) return result;

            // Тёплые поверхности, сгруппированные по помещению, НА КОТОРОЕ они выходят.
            var warmByRoom = new Dictionary<int, List<BalanceSurface>>();
            foreach (var room in allRooms)
            {
                if (room == null) continue;
                double tWarm = designTemperature != null ? designTemperature(room) : 20.0;

                foreach (var s in Surfaces(room))
                {
                    if (s.Key <= 0 || s.Value <= 0) continue;
                    List<BalanceSurface> list;
                    if (!warmByRoom.TryGetValue(s.Key, out list))
                        warmByRoom[s.Key] = list = new List<BalanceSurface>();
                    list.Add(new BalanceSurface(s.Value, tWarm, room.DisplayName));
                }
            }

            foreach (var room in allRooms)
            {
                if (room == null) continue;

                // Только те объёмы, которые названы в СП 50.13330 п. 5.2. Лестничная
                // клетка, лифтовой холл и тамбур сюда НЕ входят: они отапливаются
                // (16 °C по норме), а в списке «неотапливаемых и общедомовых» стоят
                // потому, что идут отдельным расчётом. Балансом лестница с окнами
                // вышла бы почти уличной, и ΔT у всех примыкающих квартирных стен
                // выросла бы на ровном месте.
                if (!ThermalConstants.BalanceTemperatureCategories.Contains(room.Category)) continue;

                var surfaces = new List<BalanceSurface>();

                // Холодная сторона: собственные ограждения к наружному воздуху.
                foreach (var w in room.Walls ?? new List<WallInfo>())
                    if (w != null && w.IsExternal && !w.AdjacentCategory.HasValue && w.UValue > 0)
                        surfaces.Add(new BalanceSurface(w.UValue * w.Area, outdoorTemperature, "стена"));

                foreach (var win in room.Windows ?? new List<WindowInfo>())
                    if (win != null && !win.AdjacentCategory.HasValue && win.UValue > 0)
                        surfaces.Add(new BalanceSurface(win.UValue * win.Area, outdoorTemperature, "остекление"));

                foreach (var d in room.Doors ?? new List<DoorInfo>())
                    if (d != null && d.IsExternal && !d.AdjacentCategory.HasValue && d.UValue > 0)
                        surfaces.Add(new BalanceSurface(d.UValue * d.Area, outdoorTemperature, "дверь"));

                double coldUA = surfaces.Sum(s => s.UA);

                // Тёплая сторона: ограждения соседних отапливаемых помещений.
                List<BalanceSurface> warm;
                if (warmByRoom.TryGetValue(room.Id, out warm))
                    surfaces.AddRange(warm);

                double warmUA = surfaces.Sum(s => s.UA) - coldUA;

                // ── Баланс без холодной стороны — не баланс, а пробел в данных ──
                //
                // Улика с прогона 2026-08-13 16:39: часть лоджий получила ровно
                // +20 °C, то есть температуру соседней квартиры. Причина не в физике:
                // у этих помещений в модели не собралось НИ ОДНОГО собственного
                // наружного ограждения (остекление лоджии не смоделировано либо
                // не распозналось). Формула честно вернула средневзвешенное по одной
                // тёплой стороне, а по смыслу это означало «лоджия такая же тёплая,
                // как кухня» — и стена к ней теряла НОЛЬ. Такой результат хуже
                // прежних +5 °C из таблицы: он не осторожнее, а просто неверен.
                //
                // Поэтому баланс требует ОБЕИХ сторон. Нет холодной — данных нет,
                // возвращаемся к таблице и говорим об этом в журнале и отчёте.
                BalanceResult balance;
                if (coldUA < UnheatedBalance.MinTotalUA)
                {
                    balance = new BalanceResult
                    {
                        Computed = false,
                        Note = $"у объёма не собрано наружных ограждений (холодная сторона " +
                               $"{coldUA:F2} Вт/К) — в модели нет данных для баланса"
                    };
                }
                else if (warmUA < UnheatedBalance.MinTotalUA)
                {
                    balance = new BalanceResult
                    {
                        Computed = false,
                        Note = $"объём не примыкает ни к одному отапливаемому помещению " +
                               $"(тёплая сторона {warmUA:F2} Вт/К)"
                    };
                }
                else
                {
                    balance = UnheatedBalance.Compute(surfaces);
                }
                double? fromTable = tableTemperature != null ? tableTemperature(room.Category) : null;

                result[room.Id] = new UnheatedRoomTemperature
                {
                    RoomId      = room.Id,
                    RoomName    = room.DisplayName,
                    Category    = room.Category,
                    Temperature = balance.Computed
                        ? balance.Temperature
                        : (fromTable ?? outdoorTemperature),
                    FromBalance = balance.Computed,
                    UAWarm      = balance.UAWarm,
                    UACold      = balance.UACold,
                    Note        = balance.Computed
                        ? balance.Note
                        : balance.Note + "; принято значение из таблицы температур"
                };
            }

            return result;
        }

        /// <summary>Пары «Id соседнего помещения → U·A» по всем ограждениям помещения.</summary>
        private static IEnumerable<KeyValuePair<int, double>> Surfaces(RoomData room)
        {
            foreach (var w in room.Walls ?? new List<WallInfo>())
                if (w != null && w.UValue > 0)
                    yield return new KeyValuePair<int, double>(w.AdjacentRoomId, w.UValue * w.Area);

            foreach (var win in room.Windows ?? new List<WindowInfo>())
                if (win != null && win.UValue > 0)
                    yield return new KeyValuePair<int, double>(win.AdjacentRoomId, win.UValue * win.Area);

            foreach (var d in room.Doors ?? new List<DoorInfo>())
                if (d != null && d.UValue > 0)
                    yield return new KeyValuePair<int, double>(d.AdjacentRoomId, d.UValue * d.Area);
        }
    }
}
