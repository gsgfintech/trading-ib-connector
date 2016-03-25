using Net.Teirlinck.FX.Data.ContractData;
using System.Collections.Generic;
using System.Linq;

namespace Net.Teirlinck.FX.InteractiveBrokersAPI.Extensions
{
    public static class IBUnderCompExtensions
    {
        public static IBApi.UnderComp ToIBApiUnderComp(this UnderComp underComp)
        {
            if (underComp == null)
                return null;
            else
                return new IBApi.UnderComp()
                {
                    ConId = underComp.ContractID,
                    Delta = underComp.Delta.HasValue ? underComp.Delta.Value : 0.0,
                    Price = underComp.Price.HasValue ? underComp.Price.Value : 0.0
                };
        }

        public static IEnumerable<IBApi.UnderComp> ToIBApiUnderComps(this IEnumerable<UnderComp> underComps)
        {
            return underComps?.Select(underComp => underComp.ToIBApiUnderComp());
        }

        public static UnderComp ToUnderComp(this IBApi.UnderComp ibUnderComp)
        {
            if (ibUnderComp == null)
                return null;
            else
                return new UnderComp()
                {
                    ContractID = ibUnderComp.ConId,
                    Delta = ibUnderComp.Delta,
                    Price = ibUnderComp.Price
                };
        }

        public static IEnumerable<UnderComp> ToUnderComps(this IEnumerable<IBApi.UnderComp> ibUnderComps)
        {
            return ibUnderComps?.Select(ibUnderComp => ibUnderComp.ToUnderComp());
        }
    }
}
