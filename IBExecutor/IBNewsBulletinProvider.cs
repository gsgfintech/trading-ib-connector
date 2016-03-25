using Capital.GSG.FX.Trading.Executor;
using System;
using Net.Teirlinck.FX.Data.NewsBulletinData;
using log4net;

namespace Net.Teirlinck.FX.InteractiveBrokersAPI.Executor
{
    public class IBNewsBulletinProvider : INewsBulletinProvider
    {
        private static ILog logger = LogManager.GetLogger(nameof(IBNewsBulletinProvider));

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
            NewsBulletin bulletin = new NewsBulletin()
            {
                BulletinType = msgType,
                Id = msgId,
                Message = message,
                OrigExchange = origExchange,
                Timestamp = DateTime.Now
            };

            logger.Info($"Received a new news bulletin from IB: {bulletin}");

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
