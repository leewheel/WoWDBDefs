using System.Windows;
using System.Windows.Input;
using WowDBEditor.Core;

namespace WowDBEditor.App.Dialogs;

public partial class BuildPickDialog : Window
{
    public string SelectedBuild { get; private set; }

    public BuildPickDialog(IEnumerable<DefinitionService.VersionChoice> choices, string prompt = null)
    {
        InitializeComponent();
        if (prompt != null)
            PromptText.Text = prompt;
        foreach (var choice in choices)
            ChoiceList.Items.Add(choice);
        if (ChoiceList.Items.Count > 0)
            ChoiceList.SelectedIndex = 0;

        // 选中列表项时把版本号带进输入框，用户可以在此基础上改成自己的精确版本（如 3.3.5.12340）
        ChoiceList.SelectionChanged += (_, _) =>
        {
            if (ChoiceList.SelectedItem is DefinitionService.VersionChoice choice && !BuildBox.IsKeyboardFocusWithin)
                BuildBox.Text = choice.Build;
        };
        if (ChoiceList.SelectedItem is DefinitionService.VersionChoice first)
            BuildBox.Text = first.Build;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        var manual = BuildBox.Text?.Trim();
        if (!string.IsNullOrEmpty(manual))
        {
            SelectedBuild = manual;
            DialogResult = true;
            return;
        }

        if (ChoiceList.SelectedItem is DefinitionService.VersionChoice choice)
        {
            SelectedBuild = choice.Build;
            DialogResult = true;
            return;
        }

        MessageBox.Show(this, "请选择列表中的版本，或直接输入客户端版本号。", "提示",
            MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void ChoiceList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ChoiceList.SelectedItem != null)
            Ok_Click(sender, e);
    }
}
