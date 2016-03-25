using Net.Teirlinck.FX.Data.ExecutionData;
using System;
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
                    AcctCode = !String.IsNullOrEmpty(apiExecFilter.AccountCode) ? apiExecFilter.AccountCode : String.Empty,
                    Time = apiExecFilter.SinceTime.ToString("yyyyMMdd-hh:mm:ss"),
                    Symbol = !String.IsNullOrEmpty(apiExecFilter.Symbol) ? apiExecFilter.Symbol : String.Empty,
                    SecType = (apiExecFilter.SecurityType != null) ? apiExecFilter.SecurityType.Code : String.Empty,
                    Exchange = !String.IsNullOrEmpty(apiExecFilter.Exchange) ? apiExecFilter.Exchange : String.Empty,
                    Side = apiExecFilter.Side.ToString()
                };
        }

        public static IEnumerable<IBApi.ExecutionFilter> ToIBExecutionFilters(this IEnumerable<ExecutionFilter> executionFilters)
        {
            return executionFilters?.Select(executionFilter => executionFilter.ToIBExecutionFilter());
        }
    }
}
