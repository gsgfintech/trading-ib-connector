using Net.Teirlinck.FX.Data.OrderData;
using static Net.Teirlinck.FX.Data.OrderData.OrderType;
using System.Collections.Generic;
using System.Linq;

namespace Net.Teirlinck.FX.InteractiveBrokersAPI.Extensions
{
    public static class IBOrderExtensions
    {
        public static IBApi.Order ToIBOrder(this Order order)
        {
            if (order == null)
                return null;

            return new IBApi.Order()
            {
                ClientId = order.ClientID,
                OrderId = order.OrderID,
                PermId = order.PermanentID,
                Action = order.Side.ToString(),
                TotalQuantity = order.Quantity,
                OrderType = OrderTypeToIBString(order.Type),
                LmtPrice = order.LimitPrice ?? 0,

                // For bracket orders the AuxPrice is used to store the price of the stop order.
                // For trailing stop orders the AuxPrice is used to store the trailing amount
                //     https://www.interactivebrokers.com.hk/en/software/tws/twsguide_Left.htm#CSHID=usersguidebook%2Fordertypes%2Ftrailing_stop_limit.htm|StartTopic=usersguidebook%2Fordertypes%2Ftrailing_stop_limit.htm|SkinName=ibskin
                AuxPrice = order.StopPrice ?? order.TrailingAmount ?? 0,

                Tif = order.TimeInForce.ToString(),
                OrderRef = order.OurRef,
                Transmit = true,
                ParentId = order.ParentOrderID ?? 0
            };
        }

        public static IEnumerable<IBApi.Order> TOIBOrders(this IEnumerable<Order> orders)
        {
            return orders?.Select(order => order.ToIBOrder());
        }

        public static Order ToOrder(this IBApi.Order ibOrder)
        {
            if (ibOrder == null)
                return null;

            return new Order()
            {
                StopPrice = ibOrder.AuxPrice > 0 ? ibOrder.AuxPrice : (double?)null,
                ClientID = ibOrder.ClientId,
                OurRef = ibOrder.OrderRef,
                LimitPrice = ibOrder.LmtPrice > 0 ? ibOrder.LmtPrice : (double?)null,
                OrderID = ibOrder.OrderId,
                ParentOrderID = ibOrder.ParentId > 0 ? ibOrder.ParentId : (int?)null,
                PermanentID = ibOrder.PermId,
                Side = OrderSideUtils.GetFromStringCode(ibOrder.Action),
                TimeInForce = TimeInForceUtils.GetFromStringCode(ibOrder.Tif),
                Quantity = ibOrder.TotalQuantity,
                Type = IBStringToOrderType(ibOrder.OrderType),
            };
        }

        public static IEnumerable<Order> ToOrders(this IEnumerable<IBApi.Order> ibOrders)
        {
            return ibOrders?.Select(ibOrder => ibOrder.ToOrder());
        }

        private static string OrderTypeToIBString(OrderType type)
        {
            switch (type)
            {
                case LIMIT: return "LMT";
                case MARKET: return "MKT";
                case STOP: return "STP";
                case TRAILING_STOP: return "TRAIL";
                case TRAILING_MARKET_IF_TOUCHED: return "TRAIL MIT";
                default: return null;
            }
        }

        private static OrderType IBStringToOrderType(string ibString)
        {
            switch (ibString)
            {
                case "LMT": return LIMIT;
                case "MKT": return MARKET;
                case "STP": return STOP;
                case "TRAIL": return TRAILING_STOP;
                case "TRAIL MIT": return TRAILING_MARKET_IF_TOUCHED;
                default: return UNKNOWN;
            }
        }
    }
}
