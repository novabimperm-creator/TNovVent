using QOVETER.Models;
using System.Collections.Generic;
using System.Windows;

namespace QOVETER.UI
{
    public partial class RoomTemperaturesWindow : Window
    {
        private List<RoomTypeRow> _rows;

        /// <summary>
        /// Полный набор температур, поверх которого ложатся правки из таблицы.
        /// Хранится отдельно исторически: пока таблица показывала 20 ключей из 27,
        /// сборка результата «только из видимых строк» стирала остальные семь.
        /// Сейчас <see cref="RoomTypeTemperatures.GetDisplayRows"/> строится из самого
        /// набора и показывает все категории, но наложение оставлено — оно дешевле,
        /// чем повторить ту же ошибку при добавлении новой категории.
        /// </summary>
        private RoomTypeTemperatures _all;

        public RoomTypeTemperatures Result { get; private set; }

        public RoomTemperaturesWindow(RoomTypeTemperatures current)
        {
            InitializeComponent();

            // Клонируем текущие значения — оригинал не меняем до нажатия «OK».
            _all = current.Clone();
            _rows = RoomTypeTemperatures.GetDisplayRows(_all);
            TempGrid.ItemsSource = _rows;
        }

        private void OkBtn_Click(object sender, RoutedEventArgs e)
        {
            // Принудительно фиксируем редактирование ячейки
            TempGrid.CommitEdit(System.Windows.Controls.DataGridEditingUnit.Row, true);

            // Правки НАКЛАДЫВАЮТСЯ на полный набор, а не заменяют его: см.
            // RoomTypeTemperatures.ApplyRows — там же объяснено, что ломалось раньше.
            _all.ApplyRows(_rows);
            Result = _all;

            DialogResult = true;
        }

        private void CancelBtn_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }

        private void ResetButton_Click(object sender, RoutedEventArgs e)
        {
            // Сброс возвращает нормативные значения ЦЕЛИКОМ, включая скрытые ключи.
            _all = new RoomTypeTemperatures();
            _rows = RoomTypeTemperatures.GetDisplayRows(_all);
            TempGrid.ItemsSource = _rows;
        }
    }
}
