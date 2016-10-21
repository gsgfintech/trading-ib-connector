using Capital.GSG.FX.Trading.Executor;
using System;
using Net.Teirlinck.FX.Data.NewsBulletinData;
using log4net;
using System.Threading;

namespace Net.Teirlinck.FX.InteractiveBrokersAPI.Executor
{
    public class IBNewsBulletinProvider : INewsBulletinProvider
    {
        private static ILog logger = LogManager.GetLogger(nameof(IBNewsBulletinProvider));

        private readonly IBClient ibClient;

        private readonly CancellationToken stopRequestedCt;

        public event Action<NewsBulletin> NewBulletinReceived;

        public IBNewsBulletinProvider(IBClient ibClient, CancellationToken stopRequestedCt)
        {
            this.ibClient = ibClient;
            this.stopRequestedCt = stopRequestedCt;

            this.ibClient.ResponseManager.NewsBulletinReceived += NewsBulletinReceived;

            this.ibClient.IBConnectionEstablished += () =>
            {
                logger.Info("IB client (re)connected. Will resubcribe to news bulletin updates");

                this.ibClient.RequestManager.NewsBulletinRequestManager.RequestNewsBulletins(false);
            };
        }

        private void NewsBulletinReceived(int msgId, NewsBulletinType msgType, string message, string origExchange)
        {
            try
            {
                NewBulletinReceived?.Invoke(new NewsBulletin()
                {
                    BulletinType = msgType,
                    Id = Guid.NewGuid().ToString(),
                    Message = message?.Replace("==", ""),
                    Origin = origExchange,
                    Source = NewsBulletinSource.IB,
                    Status = NewsBulletinStatus.OPEN,
                    Timestamp = DateTimeOffset.Now
                });
            }
            catch (Exception ex)
            {
                logger.Error("Failed to process new news bulletin", ex);
            }
        }

        public void Dispose()
        {
            logger.Info($"Disposing {nameof(IBNewsBulletinProvider)}");

            ibClient.ResponseManager.NewsBulletinReceived -= NewsBulletinReceived;

            ibClient.RequestManager.NewsBulletinRequestManager.CancelNewsBulletinsRequests();
        }
    }
}
