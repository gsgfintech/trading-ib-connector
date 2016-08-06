using Net.Teirlinck.FX.Data.ContractData;
using Net.Teirlinck.FX.Data.OrderData;
using static Net.Teirlinck.FX.Data.OrderData.OrderStatusCodeUtils;
using Net.Teirlinck.FX.InteractiveBrokersAPI.Extensions;
using System;

namespace Net.Teirlinck.FX.InteractiveBrokersAPI
{
    public partial class IBClientResponsesManager
    {
        public event Action<int> NextValidIDReceived;
        public event Action<int, Contract, Order, OrderState> OpenOrdersReceived;
        public event Action OrderOpenRequestEnd;
        public event Action<int, OrderStatusCode?, int?, int?, double?, int, int?, double?, int, string> OrderStatusChangeReceived;

        /// <summary>
        /// Upon accepting a Delta-Neutral RFQ(request for quote), the server sends a deltaNeutralValidation() message with the UnderComp structure. 
        /// If the delta and price fields are empty in the original request, the confirmation will contain the current values from the server. 
        /// These values are locked when the RFQ is processed and remain locked until the RFQ is cancelled
        /// </summary>
        /// <param name="reqId">The ID of the data request</param>
        /// <param name="underComp">Underlying component</param>
        public void deltaNeutralValidation(int reqId, IBApi.UnderComp underComp)
        {
            // Not implemented
        }

        /// <summary>
        /// Receives the next valid Order ID
        /// </summary>
        /// <param name="orderId">The next available order ID received from TWS upon connection. Increment all successive orders by one based on this ID</param>
        public void nextValidId(int orderId)
        {
            NextValidIDReceived?.Invoke(orderId);
        }

        /// <summary>
        /// This callback feeds in open orders
        /// </summary>
        /// <param name="orderId">The order ID assigned by TWS. Used to cancel or update the order</param>
        /// <param name="contract">The Contract class attributes describes the contract</param>
        /// <param name="order">The Order class attributes defines the details of the order</param>
        /// <param name="orderState">The orderState attributes includes margin and commissions fields for both pre and post trade data</param>
        public void openOrder(int orderId, IBApi.Contract contract, IBApi.Order order, IBApi.OrderState orderState)
        {
            OpenOrdersReceived?.Invoke(orderId, contract.ToContract(), order.ToOrder(), orderState.ToOrderStatus());
        }

        /// <summary>
        /// This is called at the end of a given request for open orders
        /// </summary>
        public void openOrderEnd()
        {
            OrderOpenRequestEnd?.Invoke();
        }

        /// <summary>
        /// This method is called whenever the status of an order changes. It is also called after reconnecting to TWS if the client has any open orders
        /// </summary>
        /// <param name="orderId">The order Id that was specified previously in the call to placeOrder()</param>
        /// <param name="statusStr">The order status. Possible values include:
        /// PendingSubmit - indicates that you have transmitted the order, but have not yet received confirmation that it has been accepted by the order destination. NOTE: This order status is not sent by TWS and should be explicitly set by the API developer when an order is submitted.
        /// PendingCancel - indicates that you have sent a request to cancel the order but have not yet received cancel confirmation from the order destination. At this point, your order is not confirmed canceled. You may still receive an execution while your cancellation request is pending. NOTE: This order status is not sent by TWS and should be explicitly set by the API developer when an order is canceled.
        /// PreSubmitted - indicates that a simulated order type has been accepted by the IB system and that this order has yet to be elected. The order is held in the IB system until the election criteria are met. At that time the order is transmitted to the order destination as specified.
        /// Submitted - indicates that your order has been accepted at the order destination and is working.
        /// ApiCanceled - after an order has been submitted and before it has been acknowledged, an API client client can request its cancellation, producing this state.
        /// Cancelled - indicates that the balance of your order has been confirmed canceled by the IB system. This could occur unexpectedly when IB or the destination has rejected your order.
        /// Filled - indicates that the order has been completely filled.
        /// Inactive - indicates that the order has been accepted by the system (simulated orders) or an exchange (native orders) but that currently the order is inactive due to system, exchange or other issues.</param>
        /// <param name="filledQuantity">Specifies the number of shares that have been executed</param>
        /// <param name="remainingQuantity">Specifies the number of shares still outstanding</param>
        /// <param name="avgFillPrice">The average price of the shares that have been executed.
        /// This parameter is valid only if the filled parameter value is greater than zero. Otherwise, the price parameter will be zero</param>
        /// <param name="permId">The TWS id used to identify orders. Remains the same over TWS sessions</param>
        /// <param name="parentId">The order ID of the parent order, used for bracket and auto trailing stop orders</param>
        /// <param name="lastFillPrice">The last price of the shares that have been executed. 
        /// This parameter is valid only if the filled parameter value is greater than zero. Otherwise, the price parameter will be zero</param>
        /// <param name="clientId">The ID of the client (or TWS) that placed the order. Note that TWS orders have a fixed clientId and orderId of 0 that distinguishes them from API orders</param>
        /// <param name="whyHeld">This field is used to identify an order held when TWS is trying to locate shares for a short sell. The value used to indicate this is 'locate'</param>
        public void orderStatus(int orderId, string statusStr, int filledQuantity, int remainingQuantity, double avgFillPrice, int permId, int parentId, double lastFillPrice, int clientId, string whyHeld)
        {
            OrderStatusCode status = GetFromStrCode(statusStr);

            OrderStatusChangeReceived?.Invoke(orderId, status != OrderStatusCode.UNKNOWN ? status : (OrderStatusCode?)null, filledQuantity > 0 ? filledQuantity : (int?)null, remainingQuantity > 0 ? remainingQuantity : (int?)null, avgFillPrice > 0 ? avgFillPrice : (double?)null, permId, parentId > 0 ? parentId : (int?)null, lastFillPrice > 0 ? lastFillPrice : (double?)null, clientId, whyHeld);
        }
    }
}
