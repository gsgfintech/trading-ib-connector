using Net.Teirlinck.FX.Data.ContractData;
using Net.Teirlinck.FX.Data.FundamentalData;
using Net.Teirlinck.FX.InteractiveBrokersAPI.Extensions;

namespace Net.Teirlinck.FX.InteractiveBrokersAPI.Requests
{
    public class FundamentalDataRequestManager
    {
        private IBApi.EClientSocket ClientSocket { get; set; }

        public FundamentalDataRequestManager(IBClientRequestsManager requestsManager)
        {
            ClientSocket = requestsManager.ClientSocket;
        }

        /// <summary>
        /// Call this method to receive Reuters global fundamental data for stocks. There must be a subscription to Reuters Fundamental set up in Account Management before you can receive this data.
        /// RequestFundamentalData can handle conid specified in the Contract object, but not tradingClass or multiplier. 
        /// This is because RequestFundamentalData is used only for stocks and stocks do not have a multiplier and trading class
        /// </summary>
        /// <param name="requestID">The ID of the data request. Ensures that responses are matched to requests if several requests are in process</param>
        /// <param name="contract">This structure contains a description of the contract for which Reuters Fundamental data is being requested</param>
        /// <param name="reportType">Type of the XML report requested</param>
        public void RequestFundamentalData(int requestID, Contract contract, FundamentalDataReportType reportType)
        {
            ClientSocket.reqFundamentalData(requestID, contract.ToIBContract(), reportType.ToStrCode(), null);
        }

        /// <summary>
        /// Call this method to stop receiving Reuters global fundamental data
        /// </summary>
        /// <param name="requestID">The ID of the data request</param>
        public void CancelFundamentalDataRequest(int requestID)
        {
            ClientSocket.cancelFundamentalData(requestID);
        }
    }
}
