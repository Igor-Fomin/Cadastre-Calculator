using System;
using System.Collections.Generic;

namespace Cadastre_Calculator.Abstractions
{
    public interface ITransactionWrapper : IDisposable
    {
        IEntityWrapper GetObject(object id, bool forWrite = false);
        void Commit();
        IXrecordWrapper CreateXrecord();
        IDictionaryWrapper CreateDictionary();
    }

    public interface IEntityWrapper
    {
        object Id { get; }
        object ExtensionDictionary { get; }
        void CreateExtensionDictionary();
    }

    public interface IDictionaryWrapper : IEntityWrapper
    {
        bool Contains(string key);
        object GetAt(string key);
        object SetAt(string key, IEntityWrapper entry);
    }

    public interface IXrecordWrapper : IEntityWrapper
    {
        void SetData(IEnumerable<string> chunks);
        IEnumerable<string> GetData();
    }

    public interface IPolyline : IEntityWrapper
    {
        double Area { get; }
    }
}