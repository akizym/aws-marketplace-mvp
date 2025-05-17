using System.Net.Http.Json;
using System.Text.Json.Nodes;

public class OrderIntegrationTests
{
    private const string BaseUrl = "https://0ke79cu2u2.execute-api.eu-central-1.amazonaws.com/prod";

    [Fact]
    public async Task CreateOrder_And_PaymentWebhook_Succeeds()
    {
        using var client = new HttpClient();
        // 1. Create Order
        var orderRequest = new
        {
            ItemIds = new List<string> { "item1", "item2" },
            Currency = "EUR",
            TotalAmount = 1000,
            CustomerEmail = "akizym@outlook.com",
            PaymentType = "CreditCard"
        };
        
        var orderResponse = await client.PostAsJsonAsync($"{BaseUrl}/orders", orderRequest);
        orderResponse.EnsureSuccessStatusCode();
        
        var responseJson = await orderResponse.Content.ReadAsStringAsync();

        // Parse to JsonObject
        var obj = JsonNode.Parse(responseJson)?.AsObject();

        string orderId = obj?["OrderId"]?.GetValue<string>();
        string paymentId = obj?["paymentId"]?.GetValue<string>();

        // 2. Send Payment Webhook
        var webhookRequest = new
        {
            PaymentId = paymentId,
            OrderId = orderId,
            Status = "PaymentSucceeded",
            Provider = "TestProvider",
            ReceiptUrl = "https://receipt.url/example",
            TransactionId = "txn_12345",
            CustomerEmail = "akizym@outlook.com",
        };

        var webhookResponse = await client.PostAsJsonAsync($"{BaseUrl}/payment-webhook", webhookRequest);
        webhookResponse.EnsureSuccessStatusCode();
    }
}