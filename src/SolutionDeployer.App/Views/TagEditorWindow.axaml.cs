using Avalonia.Controls;

namespace SolutionDeployer.App.Views;

public partial class TagEditorWindow : Window
{
    public TagEditorWindow()
    {
        InitializeComponent();
        Opened += (_, _) => this.FindControl<AutoCompleteBox>("NewTagBox")?.Focus();
    }
}
