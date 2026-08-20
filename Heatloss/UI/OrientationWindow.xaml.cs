using QOVETER.Models;
using QOVETER.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace QOVETER.UI
{
    public partial class OrientationWindow : Window
    {
        private BuildingParameters _buildingParams;
        private List<LevelInfo> _levels = new List<LevelInfo>();

        public OrientationWindow()
        {
            InitializeComponent();
            _buildingParams = new BuildingParameters();
        }

        public void SetBuildingParameters(BuildingParameters parameters)
        {
            _buildingParams = parameters;
            LoadParameters();
        }

        public void SetLevels(List<LevelInfo> levels)
        {
            _levels = levels;
            LoadLevels();
        }

        public BuildingParameters GetBuildingParameters()
        {
            return _buildingParams;
        }

        private void LoadParameters()
        {
            try
            {
                // Ориентация
                if (!string.IsNullOrEmpty(_buildingParams.SelectedCity))
                {
                    // Можно добавить логику для определения ориентации по городу
                }

                // Этажность выставляется в LoadLevels: этот метод вызывается раньше,
                // когда список уровней ещё пуст.

                // Высота здания и высота этажа — независимые величины
                BuildingHeightBox.Text = _buildingParams.TotalHeight.ToString("F1");
                FloorHeightBox.Text = _buildingParams.FloorHeight.ToString("F1");

                // Тип дверей
                DoorTypeCombo.SelectedItem = DoorTypeCombo.Items
                    .Cast<ComboBoxItem>()
                    .FirstOrDefault(item => item.Content.ToString() == _buildingParams.SelectedDoorType);

                // Ручная ориентация
                ManualOrientationCheck.IsChecked = _buildingParams.IsManualOrientation;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Ошибка загрузки параметров: {ex.Message}", "Ошибка", 
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// Заполняет списки уровней и выставляет в них ТЕКУЩИЕ границы этажности.
        ///
        /// Порядок вызовов из <c>MainWindow</c> — <see cref="SetBuildingParameters"/>,
        /// затем <see cref="SetLevels"/>, поэтому подставлять выбранные уровни надо
        /// здесь: в <see cref="LoadParameters"/> список уровней ещё пуст, и его попытка
        /// выбрать этаж не срабатывала никогда.
        ///
        /// Верхний этаж по умолчанию — ПОСЛЕДНИЙ уровень. Раньше стояло
        /// <c>Math.Min(_levels.Count - 1, 1)</c>, то есть при трёх и более уровнях
        /// всегда второй снизу: инженер, нажавший «Применить» не глядя, фиксировал
        /// не тот верхний этаж и терял кровельные потери на настоящем верхнем.
        /// </summary>
        private void LoadLevels()
        {
            try
            {
                GroundFloorCombo.ItemsSource = _levels;
                TopFloorCombo.ItemsSource = _levels;

                if (_levels.Count == 0) return;

                GroundFloorCombo.SelectedItem =
                    _levels.FirstOrDefault(l => l.FloorNumber == _buildingParams.GroundFloorNumber)
                    ?? _levels.First();

                TopFloorCombo.SelectedItem =
                    _levels.FirstOrDefault(l => l.FloorNumber == _buildingParams.TopFloorNumber)
                    ?? _levels.Last();
            }
            catch (Exception ex)
            {
                Logger.Error("Ошибка загрузки уровней", ex);
            }
        }

        private void ApplyButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // Сохраняем ориентацию
                if (OrientationCombo.SelectedItem is ComboBoxItem orientationItem)
                {
                    string orientation = orientationItem.Content.ToString();
                    // Окно задаёт ориентацию на всё здание; поуровневые переопределения
                    // кладутся в тот же словарь по LevelId (см. BuildingParameters).
                    _buildingParams.LevelOrientations[BuildingParameters.CommonOrientationKey] = orientation;
                }

                // Сохраняем этажность. Как только инженер нажал «Применить», границы
                // считаются заданными вручную и автоопределение по помещениям
                // (BuildingParameters.AutoDetectFloorRange) их больше не перебивает.
                if (GroundFloorCombo.SelectedItem is LevelInfo groundLevel)
                {
                    _buildingParams.GroundFloorNumber = groundLevel.FloorNumber;
                    _buildingParams.IsFloorRangeManual = true;
                }

                if (TopFloorCombo.SelectedItem is LevelInfo topLevel)
                {
                    _buildingParams.TopFloorNumber = topLevel.FloorNumber;
                    _buildingParams.IsFloorRangeManual = true;
                }

                // Сохраняем общую высоту здания и высоту этажа в РАЗНЫЕ поля
                if (double.TryParse(BuildingHeightBox.Text, out double totalHeight))
                {
                    _buildingParams.TotalHeight = totalHeight;
                }

                if (double.TryParse(FloorHeightBox.Text, out double floorHeight))
                {
                    _buildingParams.FloorHeight = floorHeight;
                }

                // Сохраняем тип дверей
                if (DoorTypeCombo.SelectedItem is ComboBoxItem doorTypeItem)
                {
                    _buildingParams.SelectedDoorType = doorTypeItem.Content.ToString();
                }

                // Сохраняем флаг ручной ориентации
                _buildingParams.IsManualOrientation = ManualOrientationCheck.IsChecked ?? false;

                DialogResult = true;
                Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Ошибка сохранения параметров: {ex.Message}", "Ошибка", 
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void ManualOrientationCheck_Checked(object sender, RoutedEventArgs e)
        {
            OrientationCombo.IsEnabled = true;
        }

        private void ManualOrientationCheck_Unchecked(object sender, RoutedEventArgs e)
        {
            OrientationCombo.IsEnabled = false;
        }
    }
}