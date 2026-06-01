using System.Windows;

namespace TNovVent
{
    /// <summary>
    /// Логика взаимодействия для Duct3DWPF.xaml
    /// </summary>
    public partial class Duct3DWPF : Window
    {
        public Duct3DWPF(Duct3DViewModel viewModel)
        {
            InitializeComponent();
            this.SizeToContent = SizeToContent.Height;
            DataContext = viewModel;
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

        private void Border_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {

        }

        private void HelpButton_Click(object sender, RoutedEventArgs e)
        {
            string commandText = @"https://portal.talan.group/knowledge/proektirovanie/MEPviews/";
            var proc = new System.Diagnostics.Process();
            proc.StartInfo.FileName = commandText;
            proc.StartInfo.UseShellExecute = true;
            proc.Start();
        }
    }
}
