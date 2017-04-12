using Net.Teirlinck.FX.InteractiveBrokersAPI.Extensions;
using System;
using System.Collections.Generic;
using System.Linq;
using log4net;
using Capital.GSG.FX.Data.Core.ContractData;
using Capital.GSG.FX.Data.Core.MarketData;
using Capital.GSG.FX.Utils.Core;

namespace Net.Teirlinck.FX.InteractiveBrokersAPI.Requests
{
    public class MarketDataRequestManager
    {
        private static ILog logger = LogManager.GetLogger(typeof(MarketDataRequestManager));

        private IBApi.EClientSocket ClientSocket { get; set; }

        public MarketDataRequestManager(IBClientRequestsManager requestManager)
        {
            ClientSocket = requestManager.ClientSocket;
        }

        /// <summary>
        /// Call this method to request market data. The market data will be returned by the tickPrice(), tickSize(), tickOptionComputation(), tickGeneric(), tickString() and tickEFP() methods
        /// </summary>
        /// <param name="requestID">The request ID. Must be a unique value. When the market data returns, it will be identified by this tag. This is also used when canceling the market data</param>
        /// <param name="contract">This class contains attributes used to describe the contract</param>
        /// <param name="genericTicksList">An enumerable of generic tick types
        ///      - 100 	Option Volume (currently for stocks)
        ///      - 101 	Option Open Interest (currently for stocks) 
        ///      - 104 	Historical Volatility (currently for stocks)
        ///      - 106 	Option Implied Volatility (currently for stocks)
        ///      - 162 	Index Future Premium 
        ///      - 165 	Miscellaneous Stats 
        ///      - 221 	Mark Price (used in TWS P&L computations) 
        ///      - 225 	Auction values (volume, price and imbalance) 
        ///      - 233 	RTVolume - contains the last trade price, last trade size, last trade time, total volume, VWAP, and single trade flag.
        ///      - 236 	Shortable
        ///      - 256 	Inventory 	 
        ///      - 258 	Fundamental Ratios 
        ///      - 411 	Realtime Historical Volatility 
        ///      - 456 	IBDividends
        /// </param>
        /// <param name="isSnapshot">When set to True, returns a single snapshot of market data. 
        /// When set to False, returns continues updates. Do not enter any genericTicklist values if you use snapshot</param>
        public void RequestMarketData(int requestID, Contract contract, IEnumerable<GenericTickType> genericTicksList = null, bool isSnapshot = false)
        {
            try
            {
                string genericTicksListStr = String.Empty;

                if (!genericTicksList.IsNullOrEmpty() && !isSnapshot)
                    genericTicksListStr = genericTicksList.Aggregate(String.Empty, (cur, next) => { return $"{cur},{next.ID}"; });

                ClientSocket.reqMktData(requestID, contract.ToIBContract(), genericTicksListStr, isSnapshot, false, null);
            }
            catch (Exception ex)
            {
                logger.Error("Failed to subscribe to market data", ex);
            }
        }

        /// <summary>
        /// Cancels a market data request
        /// </summary>
        /// <param name="requestID">The Id that was specified in the call to RequestMarketData()</param>
        public void CancelMarketDataRequest(int requestID)
        {
            ClientSocket.cancelMktData(requestID);
        }

        /// <summary>
        /// Request the calculation of the implied volatility based on hypothetical option and its underlying prices. The calculation will be returned by EWrapper's OptionComputationTickReceived event
        /// </summary>
        /// <param name="requestID">Unique identifier of the request</param>
        /// <param name="contract">The option's contract for which you want to calculate volatility</param>
        /// <param name="optionPrice">The hypothetical price of the option</param>
        /// <param name="underlyingPrice">The hypothetical price of the underlying</param>
        public void RequestCalculateImpliedVolatility(int requestID, Contract contract, double optionPrice, double underlyingPrice)
        {
            ClientSocket.calculateImpliedVolatility(requestID, contract.ToIBContract(), optionPrice, underlyingPrice, null);
        }

        /// <summary>
        /// Cancels a request to calculate implied volatility for a supplied option price and underlying price
        /// </summary>
        /// <param name="requestID">The identifier of the implied volatility's calculation request</param>
        public void CancelCalculateImpliedVolatilityRequest(int requestID)
        {
            ClientSocket.cancelCalculateImpliedVolatility(requestID);
        }

        /// <summary>
        /// Calculates an option's price based on the provided volatility and its underlying's price. The calculation will be returned by the ResponsesManager OptionComputationTickReceived event
        /// </summary>
        /// <param name="requestID">The request's unique identifier</param>
        /// <param name="contract">The option contract for which you want to calculate the price</param>
        /// <param name="volatility">The hypothetical volatility</param>
        /// <param name="underlyingPrice">The hypothetical price of the underlying</param>
        public void RequestCalculateOptionPrice(int requestID, Contract contract, double volatility, double underlyingPrice)
        {
            ClientSocket.calculateOptionPrice(requestID, contract.ToIBContract(), volatility, underlyingPrice, null);
        }

        /// <summary>
        /// Call this function to cancel a request to calculate the option price and greek values for a supplied volatility and underlying price
        /// </summary>
        /// <param name="requestID">The request ID</param>
        public void CancelCalculateOptionPriceRequest(int requestID)
        {
            ClientSocket.cancelCalculateOptionPrice(requestID);
        }

        /// <summary>
        /// The API can receive frozen market data from Trader Workstation. Frozen market data is the last data recorded in our system. 
        /// During normal trading hours, the API receives real-time market data. If you use this function, you are telling TWS to automatically switch to frozen market data after the close. 
        /// Then, before the opening of the next trading day, market data will automatically switch back to real-time market data
        /// </summary>
        /// <param name="marketDataType"></param>
        public void SetMarketDataTypeForRequest(MarketDataType marketDataType)
        {
            ClientSocket.reqMarketDataType((int)marketDataType);
        }

        /// <summary>
        /// Returns data histogram of specified contract
        /// </summary>
        /// <param name="tickerId">An identifier for the request</param>
        /// <param name="contract">Contract object for which histogram is being requested</param>
        /// <param name="useRegularTradingHours">Use regular trading hours only</param>
        /// <param name="period">Period of which data is being requested, e.g. "3 days"</param>
        public void RequestHistogramData(int tickerId, Contract contract, bool useRegularTradingHours, string period)
        {
            ClientSocket.reqHistogramData(tickerId, contract.ToIBContract(), useRegularTradingHours, period);
        }
    }
}
