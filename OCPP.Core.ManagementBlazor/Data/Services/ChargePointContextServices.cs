using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using OCPP.Core.ManagementBlazor.Models;
using Newtonsoft.Json;
using System.Net.Http;
using Microsoft.Extensions.Logging;
using OCPP.Core.Database;

namespace OCPP.Core.ManagementBlazor.Data.Services
{
    public class ChargePointContextServices
    {
        //private readonly IDbContextFactory<Database.OCPPCoreContext> _contextFactory;
        protected IConfiguration Config { get; private set; }
        protected ILogger Logger { get; set; }
        public ChargePointContextServices(IConfiguration config, ILoggerFactory loggerFactory)
        {
            this.Config = config;
            Logger = loggerFactory.CreateLogger<ChargePointContextServices>();
        }
        public async Task<List<Database.ChargePoint>> GetChargePoints() 
        {
            using (var context = new Database.OCPPCoreContext(this.Config)) 
            {
                return await context.ChargePoints.ToListAsync();
            }
        }
        public async Task<List<ConnectorStatus>> GetConnectorStatuses()
        {
            using (var context = new Database.OCPPCoreContext(this.Config))
            {
                return await context.ConnectorStatuses.ToListAsync();
            }
        }
        public async Task<OverviewViewModel> GetOverviewViewModels() 
        {
            OverviewViewModel overviewModel = new OverviewViewModel();
            overviewModel.ChargePoints = new List<ChargePointsOverviewViewModel>();
            try
            {
                Dictionary<string, ChargePointStatus> dictOnlineStatus = new Dictionary<string, ChargePointStatus>();
                #region Load online status from OCPP server
                string serverApiUrl = this.Config.GetValue<string>("ServerApiUrl");
                string apiKeyConfig = this.Config.GetValue<string>("ApiKey");
                if (!string.IsNullOrEmpty(serverApiUrl))
                {
                    bool serverError = false;
                    try
                    {
                        ChargePointStatus[] onlineStatusList = null;

                        using (var httpClient = new HttpClient())
                        {
                            if (!serverApiUrl.EndsWith('/'))
                            {
                                serverApiUrl += "/";
                            }
                            Uri uri = new Uri(serverApiUrl);
                            uri = new Uri(uri, "Status");
                            httpClient.Timeout = new TimeSpan(0, 0, 4); // use short timeout

                            // API-Key authentication?
                            if (!string.IsNullOrWhiteSpace(apiKeyConfig))
                            {
                                httpClient.DefaultRequestHeaders.Add("X-API-Key", apiKeyConfig);
                            }
                            else
                            {
                                Logger.LogWarning("Index: No API-Key configured!");
                            }

                            HttpResponseMessage response = await httpClient.GetAsync(uri);
                            if (response.StatusCode == System.Net.HttpStatusCode.OK)
                            {
                                string jsonData = await response.Content.ReadAsStringAsync();
                                if (!string.IsNullOrEmpty(jsonData))
                                {
                                    onlineStatusList = JsonConvert.DeserializeObject<ChargePointStatus[]>(jsonData);
                                    overviewModel.ServerConnection = true;

                                    if (onlineStatusList != null)
                                    {
                                        foreach (ChargePointStatus cps in onlineStatusList)
                                        {
                                            if (!dictOnlineStatus.TryAdd(cps.Id, cps))
                                            {
                                                Logger.LogError("Index: Online charge point status (ID={0}) could not be added to dictionary", cps.Id);
                                            }
                                        }
                                    }
                                }
                                else
                                {
                                    Logger.LogError("Index: Result of status web request is empty");
                                    serverError = true;
                                }
                            }
                            else
                            {
                                Logger.LogError("Index: Result of status web request => httpStatus={0}", response.StatusCode);
                                serverError = true;
                            }
                        }

                        Logger.LogInformation("Index: Result of status web request => Length={0}", onlineStatusList?.Length);
                    }
                    catch (Exception exp)
                    {
                        Logger.LogError(exp, "Index: Error in status web request => {0}", exp.Message);
                        serverError = true;
                    }

                    if (serverError)
                    {
                        //ViewBag.ErrorMsg = _localizer["ErrorOCPPServer"];
                    }
                }
                #endregion

                using (OCPPCoreContext dbContext = new OCPPCoreContext(this.Config))
                {
                    // List of charge point status (OCPP messages) with latest transaction (if one exist)
                    List<ConnectorStatusView> connectorStatusViewList = dbContext.ConnectorStatusViews.ToList<ConnectorStatusView>();

                    // Count connectors for every charge point (=> naming scheme)
                    Dictionary<string, int> dictConnectorCount = new Dictionary<string, int>();
                    foreach (ConnectorStatusView csv in connectorStatusViewList)
                    {
                        if (dictConnectorCount.ContainsKey(csv.ChargePointId))
                        {
                            // > 1 connector
                            dictConnectorCount[csv.ChargePointId] = dictConnectorCount[csv.ChargePointId] + 1;
                        }
                        else
                        {
                            // first connector
                            dictConnectorCount.Add(csv.ChargePointId, 1);
                        }
                    }


                    // List of configured charge points
                    List<ChargePoint> dbChargePoints = dbContext.ChargePoints.ToList<ChargePoint>();
                    if (dbChargePoints != null)
                    {
                        // Iterate through all charge points in database
                        foreach (ChargePoint cp in dbChargePoints)
                        {
                            ChargePointStatus cpOnlineStatus = null;
                            dictOnlineStatus.TryGetValue(cp.ChargePointId, out cpOnlineStatus);

                            // Preference: Check for connectors status in database
                            bool foundConnectorStatus = false;
                            if (connectorStatusViewList != null)
                            {
                                foreach (ConnectorStatusView connStatus in connectorStatusViewList)
                                {
                                    if (string.Equals(cp.ChargePointId, connStatus.ChargePointId, StringComparison.InvariantCultureIgnoreCase))
                                    {
                                        foundConnectorStatus = true;

                                        ChargePointsOverviewViewModel cpovm = new ChargePointsOverviewViewModel();
                                        cpovm.ChargePointId = cp.ChargePointId;
                                        cpovm.ConnectorId = connStatus.ConnectorId;
                                        if (string.IsNullOrWhiteSpace(connStatus.ConnectorName))
                                        {
                                            // No connector name specified => use default
                                            if (dictConnectorCount.ContainsKey(cp.ChargePointId) &&
                                                dictConnectorCount[cp.ChargePointId] > 1)
                                            {
                                                // more than 1 connector => "<charge point name>:<connector no.>"
                                                cpovm.Name = $"{cp.Name}:{connStatus.ConnectorId}";
                                            }
                                            else
                                            {
                                                // only 1 connector => "<charge point name>"
                                                cpovm.Name = cp.Name;
                                            }
                                        }
                                        else
                                        {
                                            // Connector has name override name specified
                                            cpovm.Name = connStatus.ConnectorName;
                                        }
                                        cpovm.Online = cpOnlineStatus != null;
                                        cpovm.ConnectorStatus = ConnectorStatusEnum.Undefined;
                                        OnlineConnectorStatus onlineConnectorStatus = null;
                                        if (cpOnlineStatus != null &&
                                            cpOnlineStatus.OnlineConnectors != null &&
                                            cpOnlineStatus.OnlineConnectors.ContainsKey(connStatus.ConnectorId))
                                        {
                                            onlineConnectorStatus = cpOnlineStatus.OnlineConnectors[connStatus.ConnectorId];
                                            cpovm.ConnectorStatus = onlineConnectorStatus.Status;
                                            Logger.LogTrace("Index: Found online status for CP='{0}' / Connector='{1}' / Status='{2}'", cpovm.ChargePointId, cpovm.ConnectorId, cpovm.ConnectorStatus);
                                        }

                                        if (connStatus.TransactionId.HasValue)
                                        {
                                            cpovm.MeterStart = connStatus.MeterStart.Value;
                                            cpovm.MeterStop = connStatus.MeterStop;
                                            cpovm.StartTime = connStatus.StartTime;
                                            cpovm.StopTime = connStatus.StopTime;

                                            // default status: active transaction or not?
                                            cpovm.ConnectorStatus = (cpovm.StopTime.HasValue) ? ConnectorStatusEnum.Available : ConnectorStatusEnum.Occupied;
                                        }
                                        else
                                        {
                                            cpovm.MeterStart = -1;
                                            cpovm.MeterStop = -1;
                                            cpovm.StartTime = null;
                                            cpovm.StopTime = null;

                                            // default status: Available
                                            cpovm.ConnectorStatus = ConnectorStatusEnum.Available;
                                        }

                                        // Add current charge data to view model
                                        if (cpovm.ConnectorStatus == ConnectorStatusEnum.Occupied &&
                                            onlineConnectorStatus != null)
                                        {
                                            string currentCharge = string.Empty;
                                            if (onlineConnectorStatus.ChargeRateKW != null)
                                            {
                                                currentCharge = string.Format("{0:0.0}kW", onlineConnectorStatus.ChargeRateKW.Value);
                                            }
                                            if (onlineConnectorStatus.SoC != null)
                                            {
                                                if (!string.IsNullOrWhiteSpace(currentCharge)) currentCharge += " | ";
                                                currentCharge += string.Format("{0:0}%", onlineConnectorStatus.SoC.Value);
                                            }
                                            if (!string.IsNullOrWhiteSpace(currentCharge))
                                            {
                                                cpovm.CurrentChargeData = currentCharge;
                                            }
                                        }

                                        overviewModel.ChargePoints.Add(cpovm);
                                    }
                                }
                            }
                            // Fallback: assume 1 connector and show main charge point
                            if (foundConnectorStatus == false)
                            {
                                // no connector status found in DB => show configured charge point in overview
                                ChargePointsOverviewViewModel cpovm = new ChargePointsOverviewViewModel();
                                cpovm.ChargePointId = cp.ChargePointId;
                                cpovm.ConnectorId = 0;
                                cpovm.Name = cp.Name;
                                cpovm.Comment = cp.Comment;
                                cpovm.Online = cpOnlineStatus != null;
                                cpovm.ConnectorStatus = ConnectorStatusEnum.Undefined;
                                overviewModel.ChargePoints.Add(cpovm);
                            }
                        }
                    }

                    Logger.LogInformation("Index: Found {0} charge points / connectors", overviewModel.ChargePoints?.Count);
                }
            }
            catch (Exception exp)
            {
                Logger.LogError(exp, "Index: Error loading charge points from database");
                //TempData["ErrMessage"] = exp.Message;
                //return RedirectToAction("Error", new { Id = "" });
            }
            return overviewModel;
        }
        public async Task AddChargePoint(Database.ChargePoint chargePoint)
        {
            using (var context = new Database.OCPPCoreContext(this.Config))
            {
                await context.ChargePoints.AddAsync(chargePoint);
                await context.SaveChangesAsync();
            }
        }
        public async Task RemoveChargePoint(Database.ChargePoint chargePoint)
        {
            using (var context = new Database.OCPPCoreContext(this.Config))
            {
                context.ChargePoints.Remove(chargePoint);
                await context.SaveChangesAsync();
            }
        }
        public async Task<TransactionListViewModel> GetTransactionListViewModel(string Id, string ConnectorId, string ts) 
        {
            int currentConnectorId = -1;
            int.TryParse(ConnectorId, out currentConnectorId);
            TransactionListViewModel tlvm = new TransactionListViewModel();
            tlvm.CurrentChargePointId = Id;
            tlvm.CurrentConnectorId = currentConnectorId;
            tlvm.ConnectorStatuses = new List<ConnectorStatus>();
            tlvm.Transactions = new List<Transaction>();

            try
            {
                int days = 30;
                if (ts == "2")
                {
                    // 90 days
                    days = 90;
                    tlvm.Timespan = 2;
                }
                else if (ts == "3")
                {
                    // 365 days
                    days = 365;
                    tlvm.Timespan = 3;
                }
                else
                {
                    // 30 days
                    days = 30;
                    tlvm.Timespan = 1;
                }

                using (OCPPCoreContext dbContext = new OCPPCoreContext(this.Config))
                {
                    Logger.LogTrace("Transactions: Loading charge points...");
                    tlvm.ChargePoints = dbContext.ChargePoints.ToList<ChargePoint>();

                    Logger.LogTrace("Transactions: Loading charge points connectors...");
                    tlvm.ConnectorStatuses = dbContext.ConnectorStatuses.ToList<ConnectorStatus>();

                    // Count connectors for every charge point (=> naming scheme)
                    Dictionary<string, int> dictConnectorCount = new Dictionary<string, int>();
                    foreach (ConnectorStatus cs in tlvm.ConnectorStatuses)
                    {
                        if (dictConnectorCount.ContainsKey(cs.ChargePointId))
                        {
                            // > 1 connector
                            dictConnectorCount[cs.ChargePointId] = dictConnectorCount[cs.ChargePointId] + 1;
                        }
                        else
                        {
                            // first connector
                            dictConnectorCount.Add(cs.ChargePointId, 1);
                        }
                    }

                    // Dictionary mit ID+Connector => Name erstellen und View übergeben
                    // => Combobox damit füllen
                    // => Namen in Transaktionen auflösen




                    /*
                    // search selected charge point and connector
                    foreach (ConnectorStatus cs in tlvm.ConnectorStatuses)
                    {
                        if (cs.ChargePointId == Id && cs.ConnectorId == currentConnectorId)
                        {
                            tlvm.CurrentConnectorName = cs.ConnectorName;
                            if (string.IsNullOrEmpty(tlvm.CurrentConnectorName))
                            {
                                tlvm.CurrentConnectorName = $"{Id}:{cs.ConnectorId}";
                            }
                            break;
                        }
                    }
                    */


                    // load charge tags for name resolution
                    Logger.LogTrace("Transactions: Loading charge tags...");
                    List<ChargeTag> chargeTags = dbContext.ChargeTags.ToList<ChargeTag>();
                    tlvm.ChargeTags = new Dictionary<string, ChargeTag>();
                    if (chargeTags != null)
                    {
                        foreach (ChargeTag tag in chargeTags)
                        {
                            tlvm.ChargeTags.Add(tag.TagId, tag);
                        }
                    }

                    if (!string.IsNullOrEmpty(tlvm.CurrentChargePointId))
                    {
                        Logger.LogTrace("Transactions: Loading charge point transactions...");
                        tlvm.Transactions = dbContext.Transactions
                                            .Where(t => t.ChargePointId == tlvm.CurrentChargePointId &&
                                                        t.ConnectorId == tlvm.CurrentConnectorId &&
                                                        t.StartTime >= DateTime.UtcNow.AddDays(-1 * days))
                                            .OrderByDescending(t => t.TransactionId)
                                            .ToList<Transaction>();
                    }
                }
            }
            catch (Exception exp)
            {
                Logger.LogError(exp, "Transactions: Error loading charge points from database");
            }
            return tlvm;
        }
    }
}
