using Capital.GSG.FX.Data.Core.DisplayGroup;
using System;
using System.Collections.Generic;

namespace Net.Teirlinck.FX.InteractiveBrokersAPI
{
    public partial class IBClientResponsesManager
    {
        public event Func<int, string, IEnumerable<DisplayGroup>> DisplayGroupsListReceived;
        public event Func<int, string, DisplayGroup> DisplayGroupUpdated;

        /// <summary>
        /// This callback is a one-time response to queryDisplayGroups()
        /// </summary>
        /// <param name="reqId">The requestId specified in queryDisplayGroups()</param>
        /// <param name="groups">A list of integers representing visible group ID separated by the “|” character, and sorted by most used group first. 
        /// This list will not change during TWS session (in other words, user cannot add a new group; sorting can change though). 
        /// Example: “3|1|2”</param>
        public void displayGroupList(int reqId, string groups)
        {
            DisplayGroupsListReceived?.Invoke(reqId, groups);
        }

        /// <summary>
        /// This is sent by TWS to the API client once after receiving the subscription request subscribeToGroupEvents() 
        /// and will be sent again if the selected contract in the subscribed display group has changed
        /// </summary>
        /// <param name="reqId">The requestId specified in subscribeToGroupEvents()</param>
        /// <param name="contractInfo">The encoded value that uniquely represents the contract in IB. Possible values include:
        ///     * none = empty selection
        ///     * contractID@exchange – any non-combination contract. Examples: 8314@SMART for IBM SMART; 8314@ARCA for IBM @ARCA.
        ///     * combo = if any combo is selected</param>
        public void displayGroupUpdated(int reqId, string contractInfo)
        {
            DisplayGroupUpdated?.Invoke(reqId, contractInfo);
        }
    }
}
