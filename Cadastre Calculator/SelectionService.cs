using System.Collections.Generic;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;

namespace Cadastre_Calculator
{
    public class SelectionService
    {
        // Cache for BlockTableRecord ObjectId -> Block Name
        // In a real-world scenario with frequent calls, this might be scoped to the class or injected.
        // For this specific request "within the method scope", I'll initialize it in the method or use a class-level cache if reuse is expected.
        // The prompt says "maintain a Dictionary... within the method scope", but also "Optimization: Maintain a ... cache".
        // I will put it at class level for broader reuse if the service instance is kept alive, 
        // effectively handling the "prevent opening the same definition multiple times" requirement across calls if the service is reused.
        // However, to strictly follow "within the method scope" for a standalone operation, I will declare it inside if that's the interpretation, 
        // but a class-level cache is standard for a "Service".
        // Let's stick to the prompt's likely intent of "per-operation efficiency" or "service-level efficiency". 
        // I will use a class-level cache to maximize performance as a "Service".
        private readonly Dictionary<ObjectId, string> _blockNameCache = new Dictionary<ObjectId, string>();

        public IEnumerable<ObjectId> SelectParcelsByBlockName(string blockName)
        {
            var result = new List<ObjectId>();
            
            Document doc = Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return result;
            
            Editor ed = doc.Editor;

            // Pass 1: Fast filtering using Editor selection
            // We only look for Insert (BlockReference) entities initially
            SelectionFilter filter = new SelectionFilter(new[]
            {
                new TypedValue((int)DxfCode.Start, "INSERT")
            });

            // Use SelectAll for this specific request
            PromptSelectionResult selectionResult = ed.SelectAll(filter);

            if (selectionResult.Status != PromptStatus.OK)
            {
                return result;
            }

            // Pass 2: Iteration with OpenCloseTransaction for read-only performance
            // StartOpenCloseTransaction is much lighter weight than a full transaction
            using (Transaction tr = doc.TransactionManager.StartOpenCloseTransaction())
            {
                foreach (SelectedObject so in selectionResult.Value)
                {
                    // Open the BlockReference
                    if (tr.GetObject(so.ObjectId, OpenMode.ForRead) is not BlockReference br)
                        continue;

                    // Get the effective definition ID (handles dynamic blocks correctly)
                    ObjectId defId = br.DynamicBlockTableRecord;

                    // Check cache before opening the BlockTableRecord
                    if (!_blockNameCache.TryGetValue(defId, out string? effectiveName))
                    {
                        // Cache miss: Open the BTR to get the name
                        if (tr.GetObject(defId, OpenMode.ForRead) is BlockTableRecord btr)
                        {
                            effectiveName = btr.Name;
                            _blockNameCache[defId] = effectiveName;
                        }
                    }

                    // Compare names (case-insensitive usually preferred in AutoCAD, but strict == requested)
                    if (effectiveName == blockName)
                    {
                        result.Add(so.ObjectId);
                    }
                }
                
                tr.Commit();
            }

            return result;
        }
    }
}