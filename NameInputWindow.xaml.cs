using System.Windows;
using System.Windows.Input;

namespace TraderPen
{
    public partial class NameInputWindow : Window
    {
        public string EnteredName { get; private set; } = string.Empty;

        public NameInputWindow()
        {
            InitializeComponent();
            Loaded += (_, _) => NameTextBox.Focus();
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            TryAccept();
        }

        private void NameTextBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) TryAccept();
        }

        private void TryAccept()
        {
            var nome = NameTextBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(nome)) return;

            EnteredName = nome;
            DialogResult = true;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }
    }
}
