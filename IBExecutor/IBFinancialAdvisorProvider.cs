using log4net;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Capital.GSG.FX.Data.Core.FinancialAdvisorsData;
using static Capital.GSG.FX.Data.Core.FinancialAdvisorsData.FinancialAdvisorsDataType;
using System.Xml;
using System.IO;
using Capital.GSG.FX.Utils.Core;

namespace Net.Teirlinck.FX.InteractiveBrokersAPI.Executor
{
    public class IBFinancialAdvisorProvider
    {
        private readonly ILog logger = LogManager.GetLogger(nameof(IBFinancialAdvisorProvider));

        private IBClient IBClient { get; set; }

        private readonly string tradingAccount;
        private readonly CancellationToken stopRequestedCt;

        internal IBFinancialAdvisorProvider(IBClient ibClient, string tradingAccount, CancellationToken stopRequestedCt)
        {
            this.tradingAccount = tradingAccount;
            this.stopRequestedCt = stopRequestedCt;

            IBClient = ibClient;
        }

        #region RequestManagedAccountsList
        private AutoResetEvent managedAccountsListReceivedEvent;
        private bool isRequestingManagedAccountsList;
        private object isRequestingManagedAccountsListLocker = new object();
        private string[] managedAccountsList = null;

        public async Task<(bool Success, string Message, string[] Accounts)> RequestManagedAccountsList()
        {
            if (isRequestingManagedAccountsList)
                return (false, "Unable to request managed accounts list: another request is already in progress. Try again later", null);

            SetIsRequestingManagedAccountsListFlag(true);

            return await Task.Run(() =>
            {
                try
                {
                    stopRequestedCt.ThrowIfCancellationRequested();

                    managedAccountsListReceivedEvent = new AutoResetEvent(false);

                    IBClient.ResponseManager.ManagedAccountsListReceived += ManagedAccountsListReceived;

                    IBClient.RequestManager.FinancialAdvisorsRequestManager.RequestManagedAccounts();

                    bool received = managedAccountsListReceivedEvent.WaitOne(TimeSpan.FromSeconds(3));

                    IBClient.ResponseManager.ManagedAccountsListReceived -= ManagedAccountsListReceived;

                    if (received)
                        return (true, "Received accounts list", managedAccountsList);
                    else
                        return (false, "Didn't receive any managed account list in the specified timeframe of 3 seconds", null);
                }
                catch (OperationCanceledException)
                {
                    string err = "Not requesting managed accounts list: operation cancelled";
                    logger.Error(err);
                    return (false, err, null);
                }
                catch (Exception ex)
                {
                    string err = "Failed to request managed accounts list";
                    logger.Error(err, ex);
                    return (false, $"{err}: {ex.Message}", null);
                }
                finally
                {
                    SetIsRequestingManagedAccountsListFlag(false);
                }
            }, stopRequestedCt);
        }

        private void SetIsRequestingManagedAccountsListFlag(bool value)
        {
            lock (isRequestingManagedAccountsListLocker)
            {
                managedAccountsList = null;
                isRequestingManagedAccountsList = value;
            }
        }

        private void ManagedAccountsListReceived(string accountsStr)
        {
            if (string.IsNullOrEmpty(accountsStr))
                managedAccountsList = new string[0];
            else
                managedAccountsList = accountsStr.Split(',');

            managedAccountsListReceivedEvent?.Set();
        }
        #endregion

        #region RequestFAConfiguration
        private AutoResetEvent faConfigurationReceivedEvent;
        private bool isRequestingFAConfiguration;
        private object isRequestingFAConfigurationLocker = new object();
        private FAConfiguration faConfiguration = null;

