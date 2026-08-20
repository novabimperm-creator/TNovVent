namespace QOVETER.Models
{
    /// <summary>
    /// Категория помещения для нормативных расчётов
    /// (СП 50.13330, СП 54.13330, ГОСТ 30494).
    /// Используется вместо сравнения строкой <c>room.Type</c>, чтобы исключить
    /// рассинхронизацию ключей между местом определения типа (GeometryCollector)
    /// и местом применения коэффициента (CalculationEngine).
    /// </summary>
    public enum RoomCategory
    {
        Other = 0,
        Kitchen,
        Bathroom,
        Bedroom,
        LivingRoom,
        DiningRoom,
        ChildRoom,
        Office,
        Corridor,
        Wardrobe,
        Storage,
        Balcony,
        Stairs,         // Лестничная клетка
        Vestibule,      // Тамбур, тамбур-шлюз
        Lobby,          // Лифтовой холл, общедомовой холл
        Laundry,        // Постирочная, прачечная
        Boiler,         // Котельная, ИТП
        Basement,       // Подвал, тех. подполье
        /// <summary>
        /// Раздельный санузел, туалет. Отделён от <see cref="Bathroom"/> из-за разных
        /// норм вытяжки в ТЗ: санузлы и постирочные — 25 м³/ч, совмещённые санузлы
        /// и ванные — 50 м³/ч. Новые значения дописываются в конец: категория
        /// сериализуется в фикстуры и параметры Revit.
        /// </summary>
        Toilet,

        /// <summary>
        /// Техническое помещение инженерных систем: венткамера, насосная,
        /// электрощитовая, узел связи, серверная. Отапливается по отдельному
        /// заданию либо не отапливается — как котельная, снимается с расчёта
        /// автоматически. Добавлено 2026-08-06 после прогона на 76-СУЗДАЛ.23,
        /// где такие помещения попадали в расчёт с температурой 20 °C.
        /// </summary>
        Technical,

        /// <summary>
        /// Вентиляционная, лифтовая или техническая шахта — НЕотапливаемый объём
        /// внутри отапливаемого контура здания. Категория нужна прежде всего
        /// не самим помещениям (шахту помещением обычно не моделируют), а тому,
        /// что ЗА ограждением: до 2026-08-13 стена в шахту считалась наружной
        /// с полной ΔT, потому что <c>GetRoomAtPoint</c> за ней ничего не находил.
        /// Расчётная температура шахты задаётся в той же таблице, что и tв
        /// помещений. Добавлено В КОНЕЦ: enum сериализуется в фикстуры.
        /// </summary>
        Shaft
    }

    public static class RoomCategoryHelper
    {
        /// <summary>
        /// Определяет категорию по имени и типу помещения. Регистронезависимо.
        /// </summary>
        public static RoomCategory Detect(string name, string type = null)
        {
            string both = ((name ?? string.Empty) + " " + (type ?? string.Empty)).ToLowerInvariant();

            // Порядок важен: специфичные категории — раньше общих ("холл" перед "коридор",
            // "лифтовой холл" должен попасть в Lobby, а не в Corridor).
            //
            // Шахта проверяется ПЕРВОЙ: «Лифтовая шахта» содержит и «лифтов», и «шахт»,
            // а это шахта, а не лифтовой холл. Помещение в шахте моделируют редко,
            // но когда моделируют — оно не должно считаться отапливаемым, иначе
            // стена к нему станет внутренней и потери исчезнут вовсе.
            if (both.Contains("шахт")) return RoomCategory.Shaft;
            if (both.Contains("лифтов") || both.Contains("лифтхолл")) return RoomCategory.Lobby;
            if (both.Contains("тамбур") || both.Contains("вестибюль") ||
                both.Contains("шлюз")) return RoomCategory.Vestibule;
            if (both.Contains("лестни")) return RoomCategory.Stairs;
            if (both.Contains("постирочн") || both.Contains("прачечн")) return RoomCategory.Laundry;
            if (both.Contains("котельн") || both.Contains("итп") ||
                both.Contains("тепловой пункт")) return RoomCategory.Boiler;
            if (both.Contains("подвал") || both.Contains("подполь")) return RoomCategory.Basement;

            // «кухон», а не только «кухн»: в модели 76-СУЗДАЛ.23 пять помещений
            // называются «Кухонная зона», и подстроки «кухн» в этом слове НЕТ
            // (к-у-х-о-н-н-а-я). Они уходили в «не определено» и считались нежилыми.
            if (both.Contains("кухн") || both.Contains("кухон")) return RoomCategory.Kitchen;

            // Технические помещения инженерных систем: отапливаются отдельно либо
            // не отапливаются вовсе. Раньше не распознавались и считались по общей
            // температуре из UI — насосная и электрощитовая по 20 °C.
            if (both.Contains("венткамер") || both.Contains("насосн")   ||
                both.Contains("электрощит") || both.Contains("узел связи") ||
                both.Contains("серверн")   || both.Contains("техническое помещение") ||
                both.Contains("техпомещ"))
                return RoomCategory.Technical;
            // Ванная и СОВМЕЩЁННЫЙ санузел — 50 м³/ч; раздельный санузел и туалет — 25 м³/ч
            // (ТЗ, раздел «Qвент»). Порядок важен: «совмещённый санузел» должен попасть
            // в Bathroom раньше, чем сработает общая проверка на «санузел».
            if (both.Contains("ванн") || both.Contains("душев") ||
                (both.Contains("совмещ") && (both.Contains("санузел") || both.Contains("с/у"))))
                return RoomCategory.Bathroom;
            if (both.Contains("санузел") || both.Contains("туалет") ||
                both.Contains("с/у")) return RoomCategory.Toilet;
            if (both.Contains("спальн")) return RoomCategory.Bedroom;
            if (both.Contains("гостин") || IsResidentialHall(both)) return RoomCategory.LivingRoom;
            if (both.Contains("столов")) return RoomCategory.DiningRoom;
            if (both.Contains("детск")) return RoomCategory.ChildRoom;
            if (both.Contains("офис") || both.Contains("кабинет") ||
                both.Contains("рабоч")) return RoomCategory.Office;
            if (both.Contains("коридор") || both.Contains("прихож") ||
                both.Contains("холл")) return RoomCategory.Corridor;
            if (both.Contains("гардероб")) return RoomCategory.Wardrobe;
            if (both.Contains("кладов") || both.Contains("подсобн")) return RoomCategory.Storage;
            if (both.Contains("балкон") || both.Contains("лоджи") ||
                both.Contains("терраса")) return RoomCategory.Balcony;

            // «жил» — но НЕ «нежилое». Подстрока ловила «Нежилое помещение» и
            // «Помещение нежилого назначения» — типовое имя коммерции первого этажа
            // в российских проектах, — и отдавала их как жилую комнату: жилая норма
            // притока 3 м³/(ч·м²), надбавка ГОСТ 30494 к tв и освобождение
            // от штрафного β за угол. Тот же класс ошибки, что был с «Торг. зал».
            // Нежилое помещение НЕ угадывается (магазин, офис и склад — разные нормы),
            // а честно уходит в Other и попадает в список «тип не определён».
            if (both.Contains("жил") && !both.Contains("нежил")) return RoomCategory.LivingRoom;
            if (IsResidentialRoomWord(both)) return RoomCategory.LivingRoom;

            return RoomCategory.Other;
        }

        /// <summary>
        /// «Зал» в имени помещения — жилая комната только в квартире. В том же доме
        /// на первом этаже стоит «Торг. зал ≤ 400 м²», и раньше он попадал в
        /// <see cref="RoomCategory.LivingRoom"/> по голой подстроке «зал»: получал
        /// жилую норму притока 3 м³/(ч·м²), надбавку ГОСТ 30494 по температуре и
        /// терял штрафной β = 0.05 за угол, положенный нежилым.
        ///
        /// Ищем «зал» как отдельное слово (в .NET <c>\w</c> покрывает кириллицу,
        /// поэтому «залив» и «вокзал» не сработают) и отсекаем общественные залы.
        /// </summary>
        private static bool IsResidentialHall(string both)
        {
            if (!System.Text.RegularExpressions.Regex.IsMatch(both, @"(^|\W)зал(\W|$)"))
                return false;

            string[] publicHalls =
            {
                "торг", "спорт", "тренаж", "актов", "выставоч", "конференц",
                "обеден", "операцион", "читальн", "зритель", "танцевал", "игров"
            };
            foreach (var marker in publicHalls)
                if (both.Contains(marker)) return false;

            return true;
        }

        /// <summary>
        /// «Комната» без уточнений — жилая комната: так помещения называют в половине
        /// российских моделей. Служебные «комната уборочного инвентаря», «комната
        /// охраны», «комната персонала» жилыми не являются и уходят в
        /// <see cref="RoomCategory.Other"/>.
        /// </summary>
        private static bool IsResidentialRoomWord(string both)
        {
            if (!both.Contains("комнат")) return false;

            string[] serviceRooms =
            {
                "уборочн", "инвентар", "охран", "персонал", "мусор",
                "электрощит", "серверн", "техническ"
            };
            foreach (var marker in serviceRooms)
                if (both.Contains(marker)) return false;

            return true;
        }

        /// <summary>Русская метка для UI и записи в room.Type.</summary>
        public static string GetRussianLabel(RoomCategory category)
        {
            switch (category)
            {
                case RoomCategory.Kitchen:    return "Кухня";
                case RoomCategory.Bathroom:   return "Ванная/совм. санузел";
                case RoomCategory.Toilet:     return "Санузел/туалет";
                case RoomCategory.Bedroom:    return "Спальня";
                case RoomCategory.LivingRoom: return "Жилая комната";
                case RoomCategory.DiningRoom: return "Столовая";
                case RoomCategory.ChildRoom:  return "Детская";
                case RoomCategory.Office:     return "Офис";
                case RoomCategory.Corridor:   return "Коридор";
                case RoomCategory.Wardrobe:   return "Гардероб";
                case RoomCategory.Storage:    return "Кладовая";
                case RoomCategory.Balcony:    return "Балкон/Лоджия";
                case RoomCategory.Stairs:     return "Лестница";
                case RoomCategory.Vestibule:  return "Тамбур";
                case RoomCategory.Lobby:      return "Лифтовой холл";
                case RoomCategory.Laundry:    return "Постирочная";
                case RoomCategory.Boiler:     return "Котельная";
                case RoomCategory.Basement:   return "Подвал";
                case RoomCategory.Technical:  return "Техническое";
                case RoomCategory.Shaft:      return "Шахта (вент./лифтовая)";
                // RoomCategory.Other. Раньше здесь стояло «Жилая комната», и
                // нераспознанное помещение молча получало жилую подпись, а вместе
                // с ней (через GeometryCollector.DetermineRoomType) и жилой расчёт.
                // Теперь категория не подменяется, а честно называется неопознанной:
                // инженер видит её в отчёте и решает сам.
                default:                      return "Не определено";
            }
        }
    }
}
