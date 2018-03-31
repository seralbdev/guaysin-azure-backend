
using System;
using System.Linq;
using System.IO;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.Azure.WebJobs.Host;
using Newtonsoft.Json;

using Microsoft.Azure;
using Microsoft.WindowsAzure.Storage;
using Microsoft.WindowsAzure.Storage.Table;
using System.Threading.Tasks;

using System.Net.Http;
using System.Configuration;
using System.Collections.Generic;
using System.Net.Http.Formatting;

namespace seralbdev.com
{
    class Site : TableEntity
    {        
        public Site() { }

        public string SiteName { get; set; }
        public string SiteUrl { get; set; }
        public string SiteUser { get; set; }
        public string SitePassword { get; set; }             
    }

    class User : TableEntity
    {        
        public User() { }

        public string EMail { get; set; }
        public string MasterS { get; set; }
        public string Token { get; set; }            
    }    

    public static class GuaysinBackendV1
    {
        private const string HEADER_TOKEN = "Token";         
        private const string HEADER_MASTERSECRET = "MasterS";      

        private static CloudTableClient GetTableClient(String cs)
        {
            // Retrieve the storage account from the connection string.
            CloudStorageAccount storageAccount = CloudStorageAccount.Parse(cs);

            // Create the table client.
            CloudTableClient tableClient = storageAccount.CreateCloudTableClient();

            return tableClient;           
        }

        private static CloudTable GetUsersTable(CloudTableClient tableClient)
        {
            return tableClient.GetTableReference("Users");    
        }

        private static CloudTable GetSitesTable(CloudTableClient tableClient)
        {
            return tableClient.GetTableReference("Sites");    
        }        

        private static async Task<String> GetUserIdFromToken(CloudTable usersTable,String token)
        {
            String userid = String.Empty;

            // Create the table query
            TableQuery<User> query = new TableQuery<User>().Where(
                TableQuery.GenerateFilterCondition("Token", QueryComparisons.Equal, token));

            // Initialize the continuation token to null to start from the beginning of the table.
            TableContinuationToken continuationToken = null;

            // Retrieve a segment (up to 1,000 entities).
            TableQuerySegment<User> tableQueryResult =
                await  usersTable.ExecuteQuerySegmentedAsync(query,continuationToken);

            if(tableQueryResult.Results.Count>0){
                userid = tableQueryResult.Results[0].PartitionKey;
            }     

            return userid;
        }

        private static async Task<List<Site>> GetAllUserSites(CloudTable sitesTable,String userId)
        {
            var siteList = new List<Site>();

            // Initialize the continuation token to null to start from the beginning of the table.
            TableContinuationToken continuationToken = null;

            // Create the table query
            /*TableQuery<Site> query = new TableQuery<Site>().Where(
                TableQuery.CombineFilters(
                    TableQuery.GenerateFilterCondition("PartitionKey", QueryComparisons.Equal, userId),
                    TableOperators.And,
                    TableQuery.GenerateFilterCondition("RowKey", QueryComparisons.Equal, "0")));*/

            TableQuery<Site> query = new TableQuery<Site>().Where(
                TableQuery.GenerateFilterCondition("PartitionKey", QueryComparisons.Equal, userId));                  

            do
            {
                // Retrieve a segment (up to 1,000 entities).
                TableQuerySegment<Site> tableQueryResult =
                    await sitesTable.ExecuteQuerySegmentedAsync(query, continuationToken);

                //Update list with this segment
                if(tableQueryResult.Results.Count>0)
                    siteList.AddRange(tableQueryResult.Results);

                // Assign the new continuation token to tell the service where to
                // continue on the next iteration (or null if it has reached the end).
                continuationToken = tableQueryResult.ContinuationToken;

                // Loop until a null continuation token is received, indicating the end of the table.
            } while(continuationToken != null);

      
            return siteList;
        }

        private static async Task DeleteSites(CloudTable sitesTable,List<Site> sites)
        {
            if(!sites.Any())
                return;
                
            //Create the batch operation
            TableBatchOperation batchOperation = new TableBatchOperation();

            //Load each delete operation into the batch
            foreach(var site in sites)
                batchOperation.Delete(site);

            //Execute batch
            await sitesTable.ExecuteBatchAsync(batchOperation);
        }

        private static async Task DeleteAllSitesFromUser(CloudTable sitesTable,String userId)
        {
            var userSites = await GetAllUserSites(sitesTable,userId);
            await DeleteSites(sitesTable,userSites);
        }

