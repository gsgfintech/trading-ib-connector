using Capital.GSG.FX.Data.Core.ContractData;
using Capital.GSG.FX.Data.Core.ExecutionData;
using System;
using System.Globalization;
using System.Linq;

namespace Net.Teirlinck.FX.InteractiveBrokersAPI.Extensions
{
    public static class IBExecutionExtensions
    {
        public static Execution ToExecution(this IBApi.Execution ibExecution, Cross cross)
        {
            if (ibExecution == null)
                return null;
            else
            {
                Execution apiExecution = new Execution()
                {
                    OrderId = ibExecution.OrderId,
                    ClientId = ibExecution.ClientId,
                    Cross = cross,
                    Id = ibExecution.ExecId,
                    AccountNumber = ibExecution.AcctNumber,
                    Exchange = ibExecution.Exchange,
                    Price = ibExecution.AvgPrice,
                    PermanentID = ibExecution.PermId,
                    Quantity = ibExecution.Shares,
                    ClientOrderRef = ibExecution.OrderRef
                };

                DateTimeOffset executionTime;
                if (DateTimeOffset.TryParseExact(ibExecution.Time, "yyyyMMdd  HH:mm:ss", new CultureInfo("en-US"), DateTimeStyles.AssumeLocal, out executionTime))
                    apiExecution.ExecutionTime = executionTime;

                if (ibExecution.Side == "BOT")
                    apiExecution.Side = ExecutionSide.BOUGHT;
                else if (ibExecution.Side == "SLD")
                    apiExecution.Side = ExecutionSide.SOLD;
                else
                    apiExecution.Side = ExecutionSide.UNKNOWN;

                if (!String.IsNullOrEmpty(ibExecution.OrderRef))
                {
                    string stratPart = ibExecution.OrderRef.Split('|')?.FirstOrDefault();

                    if (!String.IsNullOrEmpty(stratPart) && stratPart.Contains("Strategy:"))
                        apiExecution.Strategy = stratPart.Replace("Strategy:", "");
                }

                return apiExecution;
            }
        }
    }
}
