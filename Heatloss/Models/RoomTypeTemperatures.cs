using System.Collections.Generic;
using System.Linq;

namespace QOVETER.Models
{
    /// <summary>
    /// Таблица расчётных температур по КАТЕГОРИЯМ помещений.
    /// Для категории задана температура — используется она, иначе применяется
    /// глобальная температура из UI.
    ///
    /// Ключ — <see cref="RoomCategory"/>, а не подстрока имени. Так было не всегда,
    /// и разбор строк здесь пережил чистку <c>CalculationEngine</c> на два этапа.
    /// Чем он платил:
    ///   • перебор словаря шёл ПО ПОРЯДКУ вставки, и «Лифтовой холл» попадал
    ///     на ключ «холл» (18 °C) раньше, чем на «лифтов» (16 °C);
    ///   • «Нежилое помещение» подходило под ключ «жил» и получало 20 °C как жилая
    ///     комната — даже после того, как классификатор перестал считать его жилым;
    ///   • категории <see cref="RoomCategory.Technical"/> в таблице не было вовсе,
    ///     хотя её и завели затем, чтобы венткамера и насосная не считались по 20 °C:
    ///     вернув такому помещению галочку, инженер снова получал 20 °C;
    ///   • в таблице UI показывались 20 ключей из 27, и остальные семь можно было
    ///     потерять нажатием «OK» (починено отдельно, см. <see cref="ApplyRows"/>).
    /// Категория — единый источник правды для нормативных коэффициентов; температура
    /// теперь берётся оттуда же, и все перечисленные развилки исчезают структурно.
    /// </summary>
    public class RoomTypeTemperatures
    {
        /// <summary>
        /// Расчётная температура внутреннего воздуха по категории, °C.
        /// ГОСТ 30494-2011 Таблица 1 — нижняя граница ОПТИМАЛЬНОЙ температуры
        /// холодного периода для обычных районов. Надбавку для районов с t_н ≤ −31 °C
        /// (жилая комната 21…23 вместо 20…22) движок добавляет отдельно, в
        /// <c>CalculationEngine.GetRoomDesignTemperature</c>. Здесь её быть не должно:
        /// до 2026-08-04 в таблице стояло 21, и надбавка ложилась поверх — жилые
        /// комнаты в холодных районах считались по 22 °C вместо 21 °C.
        /// </summary>
        public Dictionary<RoomCategory, double> Temperatures { get; set; } =
            new Dictionary<RoomCategory, double>
            {
                { RoomCategory.LivingRoom, 20.0 },
                { RoomCategory.Bedroom,    20.0 },
                { RoomCategory.ChildRoom,  20.0 },
                { RoomCategory.DiningRoom, 20.0 },
                { RoomCategory.Office,     20.0 },
                { RoomCategory.Kitchen,    19.0 },
                { RoomCategory.Bathroom,   24.0 },  // ванная, совмещённый санузел
                { RoomCategory.Toilet,     19.0 },  // раздельный санузел, туалет
                { RoomCategory.Laundry,    19.0 },
                { RoomCategory.Corridor,   18.0 },  // коридор, прихожая, холл
                { RoomCategory.Wardrobe,   18.0 },
                { RoomCategory.Storage,    16.0 },
                { RoomCategory.Stairs,     16.0 },
                { RoomCategory.Vestibule,  16.0 },  // тамбур, тамбур-шлюз, вестибюль
                { RoomCategory.Lobby,      16.0 },  // лифтовой холл
                // Неотапливаемые по СП 50.13330: лоджия и балкон — «холодные»,
                // расчётная температура +5 °C, как наружный буфер.
                { RoomCategory.Balcony,     5.0 },
                { RoomCategory.Boiler,      5.0 },
                { RoomCategory.Basement,    5.0 },
                // Венткамера, насосная, электрощитовая, узел связи: отапливаются
                // по отдельному заданию либо не отапливаются. Строки не было —
                // и помещение считалось по общей tв из UI, то есть по 20 °C.
                { RoomCategory.Technical,   5.0 },
                // ── Шахта: вентиляционная, лифтовая, техническая ──────────────
                // Прямой нормативной температуры для неё нет: СП 50.13330 считает
                // неотапливаемый объём по тепловому балансу либо вводит коэффициент
                // n, а n здесь намеренно не применяется (решение 2026-08-10 —
                // температура за ограждением берётся из этой же таблицы).
                //
                // Принято 16 °C — как у лестничной клетки и тамбура, то есть
                // у соседних неотапливаемых объёмов ВНУТРИ отапливаемого контура.
                // Это оценка В ЗАПАС: вытяжная шахта несёт удаляемый из квартир
                // воздух и в действительности ближе к tв, чем к 16 °C. Инженер
                // меняет значение в диалоге температур.
                //
                // Чего эта строка стоит: до 2026-08-13 за такой стеной принимался
                // НАРУЖНЫЙ воздух. На 76-СУЗДАЛ.23 это 25 санузлов, 127 м² стен
                // и 5,9% Q огр здания при ΔT примерно вшестеро больше реальной.
                { RoomCategory.Shaft,      16.0 }
                // RoomCategory.Other в таблице НЕТ намеренно: нераспознанное помещение
                // считается по глобальной температуре из UI, и инженер видит его
                // в списке «тип не определён».
            };

        /// <summary>
        /// Расчётная температура для категории, °C. null — в таблице нет,
        /// применяется глобальная температура из UI.
        /// </summary>
        public double? GetTemperature(RoomCategory category)
        {
            double value;
            return Temperatures.TryGetValue(category, out value) ? value : (double?)null;
        }

        /// <summary>
        /// Накладывает отредактированные строки таблицы на набор температур.
        ///
        /// Отдельный метод, а не сборка нового словаря в окне: раньше в таблице
        /// показывались не все ключи, и построение результата «только из видимых
        /// строк» стирало остальные — после одного нажатия «OK» котельная считалась
        /// не по 5 °C, а по общей температуре из UI. Сейчас показываются ВСЕ
        /// категории таблицы (это проверяет тест), но наложение оставлено:
        /// добавить категорию и забыть строку в UI — ошибка одного шага.
        /// </summary>
        public void ApplyRows(IEnumerable<RoomTypeRow> rows)
        {
            if (rows == null) return;
            foreach (var row in rows)
            {
                if (row == null) continue;
                Temperatures[row.Category] = row.Temperature;
            }
        }

        /// <summary>Копия набора — чтобы правки в диалоге не трогали оригинал до «OK».</summary>
        public RoomTypeTemperatures Clone()
        {
            var clone = new RoomTypeTemperatures();
            clone.Temperatures = new Dictionary<RoomCategory, double>();
            foreach (var kv in Temperatures)
                clone.Temperatures[kv.Key] = kv.Value;
            return clone;
        }

        /// <summary>
        /// Строки для таблицы UI — ВСЕ категории набора, отсортированные по подписи.
        /// Раньше список был захардкожен и покрывал 20 ключей из 27; теперь он
        /// строится из самого набора, и «забытых» строк быть не может.
        /// </summary>
        public static List<RoomTypeRow> GetDisplayRows(RoomTypeTemperatures src)
        {
            return src.Temperatures
                .Select(kv => new RoomTypeRow
                {
                    Category    = kv.Key,
                    Label       = RoomCategoryHelper.GetRussianLabel(kv.Key),
                    Temperature = kv.Value
                })
                .OrderBy(r => r.Label, System.StringComparer.CurrentCulture)
                .ToList();
        }
    }

    public class RoomTypeRow
    {
        public string       Label       { get; set; }
        public RoomCategory Category    { get; set; }
        public double       Temperature { get; set; }
    }
}