        private static async Task ReplaceUserMasterSecret(CloudTable userTable,String userId,String masterS)
        {
            var entity = new DynamicTableEntity(userId,"0");
            entity.ETag = "*";
            entity.Properties.Add("MasterS",new EntityProperty(masterS));
            var mergeOperation = TableOperation.Merge(entity);
            await userTable.ExecuteAsync(mergeOperation);            
        }

        private static async Task InsertSites(CloudTable sitesTable,List<Site> sites,String userId)
        {
            //Create the batch operation
            TableBatchOperation batchOperation = new TableBatchOperation();

            //Load each delete operation into the batch
            int row=0;
            foreach(var site in sites)
            {
                site.PartitionKey = userId;
                site.RowKey = row.ToString();
                row++;
                batchOperation.Insert(site);
            }

            //Execute batch
            await sitesTable.ExecuteBatchAsync(batchOperation);            
        }        

        [FunctionName("PushSites")]
        public static async Task<HttpResponseMessage> PushSitesRun([HttpTrigger(AuthorizationLevel.Function, "post", Route = null)]HttpRequestMessage req, TraceWriter log)
        {
            log.Info("Entering PushSites");

            try
            {
                // Find UserId & Token in header
                if(!req.Headers.Contains(HEADER_TOKEN) || !req.Headers.Contains(HEADER_MASTERSECRET))
                    return req.CreateResponse(System.Net.HttpStatusCode.BadRequest);

                var token = req.Headers.GetValues(HEADER_TOKEN).First();
                var masterS = req.Headers.GetValues(HEADER_MASTERSECRET).First();

                //Get table references
                var tableClient = GetTableClient(System.Environment.GetEnvironmentVariable("AzureWebJobsStorage", EnvironmentVariableTarget.Process));
                var usersTable = GetUsersTable(tableClient);
                var sitesTable = GetSitesTable(tableClient);

                //Validate Token and get UserId
                var userId = await GetUserIdFromToken(usersTable,token);
                if(userId.Equals(String.Empty))
                    return req.CreateResponse(System.Net.HttpStatusCode.Unauthorized);

                //Get Site info from JSON payload in body
                dynamic body = await req.Content.ReadAsStringAsync();                
                var siteList = JsonConvert.DeserializeObject<List<Site>>(body as string);

                //Check if there are sites in the request
                if(siteList.Count == 0)
                    return req.CreateResponse(System.Net.HttpStatusCode.NoContent);

                //InserOrUpdate master secret
                await ReplaceUserMasterSecret(usersTable,userId,masterS);

                //Delete all current user sites
                await DeleteAllSitesFromUser(sitesTable,userId);

                //Insert new list of sites
                await InsertSites(sitesTable,siteList,userId);
                
                return req.CreateResponse(System.Net.HttpStatusCode.OK);          
            }
            catch(Exception ex){
                log.Error(ex.Message);
                return req.CreateResponse(System.Net.HttpStatusCode.InternalServerError,ex.Message);
            }
        }

        [FunctionName("GetSites")]
        public static async Task<HttpResponseMessage> GetSitesRun([HttpTrigger(AuthorizationLevel.Function, "get", Route = null)]HttpRequestMessage req, TraceWriter log)
        {
            log.Info("Entering PushSites");

            try
            {
                // Find UserId & Token in header
                if(!req.Headers.Contains(HEADER_TOKEN) || !req.Headers.Contains(HEADER_MASTERSECRET))
                    return req.CreateResponse(System.Net.HttpStatusCode.BadRequest);

                var token = req.Headers.GetValues(HEADER_TOKEN).First();
                var masterS = req.Headers.GetValues(HEADER_MASTERSECRET).First();

                //Get table references
                var tableClient = GetTableClient(System.Environment.GetEnvironmentVariable("AzureWebJobsStorage", EnvironmentVariableTarget.Process));
                var usersTable = GetUsersTable(tableClient);
                var sitesTable = GetSitesTable(tableClient);

                //Validate Token and get UserId
                var userId = await GetUserIdFromToken(usersTable,token);
                if(userId.Equals(String.Empty))
                    return req.CreateResponse(System.Net.HttpStatusCode.Unauthorized);

                //Get all sites from user
                var siteList = await GetAllUserSites(sitesTable,userId);  
                var payload = new StringContent(JsonConvert.SerializeObject(siteList),System.Text.Encoding.UTF8, "application/json");
                
                var rsp = req.CreateResponse();
                rsp.Content = new ObjectContent<List<Site>>(siteList, new JsonMediaTypeFormatter());
                rsp.StatusCode = System.Net.HttpStatusCode.OK;
                return rsp;
            }
            catch(Exception ex)
            { 
                log.Error(ex.Message);
                return req.CreateResponse(System.Net.HttpStatusCode.InternalServerError,ex.Message);
            }
        }
    }
    
}
