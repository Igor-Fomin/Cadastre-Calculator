using System;
using Cadastre_Calculator.Abstractions;

namespace Cadastre_Calculator.Logic
{
    public class ParcelValidator
    {
        private const double MinArea = 500.0;

        public bool ValidateParcelArea(ITransactionWrapper tr, object parcelId)
        {
            if (tr == null) throw new ArgumentNullException(nameof(tr));

            var entity = tr.GetObject(parcelId);

            if (entity is not IPolyline polyline)
            {
                // Logic decision: If it's not a polyline, is it valid? Assuming false for this example.
                return false; 
            }

            return polyline.Area >= MinArea;
        }
    }
}
