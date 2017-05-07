using System;
using Capital.GSG.FX.Data.Core.NewsBulletinData;
using Capital.GSG.FX.Trading.Executor.Core;
using Microsoft.Extensions.Logging;
using Capital.GSG.FX.Utils.Core.Logging;

namespace Net.Teirlinck.FX.InteractiveBrokersAPI.Executor
{
    public class IBNewsBulletinProvider : INewsBulletinProvider
    {
        private readonly ILogger logger = GSGLoggerFactory.Instance.CreateLogger<IBNewsBulletinProvider>();

        private readonly IBClient ibClient;

        public event Action<NewsBulletin> NewBulletinReceived;

        public IBNewsBulletinProvider(IBClient ibClient)
        {
            this.ibClient = ibClient;

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
