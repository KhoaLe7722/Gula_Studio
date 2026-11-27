using System.Windows.Controls;
using CharacterConceptAI.Desktop.ViewModels;

namespace CharacterConceptAI.Desktop.Features.Views;

public partial class SetupManagerView : UserControl
{
    public SetupManagerView()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            if (DataContext is MainViewModel vm)
            {
                vm.EnsureSetupCatalogLoaded();
                if (vm.SetupGpuInfo == "Đang kiểm tra GPU..." && vm.RefreshSetupStatusCommand.CanExecute(null))
                    vm.RefreshSetupStatusCommand.Execute(null);
            }
        };
    }
}
