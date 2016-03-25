using Net.Teirlinck.FX.Data.ContractData;
using static Net.Teirlinck.FX.Data.ContractData.SecIdentifierTypes;
using System.Collections.Generic;
using System.Linq;

namespace Net.Teirlinck.FX.InteractiveBrokersAPI.Extensions
{
    public static class IBSecIdentifierTypeExtensions
    {
        public static IEnumerable<SecIdentifierType> ToSecIdentifierTypesList(this IEnumerable<IBApi.TagValue> ibSecIdentifierTypes)
        {
            return ibSecIdentifierTypes?.Select(ibSecIdentifierType => GetFromString(ibSecIdentifierType.Value));
        }
    }
}
