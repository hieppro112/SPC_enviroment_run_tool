using System.Windows;

namespace ToolManager.Views;

public partial class PortReserveWindow : Window
{
    public string Note { get; private set; } = "";

    public PortReserveWindow(int port)
    {
        InitializeComponent();
        HeaderTitle.Text = $"Đặt trước port {port}";
        Loaded += (_, _) => NoteBox.Focus();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        Note = NoteBox.Text.Trim();
        if (Note.Length == 0)
        {
            ErrorText.Text = "Hãy ghi port này dành cho app nào.";
            NoteBox.Focus();
            return;
        }
        DialogResult = true;
    }
}
