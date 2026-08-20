using QOVETER.Models;
using System;
using System.Linq;
using System.Windows;

namespace QOVETER.UI
{
    public partial class RoomDetailsWindow : Window
    {
        private RoomData _room;
        private CalculationResult _result;

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
        public void SetData(RoomData room, CalculationResult result)
        {
            _room = room;
            _result = result;
            LoadRoomDetails();
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
                VentLossText.Text = $"{_result.Q_vent:F1} Вт";
                // Слагаемые выше — без запаса, поэтому промежуточный итог показывается
                // отдельно от Q_final. Раньше строка «Итого» показывала Q_final,
                // и сумма видимых слагаемых с ней не сходилась.
                SubtotalLossText.Text = $"{_result.Q_total:F1} Вт";
                TotalLossText.Text = $"{_result.Q_final:F1} Вт";
                
                // Ограждающие конструкции
                if (_room.Walls != null)
                    WallsList.ItemsSource = _room.Walls;
                if (_room.Windows != null)
                    WindowsList.ItemsSource = _room.Windows;
                if (_room.Doors != null)
                    DoorsList.ItemsSource = _room.Doors;
                
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

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }
    }
}