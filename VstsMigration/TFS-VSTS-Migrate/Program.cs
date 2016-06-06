/* Copyright (c) Microsoft Corporation. All Rights Reserved.
 * MIT License
 * Permission is hereby granted, free of charge, to any person obtaining
 * a copy of this software and associated documentation files (the Software), 
 * to deal in the Software without restriction, including without limitation 
 * the rights to use, copy, modify, merge, publish, distribute, sublicense, 
 * and/or sell copies of the Software, and to permit persons to whom the Software 
 * is furnished to do so, subject to the following conditions:

 * The above copyright notice and this permission notice shall be included 
 * in all copies or substantial portions of the Software.
 * 
 * THE SOFTWARE IS PROVIDED *AS IS*, WITHOUT WARRANTY OF ANY KIND, 
 * EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO 
 * THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A 
 * PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL 
 * THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, 
 * DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, 
 * TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH 
 * THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
 */

using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Xml;
using Microsoft.TeamFoundation.Client;
using Microsoft.TeamFoundation.Framework.Client;
using Microsoft.TeamFoundation.Framework.Common;
using Microsoft.TeamFoundation.Server;
using History= Microsoft.TeamFoundation.WorkItemTracking.Client;

namespace TFS_VSTS_Migrate
{
    class Program
    {
        // Query ID: Get the Query ID based on the Query Name
        // The simplest way to get this is open the Developer Tools in the browser,
        // hit the queries page and refresh
        // Search for the request ending with "queries". 
        // Find the Query Id based on query name from the payload.

        private static DataTable _areaPaths;
        private static DataTable _iterationPaths;

        private static string _tfsCollectionUri;
        private static string _tfsProjectName;
        private static Guid _queryId;
        private static string _queryName;

        private static string _vstsCollectionUri;
        private static string _vstsProjectName;
        private static string _vstsAadDomainName = "@contoso.com";

        private static string _accountName;
        private static string _accountAlternateCredPassword;

        private static TfsTeamProjectCollection _tfsTeamProjectCollection;
        private static History.WorkItemStore _tfsWorkItemStore;
        private static ICommonStructureService _tfsCss;
        private static ProjectInfo[] _tfsProjInfo;
        private static string _tfsProjectNameToVerify;
        private static IIdentityManagementService _tfsIdentityManagementService;

        private static IIdentityManagementService _vstsIdentityManagementService;
        private static TfsTeamProjectCollection _vstsTeamProjectCollection;
        private static History.WorkItemStore _vstsWorkItemStore;
        private static ICommonStructureService _vstsCss;

        private static bool _syncAreaIterationPaths;
        private static bool _migrateWorkItems;

        public static void Main(string[] args)
        {
            InitializeLog();

            if (!ProcessInputs(args)) return;
            LogMessage("Connecting to TFS..");
            InitializeTfs();

            LogMessage("Connecting to VSTS..");
            InitializeVsts();

            if (_syncAreaIterationPaths)
            {
                LogMessage("Sync area and iterations paths starting..");
                ProcessAreaIterationPaths();
                LogMessage("Sync area and iterations paths complete..");
            }
            if (_migrateWorkItems)
            {
                // Per work item calls
                LogMessage("Migrate work items starting..");
                ExportWorkItemsFromTfs2Vsts();
                LogMessage("Migrate work items complete..");
            }
            while (true)
            {
                var keyInput = Console.ReadKey();
                if (keyInput.KeyChar == 'Q' || keyInput.KeyChar == 'q')
                {
                    break;
                }
            }
            LogMessage("Exiting..");
        }

        private static void InitializeLog()
        {
            Trace.Listeners.Clear();

            var logPath = Path.Combine(Path.GetTempPath(), AppDomain.CurrentDomain.FriendlyName + Path.GetRandomFileName() +".log");
            TextWriterTraceListener twtl = new TextWriterTraceListener(logPath);
            twtl.Name = "TextLogger";
            twtl.TraceOutputOptions = TraceOptions.ThreadId | TraceOptions.DateTime;

            ConsoleTraceListener ctl = new ConsoleTraceListener(false);
            ctl.TraceOutputOptions = TraceOptions.DateTime;

            Trace.Listeners.Add(twtl);
            Trace.Listeners.Add(ctl);
            Trace.AutoFlush = true;

            Trace.WriteLine("The first line to be in the logfile and on the console.");
        }

