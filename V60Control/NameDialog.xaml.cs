using System.Windows;

namespace V60Control;

public partial class NameDialog : Window
{
    public string EnteredName => NameBox.Text.Trim();

    public NameDialog(string title, string prompt, string initial = "")
    {
        InitializeComponent();
        Title = title;
        PromptText.Text = prompt;
        NameBox.Text = initial;
        Loaded += (_, _) => { NameBox.Focus(); NameBox.SelectAll(); };
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (EnteredName.Length == 0) return;
        DialogResult = true;
    }
}
