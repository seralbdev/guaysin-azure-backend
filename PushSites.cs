
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

    public static class PushSites
    {
        private const string USER_ID_HEADER = "UserId";

        [FunctionName("PushSites")]
        public static async Task<HttpResponseMessage> Run([HttpTrigger(AuthorizationLevel.Function, "post", Route = null)]HttpRequestMessage req, TraceWriter log)
        {
           log.Info("Entering PushSites");

            try
            {
                // Find UserId in header
                if(!req.Headers.Contains(USER_ID_HEADER))
                    return req.CreateResponse(System.Net.HttpStatusCode.BadRequest);

                var UserId = req.Headers.GetValues(USER_ID_HEADER).First();

                //TODO : Validate UserId                

                //Get Site info from JSON payload in body
                dynamic body = await req.Content.ReadAsStringAsync();                
                var SiteData = JsonConvert.DeserializeObject<Site>(body as string);

                //Get Table reference
                var cs = System.Environment.GetEnvironmentVariable("AzureWebJobsStorage", EnvironmentVariableTarget.Process);
                //var cs = ConfigurationManager.ConnectionStrings["AzureWebJobsStorage"].ConnectionString;

                // Retrieve the storage account from the connection string.
                CloudStorageAccount storageAccount = CloudStorageAccount.Parse(cs);

                // Create the table client.
                CloudTableClient tableClient = storageAccount.CreateCloudTableClient();

                // Create the CloudTable object that represents the "people" table.
                CloudTable table = tableClient.GetTableReference("sites");

                SiteData.PartitionKey = UserId;
                SiteData.RowKey = "0";

                // Create the TableOperation object that inserts the customer entity.
                TableOperation insertOperation = TableOperation.Insert(SiteData);

                // Execute the insert operation.
                await table.ExecuteAsync(insertOperation);                

            }
            catch(Exception ex){
                log.Error(ex.Message);
            }


            return req.CreateResponse(System.Net.HttpStatusCode.OK);
        }
    }
}