        private static bool ProcessInputs(string[] args)
        {
            if (args.Length <= 1 || (args[0].Equals("/?") || args[0].Equals("--help")))
            {
                LogMessage("Usage:");
                LogMessage(
                    "Tfs-Vsts-Migrate SOURCE_TFS_COLLECTION_URL DESTINATION_VSTS_COLLECTION_URL QUERY_ID QUERY_NAME SETUP_MIGRATION_FLAG MIGRATE_WORK_ITEMS_FLAG SOURCE_TEAMPROJECT_NAME DESTINATION_TEAMPROJECT_NAME DESTINATION_ALTERNATE_CREDENTIALS_ID DESTINATION_ALTERNATE_CREDENTIALS_PASSWORD");
                LogMessage("");
                LogMessage("NOTE: Be careful when setting flag SETUP_MIGRATION_FLAG to true. This will CREATE area paths, iteration paths using the user credentials.");
                LogMessage("NOTE: Be careful when setting flag MIGRATE_WORK_ITEMS_FLAG to true. This will CREATE work items using the user credentials.");
                LogMessage("");
                LogMessage(
                    "Example: Tfs-Vsts-Migrate.exe \"http://fabrikam:8080/tfs/Contoso_Collection\" \"https://fabrikam-vsts.visualstudio.com/DefaultCollection\" \"c24845e9-b5dh-4a95-cl18-8224dadabf3\" \"Source Server Query Name\" false true Contoso-Project Contoso dummyuser dummyPassword");
                return false;
            }

            _tfsCollectionUri = args[0];
            _vstsCollectionUri = args[1];
            _queryId = Guid.Parse(args[2]);
            _queryName = args[3];
            _syncAreaIterationPaths = bool.Parse(args[4]);
            _migrateWorkItems = bool.Parse(args[5]);
            _tfsProjectName = args[6];
            _vstsProjectName = args[7];
            _accountName = args[8];
            _accountAlternateCredPassword = args[9];

            return true;
        }

        private static void InitializeTfs()
        {
            _tfsTeamProjectCollection = new TfsTeamProjectCollection(new Uri(_tfsCollectionUri));
            _tfsWorkItemStore = (History.WorkItemStore) _tfsTeamProjectCollection.GetService(typeof (History.WorkItemStore));
            _tfsCss = (ICommonStructureService) _tfsTeamProjectCollection.GetService(typeof (ICommonStructureService));
            _tfsProjInfo = _tfsCss.ListProjects();
            _tfsProjectNameToVerify = _tfsProjInfo[0].Name;
            _tfsIdentityManagementService = _tfsTeamProjectCollection.GetService<IIdentityManagementService>();
        }

        private static void InitializeVsts()
        {
            var netCred = new NetworkCredential(_accountName, _accountAlternateCredPassword);
            var basicCred = new BasicAuthCredential(netCred);

            var clientCredentials = new TfsClientCredentials(basicCred) { AllowInteractive = false };
            _vstsTeamProjectCollection = new TfsTeamProjectCollection(new Uri(_vstsCollectionUri), clientCredentials);
            if (_vstsTeamProjectCollection != null)
            {
                _vstsTeamProjectCollection.Authenticate();
            }
            _vstsCss = (ICommonStructureService)_vstsTeamProjectCollection.GetService(typeof(ICommonStructureService));
            _vstsWorkItemStore = (History.WorkItemStore)_vstsTeamProjectCollection.GetService(typeof(History.WorkItemStore));
            _vstsIdentityManagementService = _vstsTeamProjectCollection.GetService<IIdentityManagementService>();
        }

        private static void ProcessAreaIterationPaths()
        {
            LogMessage("--Get all iterations in progress..");
            _iterationPaths = GetAllIterations();
            _iterationPaths = RemoveTokens(_iterationPaths, "Iteration\\");

            LogMessage("--Create all iterations in progress..");
            CreateAllIterationPaths(_iterationPaths);
            LogMessage("--All iterations complete..");

            LogMessage("--Get all area paths in progress..");
            _areaPaths = GetAllAreaPaths();
            _areaPaths = RemoveTokens(_areaPaths, _tfsProjectName);
            LogMessage("--Create all area paths in progress..");
            CreateAllAreaPaths(_areaPaths);
            LogMessage("--Create all area paths complete..");
        }

        private static void CreateAllIterationPaths(DataTable dataTable)
        {
            foreach (var row in dataTable.Rows)
            {
                var dataRow = row as DataRow;
                AddCssPathNode(dataRow, "Iteration");
            }
        }

