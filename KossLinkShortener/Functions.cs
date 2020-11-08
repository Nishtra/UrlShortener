using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.Azure.Cosmos.Table;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using System.Linq;

namespace KossLinkShortener
{
    public static class Functions
    {
        [FunctionName("ShortenUrl")]
        public static async Task<IActionResult> ShortenUrl(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", "post", Route = null)] HttpRequest req,
            [Table("urls", "1", "KEY", Take = 1)] UrlKey urlKey,
            [Table("urls")] CloudTable inputTable,
            ILogger log)
        {
            log.LogInformation("ShortenUrl function processed a request.");

            // get href from request
            string href = req.Query["href"];

            string requestBody = await new StreamReader(req.Body).ReadToEndAsync();
            dynamic data = JsonConvert.DeserializeObject(requestBody);
            href = href ?? data?.href;

            if (String.IsNullOrWhiteSpace(href))
                return new BadRequestObjectResult("Href parameter is missing");

            // create a "key" for generating unique values if it doesn't exist
            if (urlKey == null)
            {
                urlKey = new UrlKey { PartitionKey = "1", RowKey = "KEY", Id = 1024 };
                var tableInsertUrlKey = TableOperation.Insert(urlKey);
                await inputTable.ExecuteAsync(tableInsertUrlKey);
            }

            // query table for the passed URL
            // and return found short url if it exists
            href = href.ToLower();
            var query = new TableQuery<UrlData>()
                .Where(TableQuery.GenerateFilterCondition(nameof(UrlData.Url), QueryComparisons.Equal, href));
            var foundUrlData = inputTable.ExecuteQuery<UrlData>(query);
            if (foundUrlData.Count() > 0)
            {
                var responseObj = foundUrlData.First();
                var res = responseObj.RowKey;
                return new OkObjectResult(res);
            }

            // generate a new short url
            var idx = urlKey.Id;
            var s = string.Empty;
            var alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ";
            while (idx > 0)
            {
                s += alphabet[idx % alphabet.Length];
                idx /= alphabet.Length;
            }
            s = String.Join(string.Empty, s.Reverse());
            var urlData = new UrlData
            {
                PartitionKey = $"{s[0]}",
                RowKey = s,
                Url = href
            };
            urlKey.Id++;
            var operation = TableOperation.Replace(urlKey);
            await inputTable.ExecuteAsync(operation);
            operation = TableOperation.Insert(urlData);
            await inputTable.ExecuteAsync(operation);

            return new OkObjectResult(urlData.RowKey);
            //return urlData.Url != null
            //    ? (ActionResult)new OkObjectResult($"You have passed URL {urlData.Url}. Shortened URL is {urlData.RowKey}")
            //    : new BadRequestObjectResult("Please pass a href on the query string or in the request body");
        }


        [FunctionName("Redirect")]
        public static async Task<IActionResult> Redirect(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", "post", Route = "redirect/{shortUrl}")] HttpRequest req,
            string shortUrl,
            [Table("urls", "1", "KEY", Take = 1)] UrlKey urlKey,
            [Table("urls")] CloudTable inputTable,
            [Queue("redirects")] IAsyncCollector<string> redirectsQueue,
            ILogger log)
        {
            log.LogInformation("Redirect function processed a request.");

            if (String.IsNullOrWhiteSpace(shortUrl))
                return new BadRequestObjectResult("Short url parameter is missing");

            shortUrl = shortUrl.ToUpper();
            var retrieveUrlDataOperation = TableOperation.Retrieve<UrlData>($"{shortUrl[0]}", shortUrl);
            var retrievalResult = await inputTable.ExecuteAsync(retrieveUrlDataOperation);
            
            string redirectUrl = "https://www.google.com/";
            if (retrievalResult != null && retrievalResult.Result is UrlData urlData)
            {
                redirectUrl = urlData.Url;
                await redirectsQueue.AddAsync(urlData.RowKey);
            }

            return new RedirectResult(redirectUrl);
        }


        [FunctionName("ProcessRedirectsQueue")]
        public static async Task ProcessRedirectsQueue(
            [QueueTrigger("redirects")] string shortUrl,
            [Table("urls")] CloudTable inputTable,
            ILogger log)
        {
            var retrieveUrlData = TableOperation.Retrieve<UrlData>($"{shortUrl[0]}", shortUrl);
            var retrievalResult = await inputTable.ExecuteAsync(retrieveUrlData);

            if (retrievalResult != null && retrievalResult.Result is UrlData urlData)
            {
                urlData.RequestCount++;
                var replaceOperation = TableOperation.Replace(urlData);
                await inputTable.ExecuteAsync(replaceOperation);
            }
        }
    }
}
