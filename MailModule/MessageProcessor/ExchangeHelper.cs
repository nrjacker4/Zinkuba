﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text.RegularExpressions;
using log4net;
using Microsoft.Exchange.WebServices.Data;
using Zinkuba.MailModule.API;
using Zinkuba.MailModule.MessageDescriptor;

namespace Zinkuba.MailModule.MessageProcessor
{
    internal class ExchangeHelper
    {
        private static readonly ILog Logger = LogManager.GetLogger(typeof(ExchangeHelper));
        internal static ExtendedPropertyDefinition MsgPropertyContentType = new ExtendedPropertyDefinition(DefaultExtendedPropertySet.InternetHeaders, "Content-Type", MapiPropertyType.String);
        internal static ExtendedPropertyDefinition PidTagFollowupIcon = new ExtendedPropertyDefinition(0x1095, MapiPropertyType.Integer);
        internal static ExtendedPropertyDefinition PidTagFlagStatus = new ExtendedPropertyDefinition(0x1090, MapiPropertyType.Integer);
        internal static ExtendedPropertyDefinition MsgPropertyDateTimeSent = new ExtendedPropertyDefinition(0x0039, MapiPropertyType.SystemTime);
        internal static ExtendedPropertyDefinition MsgPropertyDateTimeReceived = new ExtendedPropertyDefinition(0x0e06, MapiPropertyType.SystemTime);
        internal static ExtendedPropertyDefinition MsgFlagRead = new ExtendedPropertyDefinition(3591, MapiPropertyType.Integer);

        internal static readonly ExchangeVersion[] ExchangeVersions = { ExchangeVersion.Exchange2013, ExchangeVersion.Exchange2010_SP2, ExchangeVersion.Exchange2010_SP1, ExchangeVersion.Exchange2010, ExchangeVersion.Exchange2007_SP1 };

        internal static ExchangeService ExchangeConnect(String hostname, String username, String password)
        {
            ServicePointManager.ServerCertificateValidationCallback = CertificateValidationCallBack;
            int attempt = 0;
            ExchangeService exchangeService = null;
            do
            {
                try
                {
                    exchangeService = new ExchangeService(ExchangeHelper.ExchangeVersions[attempt])
                    {
                        Credentials = new WebCredentials(username, password),
                        Url = new Uri("https://" + hostname + "/EWS/Exchange.asmx"),
                        Timeout = 30 * 60 * 1000, // 30 mins
                    };
                    Logger.Debug("Binding to exchange server " + exchangeService.Url + " as " + username + ", version " +
                                 ExchangeHelper.ExchangeVersions[attempt]);
                    Folder.Bind(exchangeService, WellKnownFolderName.MsgFolderRoot);
                }
                catch (ServiceVersionException e)
                {
                    Logger.Warn("Failed to bind as version " + ExchangeHelper.ExchangeVersions[attempt]);
                    exchangeService = null;
                    attempt++;
                }
                catch (Exception e)
                {
                    Logger.Error("Failed to bind to exchange server", e);
                    if (e.Message.Contains("Unauthorized"))
                    {
                        throw new MessageProcessorException(e.Message) { Status = MessageProcessorStatus.AuthFailure };
                    }
                    throw new MessageProcessorException(e.Message) { Status = MessageProcessorStatus.ConnectionError };
                }
            } while (exchangeService == null && attempt < ExchangeHelper.ExchangeVersions.Count());
            if (exchangeService == null)
            {
                throw new MessageProcessorException("Failed to connect to " + hostname + " with username " + username)
                {
                    Status = MessageProcessorStatus.ConnectionError
                };
            }
            return exchangeService;
        }

        public static FlagIcon ConvertFlagIcon(int flag)
        {
            switch (flag)
            {
                case 1: return FlagIcon.Outlook2003Purple;
                case 2: return FlagIcon.Outlook2003Orange;
                case 3: return FlagIcon.Outlook2003Green;
                case 4: return FlagIcon.Outlook2003Yellow;
                case 5: return FlagIcon.Outlook2003Blue;
                default: return FlagIcon.Outlook2003Red;
            }
        }