        private static DataTable RemoveTokens(DataTable dataTable, string tokenName)
        {
            foreach (var row in dataTable.Rows)
            {
                var dataRow = row as DataRow;
                dataRow["Path"] = dataRow["Path"].ToString().Replace(tokenName, "");
            }
            return dataTable;
        }

        private static void CreateAllAreaPaths(DataTable dataTable)
        {
            foreach (var row in dataTable.Rows)
            {
                var dataRow = row as DataRow;
                AddCssPathNode(dataRow, "area");
            }
        }

        // Update work Items from TFS to VSO
        private static void ExportWorkItemsFromTfs2Vsts()
        {
            // Get All Fields Data.
            var tfsWorkitemFields = GetWorkItemData();

            var wiTypes = _vstsWorkItemStore.Projects[_vstsProjectName].WorkItemTypes;
            var hyperlinkService = _tfsTeamProjectCollection.GetService<TswaClientHyperlinkService>();

            var workItemCount = 0;
            var totalWorkItems = tfsWorkitemFields.Rows.Count;
            foreach (DataRow dr in tfsWorkitemFields.Rows)
            {
                var workItemId = (int)dr["ID"];
                LogMessage("=================================================================================================");
                LogMessage(string.Format("=========== Processing work item: *** {0} of {1} *** ===========", ++workItemCount, totalWorkItems));
                LogMessage(string.Format("=========== Processing work item: {0} ===========", workItemId));
                var srcWiType = dr["WorkItemType"].ToString();
                if (srcWiType == "Workitem")
                {
                    var bugOrTask = dr["RandomField1"].ToString();
                    if (bugOrTask == "Task")
                    {
                        srcWiType = "Task";
                    }
                    if (bugOrTask == "Bug")
                    {
                        srcWiType = "Bug";
                    }
                    if (bugOrTask == "User Story")
                    {
                        srcWiType = "User Story";
                    }
                }
                var wiType = wiTypes[srcWiType];
                var newWi = new History.WorkItem(wiType);

                var saveCount = 0;
                newWi.Title = dr["Title"].ToString();
                newWi.AreaPath = dr["AreaPath"].ToString().Replace(_tfsProjectName, _vstsProjectName);
                newWi.IterationPath = dr["IterationPath"].ToString().Replace(_tfsProjectName, _vstsProjectName);
                newWi.Description = dr["Description"].ToString();

                SetDecimalWorkItemField(dr, newWi, workItemId, "Priority", 2, "Priority");

                SetWorkItemStringValue(newWi, dr, workItemId, "Random Field 4 name in VSTS", "RandomField4" );
                SetWorkItemStringValue(newWi, dr, workItemId, "Original Created By", "CreatedBy");
                SetWorkItemStringValue(newWi, dr, workItemId, "Automation status");
                SetWorkItemStringValue(newWi, dr, workItemId, "Steps");
                SetWorkItemStringValue(newWi, dr, workItemId, "RandomField2");
                SetWorkItemStringValue(newWi, dr, workItemId, "RandomField3");

                try
                {
                    if (srcWiType == "Bug")
                    {
                        newWi.Fields["Microsoft.VSTS.TCM.ReproSteps"].Value = dr["ReproSteps"].ToString();
                    }
                    else
                    {
                        newWi.Fields["System.Description"].Value = dr["ReproSteps"].ToString();
                    }
                }
                catch (History.FieldDefinitionNotExistException)
                {
                    LogMessage(string.Format("               Failed to set field: ReproSteps for workItem: {0} type: {1}", workItemId, newWi.Title));
                }

                SetDecimalWorkItemField(dr, newWi, workItemId, "Remaining Work");
                SetDecimalWorkItemField(dr, newWi, workItemId, "Original Estimate");
                SetDecimalWorkItemField(dr, newWi, workItemId, "Completed Work");
                SetDecimalWorkItemField(dr, newWi, workItemId, "Stack Rank");

               var state = dr["State"].ToString();
               bool secondSaveNeeded;
                if (srcWiType == "Test Case")
                {
                    secondSaveNeeded = state != "Design";
                }
                else
                {
                    secondSaveNeeded = state != "Active";
                }

                // Get Tags by using Work Item ID  and adding to VSTS
                var workitemtags = GetWorkItemTags((int) dr["ID"]);
                var joinedTags = string.Join(";", workitemtags);
                newWi.Fields["Tags"].Value = joinedTags;
                LogMessage("               Work item field reading complete.");


                // Get Revisions by using work item ID and adding to VSTS
                var workitemHistory = GetWorkItemrevisions((int) dr["ID"]);
                var historyCount = 0;
                // Adding all Revisions one by one.
                foreach (var history in workitemHistory)
                {
                    LogMessage(string.Format("               Processing Work item history item {0} of {1}.", 
                        ++historyCount, workitemHistory.Count));
                    newWi.History = FormatHistoryItem(history);

                    try
                    {
                        newWi.Save();
                        saveCount = ChangeStateIfSaved(saveCount, secondSaveNeeded, state, newWi, dr["AssignedTo"].ToString());
                    }
                    catch (Exception)
                    {
                        LogMessage("Failed saving work item history");
                    }
                }
                LogMessage("               Work item history processing complete.");

                var workItemLinks = GetWorkItemLinks((int) dr["ID"]);
                var linkCount = 0;

                // Adding all Link items update one by one.
                foreach (var linkedworkitem in workItemLinks)
                {
                    LogMessage(string.Format("               Processing Work item links {0} of {1}.",
                        ++linkCount, workItemLinks.Count));
                    var wiUrl = hyperlinkService.GetWorkItemEditorUrl(int.Parse(linkedworkitem.Value)).ToString();
                    newWi.History = ("The source work item  " + workItemId + "  was linked (" + linkedworkitem.Key +
                                     ") to source work item " + " <a target='_blank' href='" + wiUrl + "'>" + linkedworkitem.Value +
                                     "</a>");
                    try
                    {
                        newWi.Save();
                        saveCount = ChangeStateIfSaved(saveCount, secondSaveNeeded, state, newWi, dr["AssignedTo"].ToString());
                    }
                    catch (Exception)
                    {
                        LogMessage("Failed saving work item links");
                    }
                }
                LogMessage("               Linked work item processing complete.");

                // Reading the data is successful. But it is failing while writing AssignedTo field  while assigning the value.
                try
                {
                    newWi.Fields["System.AssignedTo"].Value = FindIdentityDisplayName(dr["AssignedTo"].ToString());
                }
                catch (Exception ex)
                {
                    LogMessage(string.Format("Failed to set field: AssignedTo for workItem: {0} type: {1} {2}", workItemId, newWi.Title, ex.Message));
                    newWi.Fields["System.AssignedTo"].Value = string.Empty;
                }
                var witUrl = hyperlinkService.GetWorkItemEditorUrl(workItemId).ToString();
                newWi.History = ("The source work item is  " + " <a target='_blank' href='" + witUrl + "'>" + workItemId + "</a>");
                try
                {
                    newWi.Save();
                }
                catch (Exception)
                {
                    try
                    {
                        newWi.Fields["System.AssignedTo"].Value = FindIdentityDisplayName(newWi.Fields["System.AssignedTo"].Value.ToString());
                        newWi.Save();
                    }
                    catch (Exception)
                    {
                        newWi.Fields["System.AssignedTo"].Value = "";
                        try
                        {
                            newWi.Save();
                        }
                        catch (Exception)
                        {
                            LogMessage("Failed saving work item assigned to and source link");
                        }
                    }
                }
                LogMessage("               Assigned to and source work item link saved.");

                saveCount = ChangeStateIfSaved(saveCount, secondSaveNeeded, state, newWi, dr["AssignedTo"].ToString());
                if (saveCount == 1 && secondSaveNeeded)
                {
                    try
                    {
                        newWi.Save();
                    }
                    catch (Exception)
                    {
                        LogMessage("Failed saving work item state move");
                    }
                }
                LogMessage(string.Format("*********** Complete processing work item: Src: {0} Dest : {1} ***********", workItemId, newWi.Id));
                LogMessage("=================================================================================================");
            }
            LogMessage(string.Format("*********** Migration successfully Completed for work items count : {0} ***********", workItemCount));
        }

