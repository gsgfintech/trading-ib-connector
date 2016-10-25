using Capital.GSG.FX.Data.Core.ExecutionData;
using Capital.GSG.FX.Data.Core.OrderData;
using System.Collections.Generic;
using System.Linq;

namespace Net.Teirlinck.FX.InteractiveBrokersAPI.Extensions
{
    public static class IBExecutionFilterExtensions
    {
        public static IBApi.ExecutionFilter ToIBExecutionFilter(this ExecutionFilter apiExecFilter)
        {
            if (apiExecFilter == null)
                return null;
            else
                return new IBApi.ExecutionFilter()
                {
                    ClientId = apiExecFilter.ClientID,
                    AcctCode = !string.IsNullOrEmpty(apiExecFilter.AccountCode) ? apiExecFilter.AccountCode : string.Empty,
                    Time = apiExecFilter.SinceTime.ToString("yyyyMMdd-hh:mm:ss"),
                    Symbol = !string.IsNullOrEmpty(apiExecFilter.Symbol) ? apiExecFilter.Symbol : string.Empty,
                    SecType = apiExecFilter.SecurityType.ToString(),
                    Exchange = !string.IsNullOrEmpty(apiExecFilter.Exchange) ? apiExecFilter.Exchange : string.Empty,
                    Side = apiExecFilter.Side != OrderSide.UNKNOWN ? apiExecFilter.Side.ToString() : string.Empty
                };
        }

        public static IEnumerable<IBApi.ExecutionFilter> ToIBExecutionFilters(this IEnumerable<ExecutionFilter> executionFilters)
        {
            return executionFilters?.Select(executionFilter => executionFilter.ToIBExecutionFilter());
        }
    }
}
