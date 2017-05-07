using Capital.GSG.FX.Data.Core.ContractData;
using Capital.GSG.FX.Utils.Core;
using Capital.GSG.FX.Utils.Core.Logging;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Net.Teirlinck.FX.InteractiveBrokersAPI.Executor
{
    public class IBFundamentalDataProvider
    {
        private readonly ILogger logger = GSGLoggerFactory.Instance.CreateLogger<IBFundamentalDataProvider>();

        private readonly IBClient ibClient;
        private readonly Dictionary<Cross, Contract> contracts;

        public IBFundamentalDataProvider(IBClient ibClient, Dictionary<Cross, Contract> contracts)
        {
            this.ibClient = ibClient;
            this.contracts = contracts;

            this.ibClient.ResponseManager.FundamentalDataReceived += FundamentalDataReceived;
        }

        public void RequestFundamentalData(Cross cross)
        {
            if(contracts.IsNullOrEmpty() || !contracts.ContainsKey(cross))
            {
                logger.Error($"Unknown cross {cross}");
                return;
            }

            ibClient.RequestManager.FundamentalDataRequestManager.RequestFundamentalData(8001, contracts[cross], Capital.GSG.FX.Data.Core.FundamentalData.FundamentalDataReportType.ANALYST_ESTIMATES);
        }

        private string FundamentalDataReceived(int requestId, string data)
        {
            logger.Info($"ReqId={requestId}, Data={data}");
            return null;
        }
    }
}
