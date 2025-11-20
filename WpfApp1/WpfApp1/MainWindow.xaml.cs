using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;

namespace WpfApp1
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : DpiAwareWindowBase
    {
        public MainWindow()
        {
            WindowId = "MainWindow";
            InitializeComponent();
        }

        // button click event handler to open Window1
        private void OpenButton_Click(object sender, RoutedEventArgs e)
        {
            Window1 window1 = new Window1();
            window1.InitializeComponent(); // Ensure Window1 is initialized

            window1.Show();
        }
    }
}