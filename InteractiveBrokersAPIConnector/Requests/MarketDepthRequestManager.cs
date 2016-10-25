using Capital.GSG.FX.Data.Core.ContractData;
using Net.Teirlinck.FX.InteractiveBrokersAPI.Extensions;

namespace Net.Teirlinck.FX.InteractiveBrokersAPI.Requests
{
    public class MarketDepthRequestManager
    {
        private IBApi.EClientSocket ClientSocket { get; set; }

        public MarketDepthRequestManager(IBClientRequestsManager requestsManager)
        {
            ClientSocket = requestsManager.ClientSocket;
        }

        /// <summary>
        /// Call this method to request market depth (order book) for a specific contract. 
        /// The market depth will be returned by the OrderBookUpdateReceived and OrderBookLevel2UpdateReceived events of the responses manager
        /// </summary>
        /// <param name="requestID">The ticker Id. Must be a unique value. When the market depth data returns, it will be identified by this ID. This is also used when canceling the market depth</param>
        /// <param name="contract">This class contains attributes used to describe the contract</param>
        /// <param name="numberRows">Specifies the number of market depth rows on each side of the order book to return</param>
        public void RequestOrderBook(int requestID, Contract contract, int numberRows)
        {
            ClientSocket.reqMarketDepth(requestID, contract.ToIBContract(), numberRows, null);
        }

        /// <summary>
        /// Cancels market depth for the specified ID
        /// </summary>
        /// <param name="requestID">The ID that was specified in the call to reqMktDepth()</param>
        public void CancelOrderBookRequest(int requestID)
        {
            ClientSocket.cancelMktDepth(requestID);
        }
    }
}
