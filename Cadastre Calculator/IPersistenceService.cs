using Autodesk.AutoCAD.DatabaseServices;

namespace Cadastre_Calculator
{
    public interface IPersistenceService
    {
        void SaveData<T>(Transaction tr, ObjectId entityId, string key, T data);
        T? LoadData<T>(Transaction tr, ObjectId entityId, string key);
    }
}