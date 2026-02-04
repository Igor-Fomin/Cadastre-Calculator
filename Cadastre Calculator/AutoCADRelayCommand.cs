using System;
using Autodesk.AutoCAD.ApplicationServices;

namespace Cadastre_Calculator
{
    public class AutoCADRelayCommand : RelayCommand
    {
        private readonly IAutoCADContext _context;

        public AutoCADRelayCommand(IAutoCADContext context, Action<object?> execute, Predicate<object?>? canExecute = null)
            : base(execute, canExecute)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
        }

        public override void Execute(object? parameter)
        {
            Document doc = _context.ActiveDocument;
            if (doc == null) return;

            // Critical: Ensure document is locked for modeless operations
            using (doc.LockDocument())
            {
                base.Execute(parameter);
            }
        }
    }
}
