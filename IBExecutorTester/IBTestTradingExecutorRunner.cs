using Capital.GSG.FX.Data.Core.MarketData;
using Capital.GSG.FX.Data.Core.OrderData;
using Capital.GSG.FX.Trading.Executor.Core;
using System;

namespace Net.Teirlinck.FX.InteractiveBrokersAPI.Executor
{
    class IBTestTradingExecutorRunner : ITradingExecutorRunner
    {
        public void OnFutureTick(FutMarketDataTick tick)
        {
            Console.WriteLine($"Fut tick update: {tick.Timestamp}|{tick.Symbol}|{tick.Expiry:yyyyMMdd}|Ask={tick.Ask}|AskSize={tick.AskSize}|Bid={tick.Bid}|BidSize={tick.BidSize}|Volume={tick.DayVolume}|Open={tick.DayOpen}|High={tick.DayHigh}|Low={tick.DayLow}|Close={tick.PrevDayClose}|LastTradePrice={tick.LastTradePrice}|LastTradeSize={tick.LastTradeSize}");
        }

        public void OnMdPriceTick(PriceTick priceTick)
        {
        }

        public void OnMdRtBar(RTBar rtBar)
        {
            Console.WriteLine($"Timestamp:{rtBar.Timestamp}|Now:{DateTime.Now}|RTBar:{rtBar}");

            Program.RtBarsCounter++;
        }

        public void OnMdSizeTick(SizeTick sizeTick)
        {
        }

        public void StopTradingStrategy(string name, string version, string message, OrderOrigin origin = OrderOrigin.PositionClose_CircuitBreaker)
        {
        }
    }
}
