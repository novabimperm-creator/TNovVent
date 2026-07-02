using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;

namespace TNovVent
{
    public class DTCViewModel : INotifyPropertyChanged
    {

        private bool _all = false;
        public bool all
        {
            get => _all; set { _all = value; OnPropertyChanged(); }
        }
        private bool _visible = true;
        public bool visible
        {
            get => _visible; set { _visible = value; OnPropertyChanged(); }
        }
        private string _filePath;
        public string filePath { get { return _filePath; } set { _filePath = value; OnPropertyChanged(); } }



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
