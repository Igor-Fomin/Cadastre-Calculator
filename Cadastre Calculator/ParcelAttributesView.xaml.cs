using System.Windows.Controls;

namespace Cadastre_Calculator
{
    public partial class ParcelAttributesView : System.Windows.Controls.UserControl
    {
        private readonly IThemeService _themeService;

        public ParcelAttributesView()
        {
            InitializeComponent();
            
            // In a real app, this would be injected.
            _themeService = new AutoCADThemeService();
            _themeService.Initialize(this);
        }
    }
}