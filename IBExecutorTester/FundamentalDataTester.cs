using Capital.GSG.FX.Data.Core.ContractData;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Net.Teirlinck.FX.InteractiveBrokersAPI.Executor
{
    internal static class FundamentalDataTester
    {
        internal static void TestFundamentalData(IBFundamentalDataProvider fundamentalDataProvider)
        {
            fundamentalDataProvider.RequestFundamentalData(Cross.EURUSD);
        }
    }
}
