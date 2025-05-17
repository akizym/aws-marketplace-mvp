using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.Core;
using Amazon.EventBridge;
using Amazon.EventBridge.Model;

// Lambda serializer
[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]

namespace ProcessPayment
{
    public class PaymentWebhookRequest
    {
        public string PaymentId { get; set; } = string.Empty;
        public string OrderId { get; set; } = string.Empty;
        public string Status { get; set; }
        
        public string Provider { get; set; } 
        
        public string ReceiptUrl { get; set; }
        
        public string TransactionId { get; set; } = string.Empty;
        
        public string CustomerEmail { get; set; }
    }

    public class Function
    {
        private static readonly string paymentsTableName = Environment.GetEnvironmentVariable("PAYMENTS_TABLE")!;
        private static readonly string ordersTableName   = Environment.GetEnvironmentVariable("ORDERS_TABLE")!;
        private static readonly string eventBusName      = Environment.GetEnvironmentVariable("EVENT_BUS_NAME")!;
        
        private readonly AmazonDynamoDBClient ddb = new();
        private readonly AmazonEventBridgeClient eventBridge = new();

        public async Task<APIGatewayProxyResponse> FunctionHandler(
            APIGatewayProxyRequest request, ILambdaContext context)
        {
            if (string.IsNullOrEmpty(request.Body))
                return BadRequest("Missing body");

            PaymentWebhookRequest? input;
            try
            {
                input = JsonSerializer.Deserialize<PaymentWebhookRequest>(request.Body, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });
            }
            catch (Exception ex)
            {
                context.Logger.LogError($"JSON error: {ex}");
                return BadRequest("Bad JSON");
            }

            if (input is null || string.IsNullOrEmpty(input.PaymentId) || string.IsNullOrEmpty(input.OrderId))
                return BadRequest("Missing paymentId or orderId");

            // 1. Update payment status in DynamoDB
            
            var updatePaymentReq = UpdateItemRequest(paymentsTableName, new ("paymentId", input.PaymentId), input.Status);
            var updateOrderReq= UpdateItemRequest(ordersTableName, new ("orderId", input.OrderId), input.Status);
            
            var transactRequest = new TransactWriteItemsRequest
            {
                TransactItems =
                [
                    new() { Update = updatePaymentReq },
                    // Update order status
                    new() { Update = updateOrderReq }
                ]
            };
            await ddb.TransactWriteItemsAsync(transactRequest);
            
            // 3. Publish event to EventBridge
            var eventEntry = new PutEventsRequestEntry
            {
                Source = "market.payment",
                DetailType = input.Status,
                Detail = JsonSerializer.Serialize(input),
                EventBusName = eventBusName
            };

            await eventBridge.PutEventsAsync(new PutEventsRequest
            {
                Entries = [eventEntry]
            });

            return new APIGatewayProxyResponse
            {
                StatusCode = 200,
                Body = JsonSerializer.Serialize(new { message = "Payment processed" }),
                Headers = new Dictionary<string, string> { { "Content-Type", "application/json" } }
            };
        }

        private static Update UpdateItemRequest(string table, (string k, string v) pair, string status, bool allowCreate = false)
        {
            var update = new Update
            {
                TableName = table,
                Key = new Dictionary<string, AttributeValue>
                {
                    [pair.k] = new() {S = pair.v}
                },
                UpdateExpression = "SET #status = :newstatus",
                ExpressionAttributeNames = new Dictionary<string, string>
                {
                    ["#status"] = "status"
                },
                ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                {
                    [":newstatus"] = new() { S = status }
                },
                ConditionExpression = $"attribute_exists({pair.k})"
            };
            return update;
        }

        private static APIGatewayProxyResponse BadRequest(string msg) =>
            new()
            {
                StatusCode = 400,
                Body = JsonSerializer.Serialize(new { error = msg }),
                Headers = new Dictionary<string, string> { { "Content-Type", "application/json" } }
            };
    }
    

}
