using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using Cadastre_Calculator.Abstractions;

namespace Cadastre_Calculator.Implementations
{
    public class AutoCADTransaction : ITransactionWrapper
    {
        private readonly Transaction _transaction;

        public AutoCADTransaction(Transaction transaction)
        {
            _transaction = transaction ?? throw new ArgumentNullException(nameof(transaction));
        }

        public IEntityWrapper GetObject(object id, bool forWrite = false)
        {
            if (id is not ObjectId objectId) throw new ArgumentException("Invalid ID");
            
            var obj = _transaction.GetObject(objectId, forWrite ? OpenMode.ForWrite : OpenMode.ForRead);
            
            if (obj is DBDictionary dict) return new AutoCADDictionary(dict);
            if (obj is Xrecord xrec) return new AutoCADXrecord(xrec);
            if (obj is Polyline polyline) return new AutoCADPolyline(polyline);
            return new AutoCADEntity(obj);
        }

        public void Commit() => _transaction.Commit();
        public void Dispose() => _transaction.Dispose();

        public IXrecordWrapper CreateXrecord()
        {
            var xrec = new Xrecord();
            return new AutoCADXrecord(xrec);
        }

        public IDictionaryWrapper CreateDictionary()
        {
            var dict = new DBDictionary();
            return new AutoCADDictionary(dict);
        }
    }

    public class AutoCADEntity : IEntityWrapper
    {
        internal protected readonly DBObject _obj;
        public AutoCADEntity(DBObject obj) => _obj = obj;

        public object Id => _obj.ObjectId;
        public object ExtensionDictionary => (_obj is Entity ent) ? ent.ExtensionDictionary : ObjectId.Null;

        public void CreateExtensionDictionary()
        {
            if (_obj is Entity ent) ent.CreateExtensionDictionary();
        }
    }

    public class AutoCADDictionary : AutoCADEntity, IDictionaryWrapper
    {
        private readonly DBDictionary _dict;
        public AutoCADDictionary(DBDictionary dict) : base(dict) => _dict = dict;

        public bool Contains(string key) => _dict.Contains(key);
        public object GetAt(string key) => _dict.GetAt(key);
        public object SetAt(string key, IEntityWrapper entry)
        {
            if (entry is not AutoCADEntity ace) throw new ArgumentException("Invalid entry");
            return _dict.SetAt(key, ace._obj);
        }
    }

    public class AutoCADXrecord : AutoCADEntity, IXrecordWrapper
    {
        private readonly Xrecord _xrec;
        public AutoCADXrecord(Xrecord xrec) : base(xrec) => _xrec = xrec;

        public void SetData(IEnumerable<string> chunks)
        {
            using (var rb = new ResultBuffer())
            {
                foreach (var chunk in chunks)
                {
                    rb.Add(new TypedValue((int)DxfCode.Text, chunk));
                }
                _xrec.Data = rb;
            }
        }

        public IEnumerable<string> GetData()
        {
            if (_xrec.Data == null) return Enumerable.Empty<string>();
            return _xrec.Data.Cast<TypedValue>()
                .Where(tv => tv.TypeCode == (int)DxfCode.Text)
                .Select(tv => tv.Value?.ToString() ?? string.Empty);
        }
    }

    public class AutoCADPolyline : AutoCADEntity, IPolyline
    {
        private readonly Polyline _polyline;
        public AutoCADPolyline(Polyline polyline) : base(polyline)
        {
            _polyline = polyline;
        }

        public double Area => _polyline.Area;
    }
}