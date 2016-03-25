using IBApi;

namespace Net.Teirlinck.FX.InteractiveBrokersAPI.Requests
{
    public class DisplayGroupsRequestManager
    {
        private EClientSocket ClientSocket { get; set; }

        public DisplayGroupsRequestManager(IBClientRequestsManager requestsManager)
        {
            ClientSocket = requestsManager.ClientSocket;
        }

        /// <summary>
        /// Query display groups
        /// </summary>
        /// <param name="requestID">The unique number that will be associated with the response</param>
        public void QueryDisplayGroups(int requestID)
        {
            ClientSocket.queryDisplayGroups(requestID);
        }

        /// <summary></summary>
        /// <param name="requestID">The unique number associated with the notification</param>
        /// <param name="groupID">The ID of the group, currently it is a number from 1 to 7. This is the display group subscription request sent by the API to TWS</param>
        public void SubscribeToGroupEvents(int requestID, int groupID)
        {
            ClientSocket.subscribeToGroupEvents(requestID, groupID);
        }

        /// <summary></summary>
        /// <param name="requestID">The requestId specified in subscribeToGroupEvents()</param>
        /// <param name="contractInfo">The encoded value that uniquely represents the contract in IB. Possible values include:
        ///     none = empty selection
        ///     contractID@exchange – any non-combination contract. Examples: 8314@SMART for IBM SMART; 8314@ARCA for IBM @ARCA.
        ///     combo = if any combo is selected.</param>
        public void UpdateDisplayGroups(int requestID, string contractInfo = "none")
        {
            ClientSocket.updateDisplayGroup(requestID, contractInfo);
        }

        /// <summary></summary>
        /// <param name="requestID">The requestId specified in subscribeToGroupEvents()</param>
        public void UnsubscribeFromGroupEvents(int requestID)
        {
            ClientSocket.unsubscribeFromGroupEvents(requestID);
        }
    }
}
