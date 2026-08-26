using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace QOVETER.Services
{
    /// <summary>
    /// Счётчики времени по именованным участкам расчёта.
    ///
    /// <para><b>Зачем.</b> 2026-08-26 сетевики запустили плагин на большом проекте:
    /// сбор помещений, занимающий у нас 20-25 минут, шёл около трёх часов. Разбор
    /// кода даёт список подозреваемых, но не даёт их ВЕСА, а оптимизировать
    /// по догадке — это прогон Revit на каждую гипотезу. Здесь измеряется само
    /// здание: сколько раз и сколько времени.</para>
    ///
    /// <para><b>Почему не профилировщик.</b> Плагин работает внутри Revit на машине
    /// инженера в другом городе; попросить туда профилировщик нельзя, а журнал
    /// они и так присылают. Сводка печатается в конец журнала таблицей.</para>
    ///
    /// <para><b>Цена самих счётчиков.</b> <see cref="Stopwatch.GetTimestamp"/> —
    /// один вызов QueryPerformanceCounter, десятки наносекунд. На фоне запроса
    /// к геометрии Revit это ничто, поэтому счётчики включены всегда: выключенный
    /// в релизе замер не помог бы там, где он и нужен, — у заказчика.</para>
    ///
    /// <para>Однопоточность намеренная: Revit API работает только в своём потоке,
    /// и весь расчёт идёт в нём же.</para>
    /// </summary>
    public static class Perf
    {
        private sealed class Counter
        {
            public long Calls;
            public long Ticks;
        }

        private static readonly Dictionary<string, Counter> _counters =
            new Dictionary<string, Counter>();

        /// <summary>Метка времени для последующего <see cref="Add"/>.</summary>
        public static long Now => Stopwatch.GetTimestamp();

        /// <summary>Записать участок, начавшийся в момент <paramref name="startedAt"/>.</summary>
        public static void Add(string name, long startedAt)
        {
            long delta = Stopwatch.GetTimestamp() - startedAt;

            Counter counter;
            if (!_counters.TryGetValue(name, out counter))
            {
                counter = new Counter();
                _counters[name] = counter;
            }

            counter.Calls++;
            counter.Ticks += delta;
        }

        /// <summary>Сбросить счётчики — перед новым этапом.</summary>
        public static void Reset()
        {
            _counters.Clear();
        }

        /// <summary>
        /// Напечатать сводку в журнал и сбросить счётчики.
        /// Строки идут по убыванию времени: первая строка и есть узкое место.
        /// </summary>
        public static void Report(string stage)
        {
            if (_counters.Count == 0) return;

            double toMs = 1000.0 / Stopwatch.Frequency;

            var rows = _counters
                .Select(p => new
                {
                    Name = p.Key,
                    p.Value.Calls,
                    Ms = p.Value.Ticks * toMs
                })
                .OrderByDescending(r => r.Ms)
                .ToList();

            double totalMs = rows.Sum(r => r.Ms);

            Logger.Info($"[Время] {stage}: суммарно по замеренным участкам {totalMs / 1000:F1} с. " +
                        "Участки вложены друг в друга — складывать их между собой нельзя, " +
                        "сравнивать между собой можно.");

            foreach (var row in rows)
            {
                double perCallMs = row.Calls > 0 ? row.Ms / row.Calls : 0;
                Logger.Info($"[Время] {row.Name,-28} {row.Ms / 1000,8:F1} с   " +
                            $"вызовов {row.Calls,8}   по {perCallMs,7:F2} мс");
            }

            Reset();
        }
    }
}
