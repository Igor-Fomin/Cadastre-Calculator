using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.EditorInput;

namespace Cadastre_Calculator
{
    public interface IAutoCADContext
    {
        Document ActiveDocument { get; }
        Editor Editor { get; }
        DocumentCollection DocumentManager { get; }
    }

    public class AutoCADContext : IAutoCADContext
    {
        public Document ActiveDocument => Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument;
        public Editor Editor => ActiveDocument.Editor;
        public DocumentCollection DocumentManager => Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager;
    }
}