        private static string FormatHistoryItem(KeyValuePair<string, string> kvp)
        {
            return string.Format("[{0}]: {1}", kvp.Key, kvp.Value);
        }

        private static string FindIdentityDisplayName(string value)
        {
            if (value.Contains("Not Yet Assigned"))
            {
                value = string.Empty;
                return value;
            }
            var workItemIdentity = _tfsIdentityManagementService.ReadIdentity(IdentitySearchFactor.DisplayName, value, 
                MembershipQuery.Direct, ReadIdentityOptions.ExtendedProperties);

            var emailAddress1 = workItemIdentity.GetAttribute("Mail", null);
            var alias = workItemIdentity.UniqueName.Substring(workItemIdentity.UniqueName.IndexOf('\\')).TrimStart('\\');
            var emailAddress = alias + _vstsAadDomainName;

            var workItemIdentity2 = _vstsIdentityManagementService.ReadIdentity(IdentitySearchFactor.MailAddress, emailAddress, 
                MembershipQuery.Direct, ReadIdentityOptions.ExtendedProperties);

            LogMessage(string.Format("               User {0} resolved to email address: {1}, newDisplayName: {2}, {3}, {4}",
                value, emailAddress, emailAddress1, workItemIdentity.DisplayName, 
                workItemIdentity2 != null ? workItemIdentity2.DisplayName: "VSTS did not return email based ID"));

            return workItemIdentity2 != null ? workItemIdentity2.DisplayName : workItemIdentity.DisplayName;
        }

