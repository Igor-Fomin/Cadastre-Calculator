using NUnit.Framework;
using Moq;
using Cadastre_Calculator.Abstractions;
using Cadastre_Calculator.Logic;

namespace Cadastre_Calculator.Tests
{
    [TestFixture]
    public class ParcelValidatorTests
    {
        [Test]
        public void ValidateParcelArea_ReturnsFalse_WhenAreaIsTooSmall()
        {
            // Arrange
            var mockRepo = new MockRepository(MockBehavior.Strict);
            var mockTransaction = mockRepo.Create<ITransactionWrapper>();
            var mockPolyline = mockRepo.Create<IPolyline>();

            // Simulate a polyline with Area = 100.0 (less than 500.0)
            mockPolyline.Setup(p => p.Area).Returns(100.0);

            // Simulate GetObject returning our mock polyline
            // We use an arbitrary object as the ID since the interface uses 'object'
            object fakeId = new object(); 
            // Explicitly provide the optional argument to avoid Moq/expression tree issues
            mockTransaction.Setup(tr => tr.GetObject(fakeId, false)).Returns(mockPolyline.Object);

            var validator = new ParcelValidator();

            // Act
            bool result = validator.ValidateParcelArea(mockTransaction.Object, fakeId);

            // Assert
            Assert.That(result, Is.False, "Validator should return false for area < 500");
        }
    }
}