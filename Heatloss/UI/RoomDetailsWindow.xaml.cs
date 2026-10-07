using QOVETER.Models;
using QOVETER.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;

namespace QOVETER.UI
{
    public partial class RoomDetailsWindow : Window
    {
        private RoomData _room;
        private CalculationResult _result;
        private List<UnheatedRoomTemperature> _unheatedTemperatures;

        // ИСПРАВЛЕНИЕ: Добавлен конструктор без параметров
        public RoomDetailsWindow()
        {
            InitializeComponent();
        }

        public RoomDetailsWindow(RoomData room, CalculationResult result)
        {
            InitializeComponent();
            _room = room;
            _result = result;

            LoadRoomDetails();
        }

        // ИСПРАВЛЕНИЕ: Добавлен метод SetData для установки данных после создания
        //
        // unheatedTemperatures — посчитанные балансом температуры лоджий и прочих
        // неотапливаемых объёмов ЭТОГО прогона. Без них строка «за стеной: Лоджия»
        // не отвечает на главный вопрос инженера — при какой ΔT посчитано ограждение.
        // Вопрос проектировщика 2026-10-07 «через окна 126,9 Вт — мало?» возник
        // ровно потому, что окно выходит в остеклённую лоджию, а в UI этого
        // не было видно нигде.
        public void SetData(RoomData room, CalculationResult result,
                            List<UnheatedRoomTemperature> unheatedTemperatures = null)
        {
            _room = room;
            _result = result;
            _unheatedTemperatures = unheatedTemperatures;
            LoadRoomDetails();
        }

        /// <summary>Строка списка ограждений — всё словами, без конвертеров в XAML.</summary>
        public class EnclosureRow
        {
            public string TypeName { get; set; }
            public string AreaText { get; set; }
            public string UText { get; set; }
            public string RText { get; set; }
            public string OrientationText { get; set; }
            public string BehindText { get; set; }
            public string SourceText { get; set; }
            public string ExtraText { get; set; }
        }