        private static void SetWorkItemStringValue(History.WorkItem newWi, DataRow dr, int bugId, string vstsFieldName, string tfsFieldName = null)
        {
            var tableFieldName = vstsFieldName.Replace(" ", "");
            if (!string.IsNullOrEmpty(tfsFieldName))
            {
                tableFieldName = tfsFieldName;
            }
            try
            {
                newWi.Fields[vstsFieldName].Value = dr[tableFieldName].ToString();
            }
            catch (History.FieldDefinitionNotExistException)
            {
                LogMessage(string.Format("               Failed to set field: {0} for workItem: {1} type: {2}", vstsFieldName, bugId, newWi.Title));
            }
            catch (Exception)
            {
                LogMessage(string.Format("               Failed to set field: {0} for workItem: {1} type: {2}", vstsFieldName, bugId, newWi.Title));
            }
        }

        private static void SetDecimalWorkItemField(DataRow dr, History.WorkItem newWi, int bugId, string vstsFieldName, int defaultValue = 0, string tfsFieldName = null)
        {
            var tableFieldName = vstsFieldName.Replace(" ", "");
            
            if (!string.IsNullOrEmpty(tfsFieldName))
            {
                tableFieldName = tfsFieldName;
            }
            try
            {
                if (string.IsNullOrEmpty(dr[tableFieldName].ToString()))
                {
                    newWi.Fields[vstsFieldName].Value = defaultValue;
                }
                else
                {
                    newWi.Fields[vstsFieldName].Value = dr[tableFieldName].ToString();
                }
            }
            catch (History.FieldDefinitionNotExistException ex)
            {
                LogMessage(string.Format("               Failed to set field: {0} for workItem: {1} type: {2} {3}", vstsFieldName, bugId, newWi.Title,
                    ex.Message));
            }
            catch (Exception ex)
            {
                LogMessage(string.Format("               Failed to set field: {0} for workItem: {1} type: {2} {3}", vstsFieldName, bugId, newWi.Title,
                    ex.Message));
                newWi.Fields[vstsFieldName].Value = defaultValue;
            }
        }

        private static int ChangeStateIfSaved(int saveCount, bool secondSaveNeeded, string state, History.WorkItem newWi, string assignedTo)
        {
            saveCount++;
            if (saveCount == 1)
            {
                if (secondSaveNeeded)
                {
                    if ((newWi.Type.Name == "Test Case" && state == "Ready")
                        || (newWi.Type.Name == "Bug" && state == "Resolved")
                        || (newWi.Type.Name == "User Story" && state == "Resolved"))
                    {
                        newWi.State = state;
                        newWi.Fields["Assigned To"].Value = FindIdentityDisplayName(assignedTo);
                    }
                    else if (newWi.Type.Name == "Task" && state == "Resolved")
                    {
                        newWi.State = "Closed";
                    }
                }
            }
            return saveCount;
        }

