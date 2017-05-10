using Capital.GSG.FX.Data.Core.ContractData;
using Capital.GSG.FX.Utils.Core.Logging;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Net.Teirlinck.FX.InteractiveBrokersAPI.Executor
{
    public class IBContractDetailsProvider
    {
        //private readonly ILogger logger = GSGLoggerFactory.Instance.CreateLogger<IBContractDetailsProvider>();

        //private readonly IBClient ibClient;

        //public IBContractDetailsProvider(IBClient ibClient)
        //{
        //    this.ibClient = ibClient;
        //}

        //#region EnrichContractWithDetails
        //private AutoResetEvent contractDetailsReceivedEvent;
        //private bool isEnrichingContractWithDetails;
        //private object isEnrichingContractWithDetailsLocker = new object();
        //private Contract enrichedContract;

        //public async Task<(bool Success, string Message, Contract Contract)> EnrichContractWithDetails(Contract contract)
        //{
        //    if (isEnrichingContractWithDetails)
        //        return (false, "Unable to enrich contract with details: another request is already in progress. Try again later", null);

        //    SetIsEnrichingContractWithDetailsFlag(true);

        //    CancellationTokenSource cts = new CancellationTokenSource();
        //    cts.CancelAfter(TimeSpan.FromSeconds(10));

        //    return await Task.Run(() =>
        //    {
        //        try
        //        {
        //            cts.Token.ThrowIfCancellationRequested();

        //            if (contract == null)
        //                throw new ArgumentNullException(nameof(contract));

        //            enrichedContract = contract;

        //            contractDetailsReceivedEvent = new AutoResetEvent(false);

        //            ibClient.ResponseManager.ContractDetailsReceived += ContractDetailsReceived; ;

        //            ibClient.RequestManager.FinancialAdvisorsRequestManager.RequestManagedAccounts();

        //            bool received = managedAccountsListReceivedEvent.WaitOne(TimeSpan.FromSeconds(3));

        //            ibClient.ResponseManager.ManagedAccountsListReceived -= ManagedAccountsListReceived;

        //            if (received)
        //                return (true, "Enriched contract with details", enrichedContract);
        //            else
        //                return (false, "Didn't receive any contract details in the specified timeframe of 3 seconds", null);
        //        }
        //        catch (OperationCanceledException)
        //        {
        //            string err = "Not enriching contract with details: operation cancelled";
        //            logger.Error(err);
        //            return (false, err, null);
        //        }
        //        catch (ArgumentNullException ex)
        //        {
        //            string err = $"Not enriching contract with details: missing or invalid parameter {ex.ParamName}";
        //            logger.Error(err);
        //            return (false, err, null);
        //        }
        //        catch (Exception ex)
        //        {
        //            string err = "Failed to enrich contract with details";
        //            logger.Error(err, ex);
        //            return (false, $"{err}: {ex.Message}", null);
        //        }
        //        finally
        //        {
        //            SetIsEnrichingContractWithDetailsFlag(false);
        //        }
        //    }, cts.Token);
        //}

        //private ContractDetails ContractDetailsReceived(int requestId, ContractDetails details)
        //{
        //    if (details != null)
        //    {

        //    }
        //}

        //private void SetIsEnrichingContractWithDetailsFlag(bool value)
        //{
        //    if (isEnrichingContractWithDetails == value)
        //        return;

        //    lock (isEnrichingContractWithDetailsLocker)
        //    {
        //        isEnrichingContractWithDetails = value;
        //    }
        //}
        //#endregion
    }
}