        private void LoadRoomDetails()
        {
            if (_room == null || _result == null)
            {
                MessageBox.Show("Данные не загружены", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            try
            {
                // Основная информация
                RoomNameText.Text = _room.DisplayName;
                RoomTypeText.Text = _room.Type;
                RoomAreaText.Text = $"{_room.Area:F2} м²";
                RoomOrientationText.Text = _room.Orientation;
                RoomLevelText.Text = $"{_room.LevelName} ({_room.Elevation:F2} м)";

                // Теплопотери
                WallLossText.Text = $"{_result.Q_walls:F1} Вт";
                WindowLossText.Text = $"{_result.Q_windows:F1} Вт";
                DoorLossText.Text = $"{_result.Q_doors:F1} Вт";
                FloorLossText.Text = $"{_result.Q_floor:F1} Вт";
                RoofLossText.Text = $"{_result.Q_roof:F1} Вт";
                // Q вент без расхода ничем себя не объясняет: при квартирной норме
                // помещение получает ДОЛЮ квартирного L пропорционально площади.
                VentLossText.Text = _result.VentAirFlow > 0
                    ? $"{_result.Q_vent:F1} Вт  (L = {_result.VentAirFlow:F0} м³/ч при ΔT = {_result.DeltaT:F0} °С)"
                    : $"{_result.Q_vent:F1} Вт";
                // Слагаемые выше — без запаса, поэтому промежуточный итог показывается
                // отдельно от Q_final. Раньше строка «Итого» показывала Q_final,
                // и сумма видимых слагаемых с ней не сходилась.
                SubtotalLossText.Text = $"{_result.Q_total:F1} Вт";
                TotalLossText.Text = $"{_result.Q_final:F1} Вт";

                // Ограждающие конструкции
                if (_room.Walls != null)
                    WallsList.ItemsSource = _room.Walls.Select(WallRow).ToList();
                if (_room.Windows != null)
                    WindowsList.ItemsSource = _room.Windows.Select(WindowRow).ToList();
                if (_room.Doors != null)
                    DoorsList.ItemsSource = _room.Doors.Select(DoorRow).ToList();

                // Коэффициенты
                if (_result.BetaCoefficients != null)
                {
                    CoefficientsList.ItemsSource = _result.BetaCoefficients;
                    double totalBeta = _result.BetaCoefficients.Values.Sum();
                    TotalCoefficientText.Text = $"Сумма коэффициентов β: {totalBeta:F3}";
                }
                else
                {
                    TotalCoefficientText.Text = "Коэффициенты не рассчитаны";
                }

                // Теплотехнические характеристики
                AverageRText.Text = $"{_room.AverageInverseUValue:F3} м²·°C/Вт";
                HeatLossCoefficientText.Text = $"{_room.HeatLossCoefficient:F3} Вт/(м²·°C)";
                SpecificLoadText.Text = $"{_result.SpecificHeatLoss:F1} Вт/м²";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Ошибка загрузки данных: {ex.Message}", "Ошибка",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private EnclosureRow WallRow(WallInfo wall)
        {
            double r = wall.RValue > 0 ? wall.RValue
                     : wall.UValue > 0 ? 1.0 / wall.UValue : 0;
            return new EnclosureRow
            {
                TypeName        = wall.TypeName,
                AreaText        = $"{wall.Area:F2} м²",
                UText           = $"{wall.UValue:F3} Вт/(м²·К)",
                RText           = r > 0 ? $"{r:F2} м²·°С/Вт" : "—",
                OrientationText = wall.Orientation ?? "—",
                BehindText      = BehindLabel(wall.AdjacentCategory, wall.AdjacentRoomId),
                SourceText      = string.IsNullOrWhiteSpace(wall.ThermalSource) ? "—" : wall.ThermalSource
            };
        }

        private EnclosureRow WindowRow(WindowInfo win)
        {
            return new EnclosureRow
            {
                TypeName        = win.TypeName,
                AreaText        = $"{win.Area:F2} м²",
                UText           = $"{win.UValue:F3} Вт/(м²·К)",
                RText           = win.UValue > 0 ? $"{1.0 / win.UValue:F2} м²·°С/Вт" : "—",
                OrientationText = win.Orientation ?? "—",
                BehindText      = BehindLabel(win.AdjacentCategory, win.AdjacentRoomId),
                SourceText      = null,
                ExtraText       = win.IsCurtainGlazing ? "витраж" : win.GlassType
            };
        }

        private EnclosureRow DoorRow(DoorInfo door)
        {
            return new EnclosureRow
            {
                TypeName        = door.TypeName,
                AreaText        = $"{door.Area:F2} м²",
                UText           = $"{door.UValue:F3} Вт/(м²·К)",
                RText           = door.UValue > 0 ? $"{1.0 / door.UValue:F2} м²·°С/Вт" : "—",
                OrientationText = door.IsExternal ? "наружная" : "внутренняя",
                BehindText      = door.IsExternal
                    ? BehindLabel(door.AdjacentCategory, door.AdjacentRoomId)
                    : "отапливаемое помещение",
                SourceText      = null,
                ExtraText       = door.Material
            };
        }

        /// <summary>
        /// Что за ограждением — словами. «Улица» или категория неотапливаемого
        /// соседа; если его температура в этом прогоне посчитана балансом
        /// (СП 50.13330 п. 5.2), она печатается рядом: именно она объясняет,
        /// почему два одинаковых ограждения теряют по-разному.
        /// </summary>
        private string BehindLabel(RoomCategory? adjacent, int adjacentRoomId)
        {
            if (!adjacent.HasValue) return "улица";

            string label = RoomCategoryHelper.GetRussianLabel(adjacent.Value);

            var computed = adjacentRoomId > 0
                ? _unheatedTemperatures?.FirstOrDefault(t => t.RoomId == adjacentRoomId)
                : null;
            if (computed != null)
            {
                return label + $" ({computed.Temperature:F1} °С" +
                       (computed.FromBalance ? " по балансу)" : " по таблице)");
            }

            return label;
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }
    }
}
