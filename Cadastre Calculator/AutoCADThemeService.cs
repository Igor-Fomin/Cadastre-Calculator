using System;
using System.Windows;
using Autodesk.AutoCAD.ApplicationServices;

namespace Cadastre_Calculator
{
    public interface IThemeService
    {
        void Initialize(FrameworkElement element);
    }

    public class AutoCADThemeService : IThemeService
    {
        private FrameworkElement? _targetElement;

        public void Initialize(FrameworkElement element)
        {
            _targetElement = element;
            Autodesk.AutoCAD.ApplicationServices.Application.SystemVariableChanged += Application_SystemVariableChanged;
            ApplyTheme();
        }

        private void Application_SystemVariableChanged(object? sender, SystemVariableChangedEventArgs e)
        {
            if (e.Name.Equals("COLORTHEME", StringComparison.OrdinalIgnoreCase))
            {
                ApplyTheme();
            }
        }

        private void ApplyTheme()
        {
            if (_targetElement == null) return;

            short colorTheme = (short)Autodesk.AutoCAD.ApplicationServices.Application.GetSystemVariable("COLORTHEME");
            string themeFile = colorTheme == 0 ? "Dark.xaml" : "Light.xaml";
            
            var resourceDict = new ResourceDictionary
            {
                Source = new Uri($"/Cadastre Calculator;component/Themes/{themeFile}", UriKind.RelativeOrAbsolute)
            };

            _targetElement.Resources.MergedDictionaries.Clear();
            _targetElement.Resources.MergedDictionaries.Add(resourceDict);
        }
    }
}