        public async Task<(bool Success, string Message, FAConfiguration FAConfiguration)> RequestFAConfiguration()
        {
            if (isRequestingFAConfiguration)
                return (false, "Unable to request FA configurations: another request is already in progress. Try again later", null);

            SetIsRequestingFAConfigurationFlag(true);

            return await Task.Run(() =>
            {
                try
                {
                    stopRequestedCt.ThrowIfCancellationRequested();

                    faConfigurationReceivedEvent = new AutoResetEvent(false);

                    IBClient.ResponseManager.FinancialAdvisorsDataReceived += FinancialAdvisorsDataReceived;

                    // 1. ACCOUNT_ALIASES
                    IBClient.RequestManager.FinancialAdvisorsRequestManager.RequestFinancialAdvisorConfiguration(ACCOUNT_ALIASES);

                    bool received = faConfigurationReceivedEvent.WaitOne(TimeSpan.FromSeconds(3));

                    // 2. GROUPS
                    faConfigurationReceivedEvent.Reset();

                    IBClient.RequestManager.FinancialAdvisorsRequestManager.RequestFinancialAdvisorConfiguration(GROUPS);

                    received = received || faConfigurationReceivedEvent.WaitOne(TimeSpan.FromSeconds(3));

                    // 3. PROFILE
                    faConfigurationReceivedEvent.Reset();

                    IBClient.RequestManager.FinancialAdvisorsRequestManager.RequestFinancialAdvisorConfiguration(PROFILE);

                    received = received || faConfigurationReceivedEvent.WaitOne(TimeSpan.FromSeconds(3));

                    IBClient.ResponseManager.FinancialAdvisorsDataReceived -= FinancialAdvisorsDataReceived;

                    if (received)
                        return (true, "Received FA configurations", faConfiguration);
                    else
                        return (false, "Didn't receive FA configuration in the specified timeframe of 3 seconds", null);
                }
                catch (OperationCanceledException)
                {
                    string err = "Not requesting FA configurations: operation cancelled";
                    logger.Error(err);
                    return (false, err, null);
                }
                catch (Exception ex)
                {
                    string err = "Failed to request FA configurations";
                    logger.Error(err, ex);
                    return (false, $"{err}: {ex.Message}", null);
                }
                finally
                {
                    SetIsRequestingFAConfigurationFlag(false);
                }
            }, stopRequestedCt);
        }

        private void FinancialAdvisorsDataReceived(FinancialAdvisorsDataType faDataType, string faXmlData)
        {
            if (faConfiguration == null)
                faConfiguration = new FAConfiguration() { MasterAccount = tradingAccount };

            switch (faDataType)
            {
                case GROUPS:
                    faConfiguration.Groups = ParseFAGroups(faXmlData);
                    break;
                case PROFILE:
                    faConfiguration.AllocationProfiles = ParseFAAllocationProfiles(faXmlData);
                    break;
                case ACCOUNT_ALIASES:
                    faConfiguration.AccountAliases = ParseFAAccountAliases(faXmlData);
                    break;
                default:
                    break;
            }

            faConfigurationReceivedEvent?.Set();
        }

        private void SetIsRequestingFAConfigurationFlag(bool value)
        {
            lock (isRequestingFAConfigurationLocker)
            {
                faConfiguration = null;
                isRequestingFAConfiguration = value;
            }
        }

        private List<FAGroup> ParseFAGroups(string xml)
        {
            using (XmlReader reader = XmlReader.Create(new StringReader(xml)))
            {
                List<FAGroup> groups = new List<FAGroup>();

                while (reader.ReadToFollowing("Group"))
                {
                    using (XmlReader groupReader = reader.ReadSubtree())
                    {
                        FAGroup group = new FAGroup();

                        if (groupReader.ReadToFollowing("name"))
                            group.Name = reader.ReadElementContentAsString();

                        if (groupReader.ReadToFollowing("ListOfAccts"))
                        {
                            using (XmlReader accountsReader = groupReader.ReadSubtree())
                            {
                                List<string> accounts = new List<string>();

                                while (accountsReader.ReadToFollowing("String"))
                                    accounts.Add(accountsReader.ReadElementContentAsString());

                                group.Accounts = accounts;
                            }
                        }

                        FAGroupMethod defaultMethod;
                        if (groupReader.ReadToFollowing("defaultMethod") && Enum.TryParse(groupReader.ReadElementContentAsString(), out defaultMethod))
                            group.DefaultMethod = defaultMethod;

                        groups.Add(group);
                    }
                }

                return groups;
            }
        }

        private List<FAAllocationProfile> ParseFAAllocationProfiles(string xml)
        {
            using (XmlReader reader = XmlReader.Create(new StringReader(xml)))
            {
                List<FAAllocationProfile> profiles = new List<FAAllocationProfile>();

                while (reader.ReadToFollowing("AllocationProfile"))
                {
                    using (XmlReader profileReader = reader.ReadSubtree())
                    {
                        FAAllocationProfile profile = new FAAllocationProfile();

                        if (profileReader.ReadToFollowing("name"))
                            profile.Name = reader.ReadElementContentAsString();

                        if (profileReader.ReadToFollowing("type"))
                            profile.Type = (FAAllocationProfileType)reader.ReadElementContentAsInt();

                        if (profileReader.ReadToFollowing("ListOfAllocations"))
                        {
                            using (XmlReader allocationsReader = profileReader.ReadSubtree())
                            {
                                List<FAAllocation> allocations = new List<FAAllocation>();

                                while (allocationsReader.ReadToFollowing("Allocation"))
                                {
                                    using (XmlReader allocationReader = allocationsReader.ReadSubtree())
                                    {
                                        FAAllocation allocation = new FAAllocation();

                                        if (allocationReader.ReadToFollowing("acct"))
                                            allocation.Account = allocationReader.ReadElementContentAsString();

                                        if (allocationReader.ReadToFollowing("amount"))
                                            allocation.Amount = allocationReader.ReadElementContentAsDouble();

                                        allocations.Add(allocation);
                                    }
                                }

                                profile.Allocations = allocations;
                            }
                        }

                        profiles.Add(profile);
                    }
                }

                return profiles;
            }
        }

