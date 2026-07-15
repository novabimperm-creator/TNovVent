using Microsoft.Win32;
using System.IO;
using System.Windows;
using System.Windows.Input;
using TNovCommon;

namespace TNovVent
{
    /// <summary>
    /// Логика взаимодействия для DTCWPF.xaml
    /// </summary>
    public partial class DTCWPF : Window
    {
        public DTCWPF(DTCViewModel viewModel)
        {
            InitializeComponent(); 
            DataContext = viewModel;
            SizeToContent = SizeToContent.Height;
        }
        private void acceptButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
            this.Close(); // закрытие окна
        }

        private void escButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            this.Close(); // закрытие окна
        }

        private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
                DragMove();
        }
        private void BrowseButton_Click(object sender, RoutedEventArgs e)
        {
            OpenFileDialog openFileDialog = new OpenFileDialog();
            openFileDialog.Filter = "Файлы Excel (*.xlsx)|*.xlsx";
            openFileDialog.Title = "Выберите файл Excel";

            

            bool? result = openFileDialog.ShowDialog();

            if (result == true && DataContext is DTCViewModel viewModel)
            {
                viewModel.filePath = openFileDialog.FileName;
            }
        }
        private void HelpButton_Click(object sender, RoutedEventArgs e)
        {
            string commandText = HelpLinks.GetHelpLink("ADSK Стенки");
            var proc = new System.Diagnostics.Process();
            proc.StartInfo.FileName = commandText;
            proc.StartInfo.UseShellExecute = true;
            proc.Start();
        }
    }
}
