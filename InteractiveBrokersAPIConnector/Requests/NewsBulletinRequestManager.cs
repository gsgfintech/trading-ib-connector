using IBApi;

namespace Net.Teirlinck.FX.InteractiveBrokersAPI.Requests
{
    public class NewsBulletinRequestManager
    {
        private EClientSocket ClientSocket { get; set; }

        public NewsBulletinRequestManager(IBClientRequestsManager requestsManager)
        {
            ClientSocket = requestsManager.ClientSocket;
        }

        /// <summary>
        /// Call this method to start receiving news bulletins. Each bulletin will be returned by the NewsBulletinReceived event of the responses manager
        /// </summary>
        /// <param name="alsoReturnPastMessages">If set to TRUE, returns all the existing bulletins for the current day and any new ones. IF set to FALSE, will only return new bulletins</param>
        public void RequestNewsBulletins(bool alsoReturnPastMessages)
        {
            ClientSocket.reqNewsBulletins(alsoReturnPastMessages);
        }

        /// <summary>
        /// Call this method to stop receiving news bulletins
        /// </summary>
        public void CancelNewsBulletinsRequests()
        {
            ClientSocket.cancelNewsBulletin();
        }
    }
}
