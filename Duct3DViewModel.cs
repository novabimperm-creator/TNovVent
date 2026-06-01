using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;

namespace TNovVent
{
    public class Duct3DViewModel : INotifyPropertyChanged
    {
        [JsonIgnore] public ObservableCollection<string> paramlist { get; set; }
        private string _output1; public string output1 { get { return _output1; } set { _output1 = value; OnPropertyChanged(); } }
        public Duct3DViewModel()
        {
            Param();
        }
        private void Param()
        {
            paramlist = new ObservableCollection<string>
            {
                "Имя системы",
                "ADSK_Группирование"
            };
            output1 = paramlist[0];
        }

        public event EventHandler CloseRequest;
        private void RaiseCloseRequest()
        {
            CloseRequest?.Invoke(this, EventArgs.Empty);
        }
        public event PropertyChangedEventHandler PropertyChanged;

        void OnPropertyChanged([CallerMemberName] string PropertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(PropertyName));
        }
    }
}