        private List<FAAccountAlias> ParseFAAccountAliases(string xml)
        {
            using (XmlReader reader = XmlReader.Create(new StringReader(xml)))
            {
                List<FAAccountAlias> aliases = new List<FAAccountAlias>();

                while (reader.ReadToFollowing("AccountAlias"))
                {
                    using (XmlReader aliasReader = reader.ReadSubtree())
                    {
                        FAAccountAlias alias = new FAAccountAlias();

                        if (aliasReader.ReadToFollowing("account"))
                            alias.Account = reader.ReadElementContentAsString();

                        if (aliasReader.ReadToFollowing("alias"))
                            alias.Alias = reader.ReadElementContentAsString();

                        aliases.Add(alias);
                    }
                }

                return aliases;
            }
        }
        #endregion

        #region UpdateFAAllocationProfiles
        private AutoResetEvent faAllocationProfilesUpdateAckedEvent;
        private bool isUpdatingFAAllocationProfiles;
        private object isUpdatingFAAllocationProfilesLocker = new object();

        public async Task<(bool Success, string Message)> UpdateFAAllocationProfiles(List<FAAllocationProfile> updatedProfiles)
        {
            if (updatedProfiles.IsNullOrEmpty())
                return (false, $"Unable to update FA allocation profiles: {nameof(updatedProfiles)} is null or empty");

            if (isUpdatingFAAllocationProfiles)
                return (false, "Unable to update FA allocation profiles: another request is already in progress. Try again later");

            SetIsUpdatingFAAllocationProfiles(true);

            return await Task.Run(() =>
            {
                try
                {
                    stopRequestedCt.ThrowIfCancellationRequested();

                    faAllocationProfilesUpdateAckedEvent = new AutoResetEvent(false);

                    IBClient.ResponseManager.FinancialAdvisorsDataReceived += FAllocationProfilesUpdateAcked;

                    string xml = SerializeFAAllocationProfiles(updatedProfiles);

                    IBClient.RequestManager.FinancialAdvisorsRequestManager.ReplaceFinancialAdvisorConfiguration(PROFILE, xml);

                    bool acked = faAllocationProfilesUpdateAckedEvent.WaitOne(TimeSpan.FromSeconds(3));

                    IBClient.ResponseManager.FinancialAdvisorsDataReceived -= FAllocationProfilesUpdateAcked;

                    if (acked)
                        return (true, "Updated FA allocation profiles");
                    else
                        return (false, "Didn't receive confirmation of allocation profiles update in the specified timeframe of 3 seconds");
                }
                catch (OperationCanceledException)
                {
                    string err = "Not requesting FA allocation profiles";
                    logger.Error(err);
                    return (false, err);
                }
                catch (Exception ex)
                {
                    string err = "Failed to update FA allocation profiles";
                    logger.Error(err, ex);
                    return (false, $"{err}: {ex.Message}");
                }
                finally
                {
                    SetIsUpdatingFAAllocationProfiles(false);
                }
            }, stopRequestedCt);
        }

        private void FAllocationProfilesUpdateAcked(FinancialAdvisorsDataType faDataType, string faXmlData)
        {
            faAllocationProfilesUpdateAckedEvent?.Set();
        }

        private void SetIsUpdatingFAAllocationProfiles(bool value)
        {
            lock (isUpdatingFAAllocationProfilesLocker)
            {
                isUpdatingFAAllocationProfiles = value;
            }
        }

        private string SerializeFAAllocationProfiles(List<FAAllocationProfile> profiles)
        {
            StringBuilder output = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");

            output.Append("<ListOfAllocationProfiles>");

            foreach (var profile in profiles)
            {
                output.Append("<AllocationProfile>");

                output.Append($"<name>{profile.Name}</name>");
                output.Append($"<type>{(int)profile.Type}</type>");

                output.Append("<ListOfAllocations varName=\"listOfAllocations\">");

                foreach (var allocation in profile.Allocations)
                {
                    output.Append("<Allocation>");
                    output.Append($"<acct>{allocation.Account}</acct>");
                    output.Append($"<amount>{allocation.Amount:N1}</amount>");
                    output.Append("</Allocation>");
                }

                output.Append("</ListOfAllocations>");

                output.Append("</AllocationProfile>");
            }

            output.Append("</ListOfAllocationProfiles>");

            return output.ToString();
        }
        #endregion
    }
}
