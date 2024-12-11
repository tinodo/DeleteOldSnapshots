using Azure;
using Azure.Communication.Email;
using Azure.Core;
using Azure.Identity;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace FunctionApp1
{
    public class Function1
    {
        private readonly ILogger _logger;

        public Function1(ILoggerFactory loggerFactory)
        {
            _logger = loggerFactory.CreateLogger<Function1>();
        }

        [Function("FunctionHttp")]
        public async Task Run1([HttpTrigger(AuthorizationLevel.Anonymous, ["GET", "POST"])] HttpRequestData req)
        {
            _logger.LogInformation($"C# HTTP trigger function executed at: {DateTime.Now}");
            await DoSomething();
        }

        [Function("FunctionTimer")]
        public async Task Run2([TimerTrigger("0 30 9 * * *")] TimerInfo myTimer)
        {
            _logger.LogInformation($"C# Timer trigger function executed at: {DateTime.Now}");

            if (myTimer.ScheduleStatus is not null)
            {
                _logger.LogInformation($"Next timer schedule at: {myTimer.ScheduleStatus.Next}");
            }

            await DoSomething();
        }

        public async Task DoSomething()
        {
            string toAddress = Environment.GetEnvironmentVariable("COMMUNICATION_SERVICES_TO_STRING");
            var credential = new ManagedIdentityCredential(ManagedIdentityId.SystemAssigned);
            string token = (await credential.GetTokenAsync(new TokenRequestContext(["https://management.azure.com/.default"]))).Token;
            var subscriptions = await GetSubscriptions(token);

            List<string> snapIds = [];
            foreach (var subscription in subscriptions.RootElement.GetProperty("value").EnumerateArray())
            {
                var subscriptionId = subscription.GetProperty("subscriptionId").GetString();
                var snapshots = await GetSnapshots(subscriptionId, token);
                foreach (var snapshot in snapshots.RootElement.GetProperty("value").EnumerateArray())
                {
                    if (snapshot.TryGetProperty("tags", out var tags) && tags.TryGetProperty("CreatedBy", out var cb) && "AzureBackup".Equals(cb.GetString()))
                    {
                        continue;
                    }
                    var properties = snapshot.GetProperty("properties");
                    if (properties.TryGetProperty("timeCreated", out var tc) && tc.TryGetDateTime(out var dt) && dt < DateTime.UtcNow.AddDays(-30))
                    {
                        var snapshotId = snapshot.GetProperty("id").GetString();
                        if (await DeleteSnapshot(snapshotId, token))
                        {
                            snapIds.Add(snapshotId);
                        }
                    }
                }
            }

            if (snapIds.Count > 0)
            {
                await SendEmail(toAddress, $"{snapIds.Count} Snapshot(s) Deleted", string.Join(Environment.NewLine, snapIds));
            }
            else
            {
                await SendEmail(toAddress, "No snapshots deleted", string.Empty);
            }
        }

        public async Task SendEmail(string to, string subject, string message)
        {
            string connectionString = Environment.GetEnvironmentVariable("COMMUNICATION_SERVICES_CONNECTION_STRING");
            string senderAddress = Environment.GetEnvironmentVariable("COMMUNICATION_SERVICES_FROM_STRING");
            var emailClient = new EmailClient(connectionString);
            var emailMessage = new EmailMessage(
                senderAddress,
                content: new EmailContent(subject)
                {
                    PlainText = message,
                    Html = @$"
		<html>
			<body>
				<h1>{message}</h1>
			</body>
		</html>"
                },
            recipients: new EmailRecipients([new EmailAddress(to)]));
            EmailSendOperation emailSendOperation = await emailClient.SendAsync(WaitUntil.Completed, emailMessage);
            _logger.LogInformation($"Email sent: {emailSendOperation.Id} Completed: {emailSendOperation.HasCompleted}");
        }

        private async Task<JsonDocument> GetSubscriptions(string token)
        {
            var url = "https://management.azure.com/subscriptions?api-version=2020-01-01";
            return await GetSomething(url, token);
        }

        private async Task<JsonDocument> GetSnapshots(string subscriptionId, string token)
        {
            var url = $"https://management.azure.com/subscriptions/{subscriptionId}/providers/Microsoft.Compute/snapshots?api-version=2021-04-01";
            return await GetSomething(url, token);
        }

        private async Task<JsonDocument> GetSomething(string url, string token)
        {
            var client = new HttpClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            var response = await client.GetAsync(url);
            response.EnsureSuccessStatusCode();
            var result = await response.Content.ReadFromJsonAsync<JsonDocument>();
            _logger.LogInformation($"GetSomething: {result?.RootElement.GetRawText()}");
            return result ?? throw new Exception("Could not get data.");
        }

        private async Task<bool> DeleteSnapshot(string snapshotId, string token)
        {
            var url = $"https://management.azure.com/{snapshotId}?api-version=2021-04-01";
            var client = new HttpClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            var response = await client.DeleteAsync(url);
            return response?.IsSuccessStatusCode ?? false;
        }
    }
}
