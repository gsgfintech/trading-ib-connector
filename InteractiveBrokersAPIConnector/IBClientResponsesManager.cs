using Capital.GSG.FX.IBData;
using IBApi;
using log4net;
using System;

namespace Net.Teirlinck.FX.InteractiveBrokersAPI
{
    public partial class IBClientResponsesManager : EWrapper
    {
        private static ILog logger = LogManager.GetLogger(nameof(IBClientResponsesManager));

        public event Func<long, DateTime> CurrentServerTimeReceived;
        public event Action<APIError> ErrorMessageReceived;
        public event Func<string, string> LegacyErrorMessageReceived;
        public event Func<bool, string, bool> VerifyAPICompleted;
        public event Func<string, string, string> VerifyAPIMessageReceived;

        public event Action ConnectionOpened;
        public event Action ConnectionClosed;

        /// <summary>
        /// Callback signifying completion of successful connection
        /// </summary>
        public void connectAck()
        {
            ConnectionOpened?.Invoke();
        }

        public void connectionClosed()
        {
            ConnectionClosed?.Invoke();
        }

        /// <summary>
        /// This method receives the current system time on IB's server as a result of calling reqCurrentTime()
        /// </summary>
        /// <param name="time">The current system time on the IB server</param>
        public void currentTime(long time)
        {
            CurrentServerTimeReceived?.Invoke(time);
        }

        /// <summary>
        /// This method is called when there is an error with the communication or when TWS wants to send a message to the client
        /// </summary>
        /// <param name="requestID">The request identifier that generated the error</param>
        /// <param name="errorCode">The code identifying the error. For information on error codes, see Error Codes</param>
        /// <param name="errorMsg">The description of the error</param>
        public void error(int requestID, int errorCode, string errorMsg)
        {
            ErrorMessageReceived?.Invoke(new APIError(requestID, errorCode, errorMsg));
        }

        /// <summary>
        /// This method is called when TWS wants to send an error message to the client. (V1)
        /// </summary>
        /// <param name="message">This is the text of the error message</param>
        public void error(string message)
        {
            LegacyErrorMessageReceived?.Invoke(message);
        }

        /// <summary>
        /// This method is called when an exception occurs while handling a request
        /// </summary>
        /// <param name="e">The exception that occurred</param>
        public void error(Exception e)
        {
            ErrorMessageReceived?.Invoke(new APIError(-1, -1, e.Message, e));
        }

        public void verifyCompleted(bool isSuccessful, string errorText)
        {
            VerifyAPICompleted?.Invoke(isSuccessful, errorText);
        }

        public void verifyAndAuthCompleted(bool isSuccessful, string errorText)
        {
            VerifyAPICompleted?.Invoke(isSuccessful, errorText);
        }

        [Obsolete]
        public void verifyMessageAPI(string apiData)
        {
            VerifyAPIMessageReceived?.Invoke(apiData, null);
        }

        public void verifyAndAuthMessageAPI(string apiData, string xyzChallenge)
        {
            VerifyAPIMessageReceived?.Invoke(apiData, xyzChallenge);
        }
    }
}
