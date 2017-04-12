using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Net.Teirlinck.FX.InteractiveBrokersAPI.Executor
{
    public class IBNewsProvider
    {
        private readonly IBClient ibClient;
        private readonly CancellationToken stopRequestedCt;

        private Dictionary<string, string> newsProviders = null;
        private AutoResetEvent providersListReceivedEvent;

        public IBNewsProvider(IBClient ibClient, CancellationToken stopRequestedCt)
        {
            this.ibClient = ibClient;
            this.stopRequestedCt = stopRequestedCt;
        }

        public Dictionary<string, string> ListAvailableNewsProviders()
        {
            providersListReceivedEvent = new AutoResetEvent(false);

            ibClient.ResponseManager.NewsProvidersListReceived += NewsProvidersListReceived;

            ibClient.RequestManager.NewsRequestManager.RequestNewsProviders();

            providersListReceivedEvent.WaitOne();

            ibClient.ResponseManager.NewsProvidersListReceived -= NewsProvidersListReceived;

            return newsProviders;
        }

        private void NewsProvidersListReceived(Dictionary<string, string> newsProviders)
        {
            this.newsProviders = newsProviders;

            providersListReceivedEvent?.Set();
        }
    }
}