        public static int ConvertFlagIcon(FlagIcon flag)
        {
            switch (flag)
            {
                case FlagIcon.Outlook2003Purple: return 1;
                case FlagIcon.Outlook2003Orange: return 2;
                case FlagIcon.Outlook2003Green: return 3;
                case FlagIcon.Outlook2003Yellow: return 4;
                case FlagIcon.Outlook2003Blue: return 5;
                default: return 6;
            }
        }

        public static ItemFlagStatus ConvertFlagStatus(FollowUpFlagStatus status)
        {
            switch (status)
            {
                case FollowUpFlagStatus.Complete: return ItemFlagStatus.Complete;
                case FollowUpFlagStatus.Flagged: return ItemFlagStatus.Flagged;
                default: return ItemFlagStatus.NotFlagged;
            }
        }

        public static FollowUpFlagStatus ConvertFlagStatus(ItemFlagStatus status)
        {
            switch (status)
            {
                case ItemFlagStatus.Complete: return FollowUpFlagStatus.Complete;
                case ItemFlagStatus.Flagged: return FollowUpFlagStatus.Flagged;
                default: return FollowUpFlagStatus.NotFlagged;
            }
        }

        private static bool CertificateValidationCallBack(
            object sender,
            System.Security.Cryptography.X509Certificates.X509Certificate certificate,
            System.Security.Cryptography.X509Certificates.X509Chain chain,
            System.Net.Security.SslPolicyErrors sslPolicyErrors)
        {
            // If the certificate is a valid, signed certificate, return true.
            if (sslPolicyErrors == System.Net.Security.SslPolicyErrors.None)
            {
                return true;
            }

            // If there are errors in the certificate chain, look at each error to determine the cause.
            if ((sslPolicyErrors & System.Net.Security.SslPolicyErrors.RemoteCertificateChainErrors) != 0)
            {
                if (chain != null && chain.ChainStatus != null)
                {
                    foreach (System.Security.Cryptography.X509Certificates.X509ChainStatus status in chain.ChainStatus)
                    {
                        if ((certificate.Subject == certificate.Issuer) &&
                            (status.Status == System.Security.Cryptography.X509Certificates.X509ChainStatusFlags.UntrustedRoot))
                        {
                            // Self-signed certificates with an untrusted root are valid. 
                            //Logger.Warn("Self signed certificate, continuing regardless.");
                            continue;
                        }
                        else if (status.Status == System.Security.Cryptography.X509Certificates.X509ChainStatusFlags.NotTimeValid)
                        {
                            // expired, we don't mind
                            //Logger.Warn("Certificate has expired, continuing regardless.");
                            continue;
                        }
                        else if (status.Status == System.Security.Cryptography.X509Certificates.X509ChainStatusFlags.PartialChain)
                        {
                            // chain has an invalid or inaccessible root cert, we don't mind this either (badly configured local exchanges)
                            //Logger.Warn("Certificate chain is partial, continuing regardless.");
                            continue;
                        }
                        else
                        {
                            if (status.Status !=
                                System.Security.Cryptography.X509Certificates.X509ChainStatusFlags.NoError)
                            {
                                // If there are any other errors in the certificate chain, the certificate is invalid,
                                // so the method returns false.
                                return false;
                            }
                        }
                    }
                }

                // When processing reaches this line, the only errors in the certificate chain are 
                // untrusted root errors for self-signed certificates. These certificates are valid
                // for default Exchange server installations, so return true.
                return true;
            }
            else if ((sslPolicyErrors & System.Net.Security.SslPolicyErrors.RemoteCertificateNameMismatch) != 0)
            {
                // Certificate name is not correct, we don't care
                return true;
            }
            else
            {
                // In all other cases, return false.
                return false;
            }
        }
        /*
        public static bool SetProperty(EmailMessage message, PropertyDefinition propertyDefinition, object value)
        {
            if (message == null)
                return false;
            // get value of PropertyBag property — that is wrapper
            // over dictionary of inner message’s properties
            var members = message.GetType().FindMembers(MemberTypes.Property, BindingFlags.NonPublic | BindingFlags.Instance, PartialName, "PropertyBag");
            if (members.Length < 1)
                return false;

            var propertyInfo = members[0] as PropertyInfo;
            if (propertyInfo == null)
                return false;

            var bag = propertyInfo.GetValue(message, null);
            members = bag.GetType().FindMembers(MemberTypes.Property, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance, PartialName, "Properties");

            if (members.Length < 1)
                return false;

            // get dictionary of properties values
            var properties = ((PropertyInfo)members[0]).GetMethod.Invoke(bag, null);
            var dictionary = properties as Dictionary<PropertyDefinition, object>;
            if (dictionary == null)
                return false;
            dictionary[propertyDefinition] = value;

            return true;
        }
        */

