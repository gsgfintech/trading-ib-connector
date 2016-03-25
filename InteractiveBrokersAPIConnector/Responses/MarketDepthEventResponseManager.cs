using System;

namespace Net.Teirlinck.FX.InteractiveBrokersAPI
{
    public partial class IBClientResponsesManager
    {
        public event Action<int, int, int, int, double, int> OrderBookUpdateReceived;
        public event Action<int, int, string, int, int, double, int> OrderBookLevel2UpdateReceived;

        /// <summary>
        /// Returns market depth (the order book) in response to reqMktDepth()
        /// </summary>
        /// <param name="tickerId">The request's identifier</param>
        /// <param name="position">Specifies the row Id of this market depth entry</param>
        /// <param name="operation">Identifies how this order should be applied to the market depth. Valid values are:
        ///     0 = insert (insert this new order into the row identified by 'position')·
        ///     1 = update (update the existing order in the row identified by 'position')·
        ///     2 = delete (delete the existing order at the row identified by 'position')</param>
        /// <param name="side">Identifies the side of the book that this order belongs to. Valid values are:
        ///     0 = ask
        ///     1 = bid.</param>
        /// <param name="price">The order price</param>
        /// <param name="size">The order size</param>
        public void updateMktDepth(int tickerId, int position, int operation, int side, double price, int size)
        {
            OrderBookUpdateReceived?.Invoke(tickerId, position, operation, side, price, size);
        }

        /// <summary>
        /// Returns Level II market depth in response to reqMktDepth()
        /// </summary>
        /// <param name="tickerId">The request's identifier</param>
        /// <param name="position">Specifies the row Id of this market depth entry</param>
        /// <param name="marketMaker">Specifies the exchange holding the order</param>
        /// <param name="operation">Identifies how this order should be applied to the market depth. Valid values are:
        ///     0 = insert (insert this new order into the row identified by 'position')·
        ///     1 = update (update the existing order in the row identified by 'position')·
        ///     2 = delete (delete the existing order at the row identified by 'position')</param>
        /// <param name="side">Identifies the side of the book that this order belongs to. Valid values are:
        ///     0 = ask
        ///     1 = bid.</param>
        /// <param name="price">The order price</param>
        /// <param name="size">The order size</param>
        public void updateMktDepthL2(int tickerId, int position, string marketMaker, int operation, int side, double price, int size)
        {
            OrderBookLevel2UpdateReceived?.Invoke(tickerId, position, marketMaker, operation, side, price, size);
        }
    }
}
