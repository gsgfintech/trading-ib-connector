using Net.Teirlinck.FX.Data.NewsBulletinData;
using static Net.Teirlinck.FX.Data.NewsBulletinData.NewsBulletinTypeUtils;
using System;

namespace Net.Teirlinck.FX.InteractiveBrokersAPI
{
    public partial class IBClientResponsesManager
    {
        public event Action<int, NewsBulletinType, string, string> NewsBulletinReceived;

        /// <summary>
        /// Provides news bulletins if the client has subscribed (i.e. by calling the reqNewsBulletins() method)
        /// </summary>
        /// <param name="msgId">The bulletin ID, incrementing for each new bulletin</param>
        /// <param name="msgTypeInt">Specifies the type of bulletin. Valid values include:
        ///     1 = Regular news bulletin
        ///     2 = Exchange no longer available for trading
        ///     3 = Exchange is available for trading</param>
        /// <param name="message">The bulletin's message text</param>
        /// <param name="origExchange">The exchange from which this message originated</param>
        public void updateNewsBulletin(int msgId, int msgTypeInt, string message, string origExchange)
        {
            if (NewsBulletinReceived != null)
            {
                NewsBulletinType msgType = GetByIntCode(msgTypeInt);

                NewsBulletinReceived?.Invoke(msgId, msgType, message, origExchange);
            }
        }
    }
}
