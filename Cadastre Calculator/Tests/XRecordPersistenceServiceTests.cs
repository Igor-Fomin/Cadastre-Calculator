using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Moq;
using Cadastre_Calculator.Abstractions;

namespace Cadastre_Calculator.Tests
{
    [TestFixture]
    public class XRecordPersistenceServiceTests
    {
        [Test]
        public void SaveData_ChunksLargeStringCorrectly()
        {
            // Arrange
            var mockTr = new Mock<ITransactionWrapper>();
            var mockEnt = new Mock<IEntityWrapper>();
            var mockExtDict = new Mock<IDictionaryWrapper>();
            var mockAppDict = new Mock<IDictionaryWrapper>();
            var mockXrec = new Mock<IXrecordWrapper>();

            object entityId = new object();
            object extDictId = new object();
            object appDictId = new object();

            mockTr.Setup(t => t.GetObject(entityId, false)).Returns(mockEnt.Object);
            mockEnt.Setup(e => e.ExtensionDictionary).Returns(extDictId);
            mockTr.Setup(t => t.GetObject(extDictId, true)).Returns(mockExtDict.Object);
            
            mockExtDict.Setup(d => d.Contains("CadastreTools_Data")).Returns(true);
            mockExtDict.Setup(d => d.GetAt("CadastreTools_Data")).Returns(appDictId);
            mockTr.Setup(t => t.GetObject(appDictId, true)).Returns(mockAppDict.Object);

            mockTr.Setup(t => t.CreateXrecord()).Returns(mockXrec.Object);

            var service = new XRecordPersistenceService();
            
            // Create a 600 character string
            string largeData = new string('A', 600);

            // Act
            service.SaveData(mockTr.Object, entityId, "TestKey", largeData);

            // Assert
            // Verifying 3 chunks: 255, 255, 90
            mockXrec.Verify(x => x.SetData(It.Is<IEnumerable<string>>(chunks => 
                chunks.Count() == 3 &&
                chunks.ElementAt(0).Length == 255 &&
                chunks.ElementAt(1).Length == 255 &&
                chunks.ElementAt(2).Length == 90
            )), Times.Once);
        }
    }
}
