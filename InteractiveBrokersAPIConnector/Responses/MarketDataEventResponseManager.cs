using Net.Teirlinck.FX.Data.MarketData;
using System;

namespace Net.Teirlinck.FX.InteractiveBrokersAPI
{
    public partial class IBClientResponsesManager
    {
        public event Action<int, int, double, string, double, int, string, double, double> EFPTickReceived;
        public event Action<int, int, double> GenericTickReceived;
        public event Action<int, int, double, double, double, double, double, double, double, double> OptionComputationTickReceived;
        public event Action<int> SnapShotEndTickReceived;
        public event Action<int, MarketDataTickType, int> MarketDataSizeTickReceived;
        public event Action<int, MarketDataTickType, double, bool> MarketDataPriceTickReceived;
        public event Action<int, MarketDataTickType, string> MarketDataStringTickReceived;
        public event Func<int, int, MarketDataType> MarketDataTypeReceived;

        /// <summary>
        /// Market data callback for Exchange for Physicals
        /// </summary>
        /// <param name="tickerId">The request's unique identifier</param>
        /// <param name="tickType">Specifies the type of tick being received</param>
        /// <param name="basisPoints">Annualized basis points, which is representative of the financing rate that can be directly compared to broker rates</param>
        /// <param name="formattedBasisPoints">Annualized basis points as a formatted string that depicts them in percentage form</param>
        /// <param name="impliedFuture">Implied futures price</param>
        /// <param name="holdDays">The number of hold days until the expiry of the EFP</param>
        /// <param name="futureExpiry">The expiration date of the single stock future</param>
        /// <param name="dividendImpact">The dividend impact upon the annualized basis points interest rate</param>
        /// <param name="dividendsToExpiry">The dividends expected until the expiration of the single stock future</param>
        public void tickEFP(int tickerId, int tickType, double basisPoints, string formattedBasisPoints, double impliedFuture, int holdDays, string futureExpiry, double dividendImpact,
            double dividendsToExpiry)
        {
            EFPTickReceived?.Invoke(tickerId, tickType, basisPoints, formattedBasisPoints, impliedFuture, holdDays, futureExpiry, dividendImpact, dividendsToExpiry);
        }

        /// <summary>
        /// Market data callback
        /// </summary>
        /// <param name="tickerId">The request's unique identifier</param>
        /// <param name="field">Specifies the type of tick being received. Pass the field value into TickType.getField(int tickType) to retrieve the field description</param>
        /// <param name="value">The value of the specified field</param>
        public void tickGeneric(int tickerId, int field, double value)
        {
            GenericTickReceived?.Invoke(tickerId, field, value);
        }

        /// <summary>
        /// This method is called when the market in an option or its underlying moves. 
        /// TWS’s option model volatilities, prices, and deltas, along with the present value of dividends expected on that option's underlying are received
        /// </summary>
        /// <param name="tickerId">The request's unique identifier</param>
        /// <param name="field">Specifies the type of option computation. Pass the field value into TickType.getField(int tickType) to retrieve the field description</param>
        /// <param name="impliedVolatility">The implied volatility calculated by the TWS option modeler, using the specified tick type value</param>
        /// <param name="delta">The option delta value</param>
        /// <param name="optPrice">The option price</param>
        /// <param name="pvDividend">The present value of dividends expected on the option's underlying</param>
        /// <param name="gamma">The option gamma value</param>
        /// <param name="vega">The option vega value</param>
        /// <param name="theta">The option theta value</param>
        /// <param name="undPrice">The price of the underlying</param>
        public void tickOptionComputation(int tickerId, int field, double impliedVolatility, double delta, double optPrice, double pvDividend, double gamma, double vega, double theta, double undPrice)
        {
            OptionComputationTickReceived?.Invoke(tickerId, field, impliedVolatility, delta, optPrice, pvDividend, gamma, vega, theta, undPrice);
        }

        /// <summary>
        /// Market data tick price callback, handles all price-related ticks
        /// </summary>
        /// <param name="tickerId">The request's unique identifier</param>
        /// <param name="field">Specifies the type of price. Pass the field value into TickType.getField(int tickType) to retrieve the field description</param>
        /// <param name="price">The actual price</param>
        /// <param name="canAutoExecute">Specifies whether the price tick is available for automatic execution</param>
        public void tickPrice(int tickerId, int field, double price, int canAutoExecute)
        {
            MarketDataPriceTickReceived?.Invoke(tickerId, (MarketDataTickType)field, price, (canAutoExecute == 1));
        }

        /// <summary>
        /// Market data tick size callback, handles all size-related ticks
        /// </summary>
        /// <param name="tickerId">The request's unique identifier</param>
        /// <param name="field">The type of size being received. Pass the field value into TickType.getField(int tickType) to retrieve the field description</param>
        /// <param name="size">The actual size</param>
        public void tickSize(int tickerId, int field, int size)
        {
            MarketDataSizeTickReceived?.Invoke(tickerId, (MarketDataTickType)field, size);
        }

        /// <summary>
        /// This is called when a snapshot market data subscription has been fully received and there is nothing more to wait for. This also covers the timeout case
        /// </summary>
        /// <param name="tickerId">The request's unique identifier</param>
        public void tickSnapshotEnd(int tickerId)
        {
            SnapShotEndTickReceived?.Invoke(tickerId);
        }

        /// <summary>
        /// Market data callback
        /// </summary>
        /// <param name="tickerId">The request's unique identifier</param>
        /// <param name="field">Specifies the type of tick being received. Pass the field value into TickType.getField(int tickType) to retrieve the field description</param>
        /// <param name="value">The value of the specified field</param>
        public void tickString(int tickerId, int field, string value)
        {
            MarketDataStringTickReceived?.Invoke(tickerId, (MarketDataTickType)field, value);
        }

        /// <summary>
        /// TWS sends a marketDataType(type) callback to the API, where type is set to Frozen or RealTime, to announce that market data has been switched between frozen and real-time. 
        /// This notification occurs only when market data switches between real-time and frozen. 
        /// The marketDataType( ) callback accepts a reqId parameter and is sent per every subscription because different contracts can generally trade on a different schedule
        /// </summary>
        /// <param name="reqId">The request's identifier</param>
        /// <param name="marketDataType">1 for real-time streaming market data or 2 for frozen market data</param>
        public void marketDataType(int reqId, int marketDataType)
        {
            MarketDataTypeReceived?.Invoke(reqId, marketDataType);
        }
    }
}
