using Capital.GSG.FX.Data.Core.FinancialAdvisorsData;
using Capital.GSG.FX.Monitoring.Server.Connector;
using Capital.GSG.FX.Utils.Core;
using log4net;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Net.Teirlinck.FX.InteractiveBrokersAPI.Executor
{
    internal class FinancialAdvisorTester
    {
        private static ILog logger = LogManager.GetLogger(nameof(FinancialAdvisorTester));

        public static async Task Test(BrokerClient brokerClient)
        {
            IBFinancialAdvisorProvider faProvider = brokerClient.FAProvider;
            IBPositionsExecutor positionsExecutor = (IBPositionsExecutor)brokerClient.PositionExecutor;

            await TestRequestManagedAccounts(faProvider, positionsExecutor);
            //await TestRequestFAConfigurations(faProvider);
            //await TestRequestAccountsSummary(positionsExecutor);
        }

        private static async Task TestRequestManagedAccounts(IBFinancialAdvisorProvider faProvider, IBPositionsExecutor positionsExecutor)
        {
            var result = await faProvider.RequestManagedAccountsList();

            string accounts = !result.Accounts.IsNullOrEmpty() ? string.Join(", ", result.Accounts) : "";

            logger.Info($"Success:{result.Success} | Message:{result.Message} | Accounts:{accounts}");

            await TestRequestAccountsDetails(positionsExecutor, result.Accounts);
        }

        private static async Task TestRequestFAConfigurations(IBFinancialAdvisorProvider faProvider)
        {
            var result = await faProvider.RequestFAConfiguration();

            logger.Info($"Success:{result.Success} | Message:{result.Message}");

            if (result.FAConfiguration != null && !result.FAConfiguration.AccountAliases.IsNullOrEmpty())
            {
                List<FAAllocationProfile> profiles = new List<FAAllocationProfile>();

                List<FAAllocation> allocations1 = new List<FAAllocation>();
                foreach (var account in result.FAConfiguration.AccountAliases)
                {
                    allocations1.Add(new FAAllocation()
                    {
                        Account = account.Account,
                        Amount = (new Random()).NextDouble()
                    });
                }

                profiles.Add(new FAAllocationProfile()
                {
                    Allocations = allocations1,
                    Name = $"TestProfile{(new Random()).Next(10)}",
                    Type = FAAllocationProfileType.Shares
                });

                List<FAAllocation> allocations2 = new List<FAAllocation>();
                foreach (var account in result.FAConfiguration.AccountAliases)
                {
                    allocations2.Add(new FAAllocation()
                    {
                        Account = account.Account,
                        Amount = (new Random()).NextDouble()
                    });
                }

                profiles.Add(new FAAllocationProfile()
                {
                    Allocations = allocations2,
                    Name = $"TestProfile{(new Random()).Next(10)}",
                    Type = FAAllocationProfileType.Percentages
                });

                var updateResult = await faProvider.UpdateFAAllocationProfiles(profiles);

                logger.Info($"Success:{updateResult.Success} | Message:{updateResult.Message}");
            }
        }

        private static async Task TestRequestAccountsSummary(IBPositionsExecutor positionsExecutor)
        {
            var result = await positionsExecutor.RequestAccountsSummary();

            logger.Info($"Success:{result.Success} | Message:{result.Message}");
        }

        private static async Task TestRequestAccountsDetails(IBPositionsExecutor positionsExecutor, IEnumerable<string> accounts)
        {
            var result = await positionsExecutor.RequestAccountsDetails(accounts);

            logger.Info($"Success:{result.Success} | Message:{result.Message}");

            if (!result.Accounts.IsNullOrEmpty())
            {
                BackendAccountsConnector connector = new MonitoringServerConnector(Program.MonitorClientId, Program.MonitorAppKey, Program.MonitorBackendAddress, Program.MonitorBackendAppUri).AccountsConnector;

                foreach (var account in result.Accounts.Values)
                {
                    await connector.AddOrUpdate(account);
                }
            }
        }
    }
}
