### vstsmigration
Custom tool to migrate from TFS to VSTS
This tool migrates a select set of fields from on-premise TFS to VSTS

### What TFS -> VSTS tool migrates?
This tool migrated all work items matching this query:
*	Status = Active or Resolved
Type = Bug, User Story or Task
*	All Ready/ Design Test Cases, Test plans were migrated.
 Create a query in the source server matching above the criteria.

### What didn't get migrated?
*	Work items with Status = Closed were not moved
*	Queries were not moved
*	Test Suites were not moved

### What parts of work items did not get migrated?
Several fields in the work items won't be migrated either intentionally or because of cost. These include:

*	Attachments - Workaround: Just click the link to the original work item in the comments.
*	Created By - Workaround: Created a custom field called "Original Created By" field, which will contain the Created By migrated from TFS.
*	History - The comments in the discussion from TFS will be migrated into the new bug, but the full history of updates will not be migrated.
*   Work item Links - Adds a comment in the history informing that links exist (if they do) and linking to the old TFS work item.
*   Commit Links available in TFS and are not migrated.

In general, if there's anything missing in the work item in VSTS, just click the link in the first comment in the work item to navigate back to the original work item.

### Usage
Usage:
```sh
TFS-VSTS-Migrate Source_Tfs_Collection_Url Destination_Tfs_Collection_Url Query_Id Query_Name Setup_Migration_Flag Migrate_Work_Items_Flag Source_TeamProject_Name Destination_TeamProject_Name Destination_Alternate_Credentials_UserId Destination_Alternate_Credentials_Password
```

NOTE: Be careful when setting flag Setup_Migration_Flag to true. This will CREATE area paths, iteration paths using the user credentials.
NOTE: Be careful when setting flag Migrate_Work_Items_Flag to true. This will CREATE work items using the user credentials.

```sh
Example: TFS-VSTS-Migrate.exe "http://fabrikam:8080/tfs/Contoso_Collection" "https://fabrikam-vsts.visualstudio.com/DefaultCollection" "c24845e9-b5dh-4a95-cl18-8224dadabf3" "Source Server Query Name" false true Contoso-Project Contoso dummyuser dummyPassword
```