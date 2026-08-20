using QOVETER.Models;
using System.Collections.Generic;
using System.Windows.Media;

namespace QOVETER.UI
{
    /// <summary>
    /// Палитра плана этажа. Ключ — <see cref="RoomCategory"/>, а НЕ строка типа:
    /// подписи категорий меняются вместе с нормативными уточнениями, и привязка
    /// к строке молча роняла бы помещения в «серый по умолчанию».
    ///
    /// Оттенки подобраны в одной светлоте (около 90%) и низкой насыщенности,
    /// чтобы: план читался как чертёж, а не как диаграмма; чёрная подпись поверх
    /// любого помещения оставалась контрастной; выделение и наведение были
    /// заметны на фоне заливки.
    ///
    /// Цвет несёт смысл: жилые — холодный синий, кухня и столовая — зелёный,
    /// мокрые помещения — бирюзовый, вспомогательные и общедомовые — нейтральный
    /// тёплый серый.
    /// </summary>
    internal static class RoomPalette
    {
        private static readonly Dictionary<RoomCategory, SolidColorBrush> Fills =
            new Dictionary<RoomCategory, SolidColorBrush>
            {
                { RoomCategory.LivingRoom, Freeze("#DCE8F5") },
                { RoomCategory.Bedroom,    Freeze("#D3E0F2") },
                { RoomCategory.ChildRoom,  Freeze("#F1DEEA") },
                { RoomCategory.DiningRoom, Freeze("#E2EEDA") },
                { RoomCategory.Kitchen,    Freeze("#D9EBD1") },
                { RoomCategory.Bathroom,   Freeze("#CDE4E9") },
                { RoomCategory.Toilet,     Freeze("#DCEBEE") },
                { RoomCategory.Laundry,    Freeze("#E3EBEF") },
                { RoomCategory.Corridor,   Freeze("#EBE8E3") },
                { RoomCategory.Wardrobe,   Freeze("#EEE8DC") },
                { RoomCategory.Storage,    Freeze("#ECE7DC") },
                { RoomCategory.Balcony,    Freeze("#DFEFEF") },
                { RoomCategory.Stairs,     Freeze("#E6E4EE") },
                { RoomCategory.Vestibule,  Freeze("#ECE8E1") },
                { RoomCategory.Lobby,      Freeze("#E7E5EF") },
                { RoomCategory.Boiler,     Freeze("#F1E2D9") },
                { RoomCategory.Basement,   Freeze("#E4E4E4") },
                { RoomCategory.Other,      Freeze("#EBEBEB") }
            };

        /// <summary>Контур помещения: тёмный сине-серый, спокойнее чистого чёрного.</summary>
        public static readonly SolidColorBrush Outline = Freeze("#5A6672");

        /// <summary>Контур под курсором.</summary>
        public static readonly SolidColorBrush Hover = Freeze("#2C3E50");

        /// <summary>Контур выбранного помещения.</summary>
        public static readonly SolidColorBrush Selected = Freeze("#E67E22");

        /// <summary>Заливка помещения, исключённого из расчёта.</summary>
        public static readonly SolidColorBrush Excluded = Freeze("#F2F2F2");

        /// <summary>Контур исключённого помещения.</summary>
        public static readonly SolidColorBrush ExcludedOutline = Freeze("#B6BCC4");

        public static SolidColorBrush Fill(RoomCategory category)
        {
            SolidColorBrush brush;
            return Fills.TryGetValue(category, out brush) ? brush : Fills[RoomCategory.Other];
        }

        private static SolidColorBrush Freeze(string hex)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            brush.Freeze();   // неизменяемая кисть переиспользуется всеми полигонами
            return brush;
        }
    }
}
