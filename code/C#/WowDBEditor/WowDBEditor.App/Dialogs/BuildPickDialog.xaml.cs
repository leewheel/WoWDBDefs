using System.Windows;
using System.Windows.Input;

namespace WowDBEditor.App.Dialogs;

public partial class BuildPickDialog : Window
{
    public sealed record Choice(string Label, string Build);

    public string SelectedBuild { get; private set; }

    public BuildPickDialog(IEnumerable<Choice> choices, string prompt = null)
    {
        InitializeComponent();
        if (prompt != null)
            PromptText.Text = prompt;
        foreach (var choice in choices)
            ChoiceList.Items.Add(choice);
        if (ChoiceList.Items.Count > 0)
            ChoiceList.SelectedIndex = 0;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (ChoiceList.SelectedItem is Choice choice)
        {
            SelectedBuild = choice.Build;
            DialogResult = true;
        }
        else
        {
            MessageBox.Show(this, "请先选择一个版本。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void ChoiceList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ChoiceList.SelectedItem != null)
            Ok_Click(sender, e);
    }
}