        private static DataTable GetWorkItemData()
        {
            // List of fields that you want to migrate from source TFS
            // To Add a new field to read from your source server, add it here, read it from the workitem and add it to the datarows.
            // See example of Custom Field here below.
            var stringFields = new string[]
            {
                "Title", // Field present in all work item types
                "AreaPath", 
                "IterationPath",
                "CreatedBy", 
                "Description",
                "AssignedTo",
                "Priority",
                "RandomField4", // Custom field
                "ReproSteps",
                "State",
                "RandomField", // Custom Field
                "WorkItemType",
                "RemainingWork",
                "CompletedWork",
                "OriginalEstimate",
                "StackRank",
                "AutomationStatus", // Work item type specific field
                "Steps",
                "RandomField2", // Another custom field
                "RandomField3" // Another custom field
            };

            var workitemFieldsTable = new DataTable();
            var col1 = new DataColumn("ID", typeof (int))
            {
                Unique = true,
                AllowDBNull = false
            };
            workitemFieldsTable.Columns.Add(col1);

            foreach (var stringField in stringFields)
            {
                var col2 = new DataColumn(stringField, typeof(string));
                workitemFieldsTable.Columns.Add(col2);
            }
            workitemFieldsTable.PrimaryKey = new[] { workitemFieldsTable.Columns["ID"] };

            var item = _tfsWorkItemStore.GetQueryDefinition(_queryId);
            if (item == null)
            {
                LogMessage(string.Format("Query with id: {0} not found", _queryId));
                return null;
            }
            if (!item.Name.Equals(_queryName))
            {
                LogMessage(string.Format("Query with id: {0} has name {1}, but it does not match with expected query name {2}",
                    _queryId, item.Name, _queryName));
                return null;
            }
            LogMessage(string.Format("Query with id: {0} found", item.Name));
            var variables = new Dictionary<string, string>
            {
                {"project", item.Project.Name}
            };

            // Run a query.
            var queryResults = _tfsWorkItemStore.Query(item.QueryText, variables);

            LogMessage(string.Format("Query run: {0}", item.QueryText));
            LogMessage(string.Format("Ran work item query and retrieved results: {0}", queryResults.Count));

            foreach (History.WorkItem workItem in queryResults)
            {
                var id = workItem.Id;
                LogMessage(string.Format("=========== Reading work item: {0} ===========", id));
                var title = workItem.Title;
                var areaPath = workItem.AreaPath;
                var iterationpath = workItem.IterationPath;
                var createdBy = workItem.CreatedBy;
                var description = workItem.Description;
                var wit = workItem.Type.Name;
                var assignedTo = workItem.Fields["Assigned To"].Value.ToString();
                var state = workItem.Fields["System.State"].Value.ToString();

                var customField2 = ReadWorkItemStringField(workItem, "RandomField2");
                var priority = ReadWorkItemStringField(workItem, "Priority");
                var customField1 = ReadWorkItemStringField(workItem, "RandomField1");
                var customField4 = ReadWorkItemStringField(workItem, "RandomField4");
                var reproSteps = ReadWorkItemStringField(workItem, "Microsoft.VSTS.TCM.ReproSteps");
                var tcmSteps = ReadWorkItemStringField(workItem, "Microsoft.VSTS.TCM.Steps");
                var tcmAutomationStatus = ReadWorkItemStringField(workItem, "Microsoft.VSTS.TCM.AutomationStatus");
                var remainingWork = ReadWorkItemStringField(workItem, "Remaining Work");
                var completedWork = ReadWorkItemStringField(workItem, "Completed Work");
                var originalEstimate = ReadWorkItemStringField(workItem, "Original Estimate");
                var stackRank = ReadWorkItemStringField(workItem, "Stack Rank");
                var customField3 = ReadWorkItemStringField(workItem, "RandomField3");


                workitemFieldsTable.Rows.Add(id, title, areaPath, iterationpath, createdBy,
                    description, assignedTo, priority, customField4, reproSteps, state, customField1,
                    wit, remainingWork, completedWork, originalEstimate, stackRank,
                    tcmAutomationStatus, tcmSteps, customField2, customField3);
                LogMessage(string.Format("*********** Complete reading work item: {0} ***********", id));
            }

            return workitemFieldsTable;
        }

        private static string ReadWorkItemStringField(History.WorkItem workItem, string fieldName)
        {
            var fieldValue = string.Empty;
            try
            {
                fieldValue = workItem.Fields[fieldName].Value.ToString();
            }
            catch (History.FieldDefinitionNotExistException)
            {
                LogMessage(string.Format("               Field does not exist: {0} for workItem: {1} type: {2}", fieldName, workItem.Id,
                    workItem.Type.Name));
                fieldValue = string.Empty;
            }
            catch (Exception)
            {
                LogMessage(string.Format("               Exception when setting field: {0} for workItem: {1} type: {2}", fieldName, workItem.Id,
                    workItem.Type.Name));
            }
            return fieldValue;
        }

