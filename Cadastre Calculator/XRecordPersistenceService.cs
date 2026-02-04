using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using Cadastre_Calculator.Abstractions;

namespace Cadastre_Calculator
{
    public class XRecordPersistenceService
    {
        private const string AppDictionaryName = "CadastreTools_Data";
        private const int MaxChunkSize = 255;

        public void SaveData<T>(ITransactionWrapper tr, object entityId, string key, T data)
        {
            string jsonString = JsonSerializer.Serialize(data);
            var ent = tr.GetObject(entityId);

            if (ent.ExtensionDictionary.Equals(null) || ent.ExtensionDictionary.ToString() == "0") // Simplistic check for ObjectId.Null
            {
                ent.CreateExtensionDictionary();
            }

            var extensionDict = (IDictionaryWrapper)tr.GetObject(ent.ExtensionDictionary, true);
            
            IDictionaryWrapper appDict;
            if (extensionDict.Contains(AppDictionaryName))
            {
                appDict = (IDictionaryWrapper)tr.GetObject(extensionDict.GetAt(AppDictionaryName), true);
            }
            else
            {
                appDict = tr.CreateDictionary();
                extensionDict.SetAt(AppDictionaryName, appDict);
            }

            var xRec = tr.CreateXrecord();
            xRec.SetData(ChunkString(jsonString, MaxChunkSize));
            appDict.SetAt(key, xRec);
        }

        public T? LoadData<T>(ITransactionWrapper tr, object entityId, string key)
        {
            var ent = tr.GetObject(entityId);
            if (ent.ExtensionDictionary.ToString() == "0") return default;

            var extensionDict = (IDictionaryWrapper)tr.GetObject(ent.ExtensionDictionary);
            if (!extensionDict.Contains(AppDictionaryName)) return default;

            var appDict = (IDictionaryWrapper)tr.GetObject(extensionDict.GetAt(AppDictionaryName));
            if (!appDict.Contains(key)) return default;

            var xRec = (IXrecordWrapper)tr.GetObject(appDict.GetAt(key));
            
            StringBuilder sb = new StringBuilder();
            foreach (var chunk in xRec.GetData())
            {
                sb.Append(chunk);
            }

            string jsonString = sb.ToString();
            return string.IsNullOrEmpty(jsonString) ? default : JsonSerializer.Deserialize<T>(jsonString);
        }

        private IEnumerable<string> ChunkString(string str, int maxChunkSize)
        {
            if (string.IsNullOrEmpty(str)) yield break;
            for (int i = 0; i < str.Length; i += maxChunkSize)
            {
                yield return str.Substring(i, Math.Min(maxChunkSize, str.Length - i));
            }
        }
    }
}