        // Get a summary of all the folders
        public static void GetFolderSummary(ExchangeService service, List<ExchangeFolder> folderStore, DateTime startDate, DateTime endDate, bool purgeIgnored = true)
        {
            SearchFilter.SearchFilterCollection filter = new SearchFilter.SearchFilterCollection();
            filter.LogicalOperator = LogicalOperator.And;
            Logger.Debug("Getting mails from " + startDate + " to " + endDate);
            filter.Add(new SearchFilter.IsGreaterThanOrEqualTo(ItemSchema.DateTimeReceived, startDate));
            filter.Add(new SearchFilter.IsLessThanOrEqualTo(ItemSchema.DateTimeReceived, endDate));
            var view = new ItemView(20, 0, OffsetBasePoint.Beginning) { PropertySet = PropertySet.IdOnly };
            var ignoredFolders = new List<ExchangeFolder>();
            foreach (var exchangeFolder in folderStore)
            {
                var destinationFolder = FolderMapping.ApplyMappings(exchangeFolder.FolderPath, MailProvider.Exchange);
                if (!String.IsNullOrWhiteSpace(destinationFolder))
                {
                    exchangeFolder.MappedDestination = destinationFolder;
                    var findResults = service.FindItems(exchangeFolder.FolderId, filter, view);
                    Logger.Debug(exchangeFolder.FolderPath + " => " + exchangeFolder.MappedDestination + ", " +
                                 findResults.TotalCount + " messages.");
                    exchangeFolder.MessageCount = findResults.TotalCount;
                }
                else
                {
                    ignoredFolders.Add(exchangeFolder);
                }
            }
            if (purgeIgnored)
            {
                foreach (var exchangeFolder in ignoredFolders)
                {
                    folderStore.Remove(exchangeFolder);
                }
            }
        }

        public static void GetAllSubFolders(ExchangeService service, ExchangeFolder currentFolder, List<ExchangeFolder> folderStore, bool skipEmpty = true)
        {
            Logger.Debug("Looking for sub folders of '" + currentFolder.FolderPath + "'");
            var results = service.FindFolders(currentFolder.Folder.Id, new FolderView(int.MaxValue));
            foreach (var folder in results)
            {
                String folderPath = (String.IsNullOrEmpty(currentFolder.FolderPath)
                    ? ""
                    : currentFolder.FolderPath + @"\") + folder.DisplayName;
                if (currentFolder.IsPublicFolder) folderPath = Regex.Replace(folderPath, @"^Global Public Folder Root\\", @"");
                if (skipEmpty && folder.TotalCount == 0 && folder.ChildFolderCount == 0)
                {
                    Logger.Debug("Skipping folder " + folderPath + ", no messages, no subfolders.");
                    continue;
                }
                Logger.Debug("Found folder " + folderPath + ", " + folder.TotalCount + " messages in total.");
                var exchangeFolder = new ExchangeFolder()
                {
                    Folder = folder,
                    FolderPath = folderPath,
                    MessageCount = folder.TotalCount,
                    FolderId = folder.Id,
                    IsPublicFolder = currentFolder.IsPublicFolder
                };
                // only add it to the list of folders if it isn't the public folder root
                if(!currentFolder.IsPublicFolder || !String.Equals(folder.DisplayName,"Global Public Folder Root"))
                    folderStore.Add(exchangeFolder);
                if (exchangeFolder.Folder.ChildFolderCount > 0)
                {
                    GetAllSubFolders(service, exchangeFolder, folderStore, skipEmpty);
                }
            }
        }

    }


}