        private static void LogMessage(string message)
        {
            Trace.WriteLine(message);
        }

        // Get All Area Paths
        private static DataTable GetAllAreaPaths()
        {
            var dtAreas = new DataTable();

            foreach (History.Project p in _tfsWorkItemStore.Projects)
            {
                // Check for user given project.
                if (p.Name == _tfsProjectNameToVerify)
                {
                    // Create datatable columns.
                    dtAreas.Columns.Add("Path", typeof(string));

                    // Check for all area paths.
                    foreach (History.Node area in p.AreaRootNodes)
                    {
                        var drAreas = dtAreas.NewRow();
                        drAreas["Path"] = area.Path;
                        dtAreas.Rows.Add(drAreas);
                        RecursiveAddPaths(area, dtAreas, drAreas, area.Path);
                    }
                }
            }
            // Return data table.
            return dtAreas;
        }

        private static NodeInfo AddCssPathNode(DataRow dataRow, string type)
        {
            var elementPath = dataRow["Path"].ToString();
            var rootPath = string.Format("\\{0}\\{1}", _vstsProjectName, type);
            var newNodePath = rootPath + elementPath;
            try
            {
                var retVal = _vstsCss.GetNodeFromPath(newNodePath);
                if (retVal != null)
                {
                    // return null; //already exists
                }
            }
            catch (Exception ex)
            {
                if (ex.Message.Contains("The following node does not exist"))
                {
                    //just means that this path is not exist and we can continue.
                }
                else
                {
                    throw ex;
                }
            }

            var backSlashIndex = elementPath.LastIndexOf("\\");
            var newpathname = elementPath.Substring(backSlashIndex + 1);
            var newPath = (backSlashIndex == 0 ? string.Empty : elementPath.Substring(0, backSlashIndex));
            var pathRoot = rootPath + newPath;
            NodeInfo previousPath;
            try
            {
                previousPath = _vstsCss.GetNodeFromPath(pathRoot);
            }
            catch (Exception ex)
            {
                if (ex.Message.Contains("Invalid path."))
                {
                    //just means that this path is not exist and we can continue.
                    previousPath = null;
                }
                else
                {
                    throw ex;
                }
            }
            if (previousPath == null)
            {
                //call this method to create the parent paths.
                //throw new Exception("Parent must be created already");
                var previousRow = dataRow.Table.NewRow();
                previousRow["Path"] = newPath;
                previousPath = AddCssPathNode(previousRow, type);
            }
            var newPathUri = _vstsCss.CreateNode(newpathname, previousPath.Uri);
            if (type == "Iteration")
            {
                var vsoCss4 = _vstsTeamProjectCollection.GetService<ICommonStructureService4>();
                if (dataRow["StartDate"].ToString() != "" && dataRow["FinishDate"].ToString() != "")
                {
                    vsoCss4.SetIterationDates(newPathUri, (DateTime)dataRow["StartDate"], (DateTime)dataRow["FinishDate"]);
                }
            }
            return _vstsCss.GetNode(newPathUri);
        }

        // Get All Iteration Paths
        private static DataTable GetAllIterations()
        {
            var dtIterations = new DataTable();
            foreach (History.Project p in _tfsWorkItemStore.Projects)
            {
                // Check for user given project.
                if (p.Name == _tfsProjectNameToVerify)
                {
                    var structures = _tfsCss.ListStructures(p.Uri.AbsoluteUri);
                    var iterations = structures.FirstOrDefault(n => n.StructureType.Equals("ProjectLifecycle"));
                    var iterationsTree = _tfsCss.GetNodesXml(new[] { iterations.Uri }, true);

                    // Create datatable columns.
                    dtIterations.Columns.Add("Name", typeof(string));
                    dtIterations.Columns.Add("Path", typeof(string));
                    dtIterations.Columns.Add("StartDate", typeof(DateTime));
                    dtIterations.Columns.Add("FinishDate", typeof(DateTime));
                    BuildIterationTree(iterationsTree.ChildNodes, dtIterations, string.Empty);
                }
            }
            // Return data table.
            return dtIterations;
        }

