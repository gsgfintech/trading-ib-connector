using Capital.GSG.FX.Trading.Executor;
using System;
using Net.Teirlinck.FX.Data.MarketData;
using Net.Teirlinck.FX.Data.OrderData;

namespace Net.Teirlinck.FX.InteractiveBrokersAPI.Executor
{
    class IBTestTradingExecutorRunner : ITradingExecutorRunner
    {
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
