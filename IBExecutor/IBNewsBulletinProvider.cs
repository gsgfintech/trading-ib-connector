using Capital.GSG.FX.Trading.Executor;
using System;
using Net.Teirlinck.FX.Data.NewsBulletinData;
using log4net;
using Capital.GSG.FX.AzureTableConnector;
using System.Threading;

namespace Net.Teirlinck.FX.InteractiveBrokersAPI.Executor
{
    public class IBNewsBulletinProvider : INewsBulletinProvider
    {
        private static ILog logger = LogManager.GetLogger(nameof(IBNewsBulletinProvider));

        private readonly IBClient ibClient;
        private readonly AzureTableClient azureTableClient;

        private readonly CancellationToken stopRequestedCt;

        public event Action<NewsBulletin> NewBulletinReceived;

        public IBNewsBulletinProvider(IBClient ibClient, AzureTableClient azureTableClient, CancellationToken stopRequestedCt)
        {
            this.ibClient = ibClient;
            this.stopRequestedCt = stopRequestedCt;
            this.azureTableClient = azureTableClient;

            this.ibClient.ResponseManager.NewsBulletinReceived += NewsBulletinReceived;

            this.ibClient.IBConnectionEstablished += () =>
            {
                logger.Info("IB client (re)connected. Will resubcribe to news bulletin updates");

                this.ibClient.RequestManager.NewsBulletinRequestManager.RequestNewsBulletins(false);
            };
        }

        private async void NewsBulletinReceived(int msgId, NewsBulletinType msgType, string message, string origExchange)
        {
            NewsBulletin bulletin = new NewsBulletin()
            {
                BulletinType = msgType,
                Id = msgId.ToString(),
                Message = message,
                Origin = origExchange,
                Source = NewsBulletinSource.IB,
                Status = NewsBulletinStatus.OPEN,
                Timestamp = DateTimeOffset.Now
            };

            logger.Info($"Received a new news bulletin from IB: {bulletin}. Will save it in database and notify monitoring interface");

            await azureTableClient?.NewsBulletinActioner.AddOrUpdate(bulletin, stopRequestedCt);

            NewBulletinReceived?.Invoke(bulletin);
        }

        public void Dispose()
        {
            logger.Info($"Disposing {nameof(IBNewsBulletinProvider)}");

            ibClient.ResponseManager.NewsBulletinReceived -= NewsBulletinReceived;

            ibClient.RequestManager.NewsBulletinRequestManager.CancelNewsBulletinsRequests();
        }
    }
}