        private static void BuildIterationTree(XmlNodeList subItems, DataTable dtIterations, string parentPath)
        {
            foreach (XmlNode node in subItems)
            {
                var path = parentPath;
                if (node.Name != "Children")
                {
                    var nodeId = GetNodeId(node.OuterXml);

                    var nodeInfo = _tfsCss.GetNode(nodeId);
                    var dr = dtIterations.NewRow();
                    dr["Name"] = nodeInfo.Name;
                    path = parentPath + @"\" + nodeInfo.Name;
                    if (parentPath != string.Empty)
                    {
                        dr["Path"] = path;
                        if (nodeInfo.StartDate.HasValue)
                        {
                            dr["StartDate"] = nodeInfo.StartDate;
                        }
                        if (nodeInfo.FinishDate.HasValue)
                        {
                            dr["FinishDate"] = nodeInfo.FinishDate;
                        }
                        dtIterations.Rows.Add(dr);
                    }
                }
                if (node.ChildNodes.Count > 0)
                {
                    BuildIterationTree(node.ChildNodes, dtIterations, path);
                }
            }
        }

        private static string GetNodeId(string xml)
        {
            var first = "NodeID=\"";
            var start = xml.IndexOf(first) + first.Length;
            var end = xml.IndexOf("\"", start);
            return xml.Substring(start, (end - start));
        }

        // Get Link Item details for Each Work Id
        private static List<KeyValuePair<string, string>> GetWorkItemLinks(int workitemId)
        {
            var workItemLinks = new List<KeyValuePair<string, string>>();

            var teamProjectCollection = new TfsTeamProjectCollection(new Uri(_tfsCollectionUri));
            var workItemStore = (History.WorkItemStore)teamProjectCollection.GetService(typeof(History.WorkItemStore));
            var workItem = workItemStore.GetWorkItem(workitemId);
            var links = workItem.WorkItemLinks;
            foreach (History.WorkItemLink workLink in links)
            {
                switch (workLink.LinkTypeEnd.Name)
                {
                    case "Parent":
                        workItemLinks.Add(new KeyValuePair<string, string>(workLink.LinkTypeEnd.Name, workLink.TargetId.ToString()));
                        break;

                    case "Child":
                        workItemLinks.Add(new KeyValuePair<string, string>(workLink.LinkTypeEnd.Name, workLink.TargetId.ToString()));
                        break;

                    case "Related":
                        workItemLinks.Add(new KeyValuePair<string, string>(workLink.LinkTypeEnd.Name, workLink.TargetId.ToString()));
                        break;

                    default:
                        break;
                }
            }
            return workItemLinks;

        }

        // Reading revisions for each Work Id
        private static List<KeyValuePair<string, string>> GetWorkItemrevisions(int workitemId)
        {
            var workitemHistory = new List<KeyValuePair<string, string>>();

            var teamProjectCollection = new TfsTeamProjectCollection(new Uri(_tfsCollectionUri));
            var workItemStore = (History.WorkItemStore)teamProjectCollection.GetService(typeof(History.WorkItemStore));
            var workItem = workItemStore.GetWorkItem(workitemId);

            foreach (History.Revision revision in workItem.Revisions)
            {
                    // Get work item revisions by History
                    foreach (History.Field field in workItem.Fields)
                    {
                        if (field.Name == "History" && !string.IsNullOrEmpty(revision.Fields[field.Name].Value.ToString()))
                        {
                            workitemHistory.Add(new KeyValuePair<string, string>(revision.GetTagLine(), revision.Fields[field.Name].Value.ToString()));

                        }
                    }
            }
            return workitemHistory;
        }

        // Reading Tags of each Work item Id
        private static List<string> GetWorkItemTags(int workitemId)
        {
            var teamProjectCollection = new TfsTeamProjectCollection(new Uri(_tfsCollectionUri));
            var workItemStore = (History.WorkItemStore)teamProjectCollection.GetService(typeof(History.WorkItemStore));
            var workItem = workItemStore.GetWorkItem(workitemId);
            var tags= workItem.Tags.Split(';');

            return tags.ToList();
        }

        private static void RecursiveAddPaths(History.Node node, DataTable dt, DataRow dr, string parentPath)
        {
            foreach (History.Node item in node.ChildNodes)
            {
                var path = parentPath + "\\" + item.Name;
                dr = dt.NewRow();
                dr["Path"] = item.Path;
                dt.Rows.Add(dr);
                if (item.HasChildNodes)
                {
                    RecursiveAddPaths(item, dt, dr, path);
                }
            }
        }
    }
}